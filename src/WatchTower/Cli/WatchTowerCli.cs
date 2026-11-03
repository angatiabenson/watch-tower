using System.CommandLine;
using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;

using WatchTower.Configuration.Settings;
using WatchTower.Hosting;
using WatchTower.Portal;
using WatchTower.Ui;

namespace WatchTower.Cli;

/// <summary>
/// Command-line entry point. With no command, WatchTower runs the monitoring host (this is
/// what the Windows service executes). Management commands install/control the service.
/// </summary>
public static class WatchTowerCli
{
    public static async Task<int> RunAsync(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var output = stdout ?? Console.Out;
        var error = stderr ?? Console.Error;

        var dataDirOption = new Option<string?>("--data-dir")
        {
            Description = "Data directory for settings, database and logs. Defaults to the installed service's " +
                          $"directory, then the {WatchTowerPaths.DataDirectoryEnvironmentVariable} environment " +
                          "variable, then %ProgramData%\\WatchTower.",
            Recursive = true,
        };

        var serviceNameOption = new Option<string>("--service-name")
        {
            Description = "Windows service name (default: WatchTower). Use to run side-by-side instances.",
            Recursive = true,
            DefaultValueFactory = _ => WatchTowerHost.DefaultServiceName,
        };

        var ctx = new CliContext(dataDirOption, serviceNameOption, output);

        var root = new RootCommand(
            "WatchTower — Windows server monitoring agent. Run without a command to start the monitor.");
        root.Options.Add(dataDirOption);
        root.Options.Add(serviceNameOption);
        // The service itself always runs with an explicit --data-dir (written by `install`).
        root.SetAction(async (parse, _) => await WatchTowerHost.RunAsync(
            WatchTowerPaths.Resolve(Normalize(parse.GetValue(dataDirOption))), parse.GetValue(serviceNameOption)!));

        root.Subcommands.Add(BuildInstall(ctx));
        root.Subcommands.Add(BuildUninstall(ctx));
        root.Subcommands.Add(BuildControl(ctx, "start", "Start the WatchTower service.",
            (s, _) => s.Start(TimeSpan.FromSeconds(60))));
        root.Subcommands.Add(BuildControl(ctx, "stop", "Stop the WatchTower service.",
            (s, _) => s.Stop(TimeSpan.FromSeconds(60))));
        var delayOption = new Option<int>("--delay")
        {
            Description = "Seconds to wait before restarting (lets the caller finish responding).",
        };
        var restart = BuildControl(ctx, "restart", "Restart the WatchTower service.", (s, parse) =>
        {
            if (parse.GetValue(delayOption) is > 0 and var delay)
                Thread.Sleep(TimeSpan.FromSeconds(Math.Min(delay, 300)));
            s.Stop(TimeSpan.FromSeconds(90));
            s.Start(TimeSpan.FromSeconds(90));
        });
        restart.Options.Add(delayOption);
        root.Subcommands.Add(restart);
        root.Subcommands.Add(BuildStatus(ctx));
        root.Subcommands.Add(BuildOpen(ctx));
        root.Subcommands.Add(BuildVersion(output));
        root.Subcommands.Add(BuildConfig(ctx));
        root.Subcommands.Add(BuildSetPassword(ctx));
        root.Subcommands.Add(BuildApplyUpdate(ctx));
        root.Subcommands.Add(BuildImportLegacy(ctx));
        root.Subcommands.Add(BuildEnroll(ctx));
        root.Subcommands.Add(BuildMaintenance(ctx));

        var parseResult = root.Parse(args);
        var config = new InvocationConfiguration
        {
            Output = output,
            Error = error,
            // The host owns Ctrl+C / service-stop handling and needs up to 30 s to shut down
            // gracefully; don't let the CLI layer force-terminate the process first.
            ProcessTerminationTimeout = null,
            EnableDefaultExceptionHandler = false,
        };

        try
        {
            return await parseResult.InvokeAsync(config);
        }
        catch (CliException ex)
        {
            await error.WriteLineAsync($"error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException
                                       or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        {
            await error.WriteLineAsync($"error: {ex.Message}");
            return 1;
        }
    }

    private sealed record CliContext(Option<string?> DataDir, Option<string> ServiceName, TextWriter Output)
    {
        public string ServiceNameOf(ParseResult parse) => parse.GetValue(ServiceName)!;

        /// <summary>
        /// Data directory for management commands: an explicit --data-dir, else the directory
        /// the installed service runs with, else the environment/default. This keeps
        /// `status`, `config import`, `set-password` and `uninstall --purge-data` pointed at
        /// the instance they manage.
        /// </summary>
        /// <param name="useInstalledBinaries">
        /// Read shipped defaults (appsettings.json) from the installed service's folder rather than
        /// this executable's — what the running instance actually uses. `config validate` passes
        /// false to check a seed against the NEW version's defaults before upgrading.
        /// </param>
        public WatchTowerPaths PathsOf(ParseResult parse, bool useInstalledBinaries = true)
        {
            var service = new ServiceInstaller(ServiceNameOf(parse), TextWriter.Null);
            var installDir = useInstalledBinaries ? service.GetConfiguredInstallDirectory() : null;

            var explicitDir = Normalize(parse.GetValue(DataDir));
            var dataDir = explicitDir ?? Normalize(service.GetConfiguredDataDirectory());
            return WatchTowerPaths.Resolve(dataDir, installDir);
        }
    }

    private static Command BuildInstall(CliContext ctx)
    {
        var start = new Option<bool>("--start") { Description = "Start the service after installing." };
        var seed = new Option<FileInfo?>("--config")
        {
            Description = "JSON settings file to merge into the operator settings (e.g. portal URL, " +
                          "agent token, SMTP). Validated first; secrets are encrypted on import.",
        };

        var cmd = new Command("install", "Register WatchTower as a Windows service running from this folder.")
        {
            start, seed,
        };
        cmd.SetAction(parse =>
        {
            RequireAdministrator();
            var output = ctx.Output;
            var paths = ctx.PathsOf(parse, useInstalledBinaries: false); // defaults of the exe being installed
            var exe = Environment.ProcessPath ?? throw new CliException("Cannot determine the executable path.");

            WarnOnVolatileLocation(exe, output);

            paths.EnsureDirectories();
            DataDirectorySecurity.Harden(paths.DataDirectory);
            output.WriteLine($"Data directory: {paths.DataDirectory} (restricted to SYSTEM and Administrators)");

            if (parse.GetValue(seed) is { } seedFile)
                ImportSettings(paths, seedFile, output);

            // Always record the resolved data directory, so the service uses exactly the
            // directory that was prepared (and seeded) here, whatever the caller's environment.
            var serviceName = ctx.ServiceNameOf(parse);
            var serviceArgs = new List<string> { "--data-dir", paths.DataDirectory };
            if (!string.Equals(serviceName, WatchTowerHost.DefaultServiceName, StringComparison.OrdinalIgnoreCase))
                serviceArgs.AddRange(["--service-name", serviceName]);

            var installer = new ServiceInstaller(serviceName, output);
            installer.InstallOrUpdate(exe, serviceArgs,
                displayName: serviceName == WatchTowerHost.DefaultServiceName
                    ? "WatchTower Server Monitor"
                    : $"WatchTower Server Monitor ({serviceName})",
                description: "WatchTower server monitoring agent: resource, service and IIS health with alerting.");
            output.WriteLine($"Service '{installer.ServiceName}' installed ({exe}).");

            var url = ConsoleUrl(paths);
            if (url is not null)
            {
                ConsoleShortcut.Create(serviceName, url, exe);
                output.WriteLine();
                output.WriteLine($"Web console: {url}");
                output.WriteLine("  Open it from the Start menu ('WatchTower Console') or run 'WatchTower open'.");
                output.WriteLine("  The first visit (from this server) asks you to create the administrator password.");
            }

            if (parse.GetValue(start))
            {
                installer.Start(TimeSpan.FromSeconds(60));
                output.WriteLine("Service started.");
            }
            return 0;
        });
        return cmd;
    }

    private static Command BuildUninstall(CliContext ctx)
    {
        var purge = new Option<bool>("--purge-data")
        {
            Description = "Also delete the data directory (settings, database, logs). Irreversible.",
        };
        var cmd = new Command("uninstall", "Stop and remove the WatchTower Windows service.") { purge };
        cmd.SetAction(parse =>
        {
            RequireAdministrator();
            var output = ctx.Output;
            var serviceName = ctx.ServiceNameOf(parse);
            var paths = ctx.PathsOf(parse); // resolve BEFORE the registration disappears

            var installer = new ServiceInstaller(serviceName, output);
            installer.Uninstall();
            ConsoleShortcut.Remove(serviceName);
            output.WriteLine($"Service '{installer.ServiceName}' removed.");

            if (parse.GetValue(purge) && Directory.Exists(paths.DataDirectory))
            {
                if (UnsafePurgeReason(paths, serviceName) is { } reason)
                    throw new CliException($"Refusing to delete '{paths.DataDirectory}': {reason}");
                Directory.Delete(paths.DataDirectory, recursive: true);
                output.WriteLine($"Deleted data directory {paths.DataDirectory}.");
            }
            return 0;
        });
        return cmd;
    }

    private static Command BuildControl(CliContext ctx, string name, string description,
        Action<ServiceInstaller, ParseResult> action)
    {
        var cmd = new Command(name, description);
        cmd.SetAction(parse =>
        {
            RequireAdministrator();
            var installer = new ServiceInstaller(ctx.ServiceNameOf(parse), ctx.Output);
            if (!installer.IsInstalled())
                throw new CliException($"Service '{installer.ServiceName}' is not installed. Run 'WatchTower install' first.");
            action(installer, parse);
            ctx.Output.WriteLine($"Service '{installer.ServiceName}': {installer.GetStatus()}");
            return 0;
        });
        return cmd;
    }

    private static Command BuildStatus(CliContext ctx)
    {
        var cmd = new Command("status", "Show service state, version, file locations and the web console address.");
        cmd.SetAction(parse =>
        {
            var output = ctx.Output;
            var paths = ctx.PathsOf(parse);
            var installer = new ServiceInstaller(ctx.ServiceNameOf(parse), output);
            var status = installer.GetStatus();
            output.WriteLine($"Version:        {AgentInfo.ProductVersion}");
            output.WriteLine($"Service:        {(status is null ? "not installed" : status.ToString())}");
            output.WriteLine($"Install folder: {paths.InstallDirectory}");
            output.WriteLine($"Data folder:    {paths.DataDirectory}");
            output.WriteLine($"Settings file:  {paths.SettingsFilePath}");
            output.WriteLine($"Logs:           {paths.LogsDirectory}");
            output.WriteLine($"Web console:    {ConsoleUrl(paths) ?? "disabled"}");
            return 0;
        });
        return cmd;
    }

    private static Command BuildOpen(CliContext ctx)
    {
        var cmd = new Command("open", "Open the web console in the default browser.");
        cmd.SetAction(parse =>
        {
            var url = ConsoleUrl(ctx.PathsOf(parse))
                      ?? throw new CliException("The web console is disabled (Ui:Enabled) or has no usable listen URL.");
            ctx.Output.WriteLine($"Opening {url}");
            using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return 0;
        });
        return cmd;
    }

    private static Command BuildSetPassword(CliContext ctx)
    {
        var fromStdin = new Option<bool>("--password-stdin")
        {
            Description = "Read the new password from standard input (for scripted installs).",
        };
        var cmd = new Command("set-password", "Set or reset the web console administrator password.") { fromStdin };
        cmd.SetAction(async (parse, ct) =>
        {
            RequireAdministrator();
            var output = ctx.Output;
            var paths = ctx.PathsOf(parse);
            paths.EnsureDirectories();
            DataDirectorySecurity.Harden(paths.DataDirectory);

            string? password;
            if (parse.GetValue(fromStdin))
            {
                // Windows PowerShell 5.1 prefixes piped text with a UTF-8 BOM, which Trim() keeps.
                password = (await Console.In.ReadLineAsync(ct))?.Trim().TrimStart('﻿').Trim();
            }
            else
            {
                if (Console.IsInputRedirected)
                    throw new CliException("No interactive console; use --password-stdin.");
                password = ReadHidden("New password: ", output);
                if (ReadHidden("Confirm password: ", output) != password)
                    throw new CliException("Passwords do not match.");
            }

            if (PasswordHasher.CheckStrength(password) is { } weak)
                throw new CliException(weak);

            var store = new JsonSettingsStore(paths.SettingsFilePath);
            await store.UpdateAsync(doc => ConfigService.SetPath(doc, AdminCredentials.HashKey, PasswordHasher.Hash(password!)), ct);
            output.WriteLine("Web console password updated. Existing console sessions are signed out.");
            return 0;
        });
        return cmd;
    }

    /// <summary>
    /// Internal (install.ps1): import state from an old-style installation folder BEFORE its
    /// files are replaced — the old hand-edited appsettings.json is otherwise overwritten by the
    /// new shipped defaults before the new service gets a chance to migrate it.
    /// </summary>
    private static Command BuildImportLegacy(CliContext ctx)
    {
        var from = new Option<string>("--from") { Required = true, Description = "Old installation folder." };
        var cmd = new Command("import-legacy", "Internal: import settings and state from an old installation folder.")
        {
            from,
        };
        cmd.Hidden = true;
        cmd.SetAction(parse =>
        {
            RequireAdministrator();
            var target = ctx.PathsOf(parse, useInstalledBinaries: false);
            target.EnsureDirectories();
            DataDirectorySecurity.Harden(target.DataDirectory);

            var legacy = new WatchTowerPaths(Path.GetFullPath(parse.GetValue(from)!), target.DataDirectory);
            var result = LegacyStateMigrator.Run(legacy);
            if (result.Error is not null)
                throw new CliException($"Importing the previous installation's state failed: {result.Error}");
            ctx.Output.WriteLine(result.CopiedFiles.Count == 0
                ? "No previous-installation state to import."
                : $"Imported from {legacy.InstallDirectory}: {string.Join(", ", result.CopiedFiles)}");
            return 0;
        });
        return cmd;
    }

    /// <summary>Internal: launched by the running service from the staged new version.</summary>
    private static Command BuildApplyUpdate(CliContext ctx)
    {
        var source = new Option<string>("--source") { Required = true };
        var target = new Option<string>("--target") { Required = true };
        var pid = new Option<int>("--pid") { Required = true };
        var from = new Option<string>("--from") { Required = true };
        var to = new Option<string>("--to") { Required = true };
        var healthTimeout = new Option<int>("--health-timeout") { DefaultValueFactory = _ => 120 };

        var cmd = new Command("apply-update", "Internal: install a staged update (started by the service).")
        {
            source, target, pid, from, to, healthTimeout,
        };
        cmd.Hidden = true;
        cmd.SetAction(async (parse, _) =>
        {
            RequireAdministrator();
            var paths = WatchTowerPaths.Resolve(Normalize(parse.GetValue(ctx.DataDir)));
            Directory.CreateDirectory(paths.LogsDirectory);
            var logPath = Path.Combine(paths.LogsDirectory,
                $"update-{DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture)}.log");
            await using var log = new StreamWriter(
                new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));

            var request = new Updates.UpdateApplier.Request(
                ctx.ServiceNameOf(parse), paths,
                Path.GetFullPath(parse.GetValue(source)!), Path.GetFullPath(parse.GetValue(target)!),
                parse.GetValue(pid), parse.GetValue(from)!, parse.GetValue(to)!,
                TimeSpan.FromSeconds(Math.Clamp(parse.GetValue(healthTimeout), 30, 900)));

            var result = await new Updates.UpdateApplier(request, new Updates.WindowsServiceControl(), log).RunAsync();
            return result.Success ? 0 : 1;
        });
        return cmd;
    }

    private static Command BuildVersion(TextWriter output)
    {
        var cmd = new Command("version", "Print the WatchTower version.");
        cmd.SetAction(_ =>
        {
            output.WriteLine(AgentInfo.ProductVersion);
            return 0;
        });
        return cmd;
    }

    private static Command BuildConfig(CliContext ctx)
    {
        var file = new Argument<FileInfo>("file") { Description = "JSON file to merge into the operator settings." };
        var import = new Command("import",
            "Merge a JSON settings file into the operator settings (validated first; secrets are encrypted).")
        {
            file,
        };
        import.SetAction(parse =>
        {
            RequireAdministrator();
            var paths = ctx.PathsOf(parse);
            paths.EnsureDirectories();
            DataDirectorySecurity.Harden(paths.DataDirectory);
            ImportSettings(paths, parse.GetValue(file)!, ctx.Output);
            return 0;
        });

        var validateFile = new Argument<FileInfo>("file") { Description = "JSON settings file to check." };
        var validate = new Command("validate",
            "Check that merging a JSON settings file would produce a valid configuration, without writing anything.")
        {
            validateFile,
        };
        validate.SetAction(parse =>
        {
            ImportSettings(ctx.PathsOf(parse, useInstalledBinaries: false), parse.GetValue(validateFile)!, ctx.Output,
                validateOnly: true);
            return 0;
        });

        var path = new Command("path", "Print the operator settings file path.");
        path.SetAction(parse =>
        {
            ctx.Output.WriteLine(ctx.PathsOf(parse).SettingsFilePath);
            return 0;
        });

        return new Command("config", "Manage operator settings.") { import, validate, path };
    }

    /// <summary>Validates the configuration that would result, then (unless validating only) merges the file in.</summary>
    internal static void ImportSettings(WatchTowerPaths paths, FileInfo file, TextWriter output, bool validateOnly = false)
    {
        if (!file.Exists)
            throw new CliException($"Settings file '{file.FullName}' does not exist.");

        JsonObject patch;
        try
        {
            patch = JsonNode.Parse(File.ReadAllText(file.FullName), JsonSettingsStore.NodeOptions,
                        new System.Text.Json.JsonDocumentOptions
                        {
                            CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                            AllowTrailingCommas = true,
                        }) as JsonObject
                    ?? throw new CliException("The settings file must contain a JSON object.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new CliException($"Invalid JSON in '{file.FullName}': {ex.Message}");
        }

        var written = MergeValidated(paths, patch, $"'{file.Name}'", validateOnly);
        output.WriteLine(written
            ? $"Imported settings from {file.FullName} into {paths.SettingsFilePath}."
            : $"{file.FullName} is valid.");
    }

    /// <summary>
    /// Merges <paramref name="patch"/> into the operator settings after validating the resulting
    /// configuration — read, merge, validate and write under the store lock, so a concurrent
    /// console edit cannot slip in between. Returns whether anything was written.
    /// </summary>
    internal static bool MergeValidated(WatchTowerPaths paths, JsonObject patch, string what, bool validateOnly = false)
    {
        var report = new StartupValidator.Report([], []);
        var store = new JsonSettingsStore(paths.SettingsFilePath);
        var written = store.TryUpdateAsync(doc =>
        {
            JsonSettingsStore.MergePatch(doc, patch);
            report = ValidateCandidate(paths, doc);
            return report.IsValid && !validateOnly;
        }).GetAwaiter().GetResult();

        if (!report.IsValid)
            throw new CliException($"{what} would produce an invalid configuration; nothing was changed:" +
                                   Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", report.Errors));
        return written;
    }

    /// <summary>
    /// <c>maintenance start --minutes N [--reason text]</c> / <c>maintenance stop</c>: writes
    /// <c>Maintenance:Until</c>/<c>Reason</c> to the operator settings, which the running service
    /// picks up within seconds (no restart).
    /// </summary>
    private static Command BuildMaintenance(CliContext ctx)
    {
        var minutes = new Option<int>("--minutes")
        {
            Required = true,
            Description = $"Length of the window in minutes (1-{Services.Alerting.MaintenanceWindowRules.MaxMinutes}).",
        };
        var reason = new Option<string?>("--reason") { Description = "Why (shown in the console and the portal)." };
        var start = new Command("start",
            "Start a maintenance window: incidents are still recorded, but notifications are held and " +
            "services/application pools are not restarted automatically.")
        {
            minutes, reason,
        };
        start.SetAction(parse =>
        {
            var length = parse.GetValue(minutes);
            if (length is < 1 or > Services.Alerting.MaintenanceWindowRules.MaxMinutes)
                throw new CliException($"--minutes must be between 1 and {Services.Alerting.MaintenanceWindowRules.MaxMinutes}.");
            var text = parse.GetValue(reason)?.Trim() ?? string.Empty;
            if (text.Length > Services.Alerting.MaintenanceWindowRules.MaxReasonLength)
                throw new CliException($"--reason must be at most {Services.Alerting.MaintenanceWindowRules.MaxReasonLength} characters.");

            RequireAdministrator();
            var paths = ctx.PathsOf(parse);
            paths.EnsureDirectories();
            var until = DateTimeOffset.UtcNow.AddMinutes(length);
            MergeValidated(paths, new JsonObject
            {
                ["Maintenance"] = new JsonObject
                {
                    ["Until"] = Services.Alerting.MaintenanceState.Format(until),
                    ["Reason"] = text,
                },
            }, "The maintenance window");
            ctx.Output.WriteLine($"Maintenance window active until {until:yyyy-MM-dd HH:mm} UTC" +
                                 (text.Length > 0 ? $" ({text})." : "."));
            return 0;
        });

        var stop = new Command("stop", "End the maintenance window now. Alerts still open will notify once.");
        stop.SetAction(parse =>
        {
            RequireAdministrator();
            var paths = ctx.PathsOf(parse);
            paths.EnsureDirectories();
            MergeValidated(paths, new JsonObject { ["Maintenance"] = null }, "Ending the maintenance window");
            ctx.Output.WriteLine("Maintenance window ended.");
            if (PortalSetsMaintenance(paths))
                ctx.Output.WriteLine("note: the portal has also set a maintenance window, which takes precedence; end it from the portal.");
            return 0;
        });

        return new Command("maintenance", "Start or end a maintenance window.") { start, stop };
    }

    private static bool PortalSetsMaintenance(WatchTowerPaths paths)
    {
        try
        {
            return File.Exists(paths.RemoteConfigFilePath) &&
                   JsonNode.Parse(File.ReadAllText(paths.RemoteConfigFilePath), JsonSettingsStore.NodeOptions) is JsonObject root &&
                   root["Maintenance"] is JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static Command BuildEnroll(CliContext ctx)
    {
        var code = new Argument<string>("code") { Description = "Enrollment code from the portal's Add server dialog (WT1.…)." };
        var check = new Option<bool>("--check") { Description = "Only validate the code; change nothing." };
        var noRestart = new Option<bool>("--no-restart") { Description = "Do not restart the service afterwards." };
        var cmd = new Command("enroll", "Connect this server to a Visibility Portal using an enrollment code.")
        {
            code, check, noRestart,
        };
        cmd.SetAction(parse =>
        {
            if (!EnrollmentCode.TryParse(parse.GetValue(code), out var enrollment, out var error))
                throw new CliException(error);

            var output = ctx.Output;
            if (parse.GetValue(check))
            {
                output.WriteLine($"Valid enrollment code for agent '{enrollment.AgentId}' at {enrollment.Url}.");
                return 0;
            }

            RequireAdministrator();
            var paths = ctx.PathsOf(parse);
            paths.EnsureDirectories();
            DataDirectorySecurity.Harden(paths.DataDirectory);
            MergeValidated(paths, enrollment.ToSettingsPatch(), "The enrollment code");
            output.WriteLine($"Connected to {enrollment.Url} as agent '{enrollment.AgentId}' (token stored encrypted).");
            if (enrollment.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                output.WriteLine("warning: the portal uses plain HTTP; the agent token will travel unencrypted.");

            var installer = new ServiceInstaller(ctx.ServiceNameOf(parse), output);
            if (!parse.GetValue(noRestart) && installer.GetStatus() == System.ServiceProcess.ServiceControllerStatus.Running)
            {
                installer.Stop(TimeSpan.FromSeconds(90));
                installer.Start(TimeSpan.FromSeconds(90));
                output.WriteLine("Service restarted; the agent will appear in the portal within a minute.");
            }
            else
            {
                output.WriteLine("Restart the service to apply ('WatchTower restart').");
            }
            return 0;
        });
        return cmd;
    }

    private static StartupValidator.Report ValidateCandidate(WatchTowerPaths paths, JsonObject operatorDoc)
    {
        var builder = new ConfigurationBuilder();
        WatchTowerHost.ConfigureSources(builder, paths, "Production", reloadOnChange: false,
            operatorSettingsOverride: operatorDoc);
        var candidateConfig = builder.Build();
        try
        {
            return StartupValidator.Validate(candidateConfig);
        }
        finally
        {
            (candidateConfig as IDisposable)?.Dispose();
        }
    }

    /// <summary>The first address the console listens on, as a browsable URL; null when disabled.</summary>
    internal static string? ConsoleUrl(WatchTowerPaths paths)
    {
        try
        {
            var builder = new ConfigurationBuilder();
            WatchTowerHost.ConfigureSources(builder, paths, "Production", reloadOnChange: false);
            var config = builder.Build();
            var ui = config.GetSection(UiOptions.SectionName).Get<UiOptions>() ?? new UiOptions();
            (config as IDisposable)?.Dispose();

            var endpoint = UiEndpointPlanner.Plan(ui).Endpoints
                .OrderByDescending(e => e.IsLoopback) // prefer the local address for this machine
                .FirstOrDefault();
            if (endpoint is null)
                return null;

            var host = endpoint.IsLoopback
                ? (endpoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[::1]" : "127.0.0.1")
                : endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any)
                    ? Environment.MachineName
                    : endpoint.Address.ToString();
            return $"{(endpoint.Https ? "https" : "http")}://{host}:{endpoint.Port}/";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Why deleting the data directory would be unsafe, or null when it is safe.</summary>
    internal static string? UnsafePurgeReason(WatchTowerPaths paths, string serviceName)
    {
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.DataDirectory));

        if (Path.GetPathRoot(dir) is { } root && string.Equals(Path.TrimEndingDirectorySeparator(root), dir, StringComparison.OrdinalIgnoreCase))
            return "it is a drive root";

        Environment.SpecialFolder[] protectedFolders =
        [
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.UserProfile,
            Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyDocuments,
        ];
        foreach (var folder in protectedFolders.Select(Environment.GetFolderPath).Where(f => !string.IsNullOrEmpty(f)))
        {
            var f = Path.TrimEndingDirectorySeparator(folder);
            if (string.Equals(f, dir, StringComparison.OrdinalIgnoreCase) ||
                f.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "it is (or contains) a system folder";
        }

        if (string.Equals(dir, Path.TrimEndingDirectorySeparator(paths.InstallDirectory), StringComparison.OrdinalIgnoreCase))
            return "it is the install folder";

        var looksLikeWatchTower = Directory.Exists(paths.ConfigDirectory) || Directory.Exists(paths.StateDirectory) ||
                                  File.Exists(paths.DatabasePath);
        if (!looksLikeWatchTower)
            return "it does not look like a WatchTower data directory";

        var sharedWith = ServiceInstaller.FindServicesUsingDataDirectory(dir)
            .Where(s => !string.Equals(s, serviceName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sharedWith.Count > 0)
            return $"it is still used by service(s) {string.Join(", ", sharedWith)}";

        return null;
    }

    private static string ReadHidden(string prompt, TextWriter output)
    {
        output.Write(prompt);
        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (buffer.Length > 0) buffer.Length--; continue; }
            if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
        }
        output.WriteLine();
        return buffer.ToString();
    }

    private static void RequireAdministrator()
    {
        if (!DataDirectorySecurity.IsPrivileged())
            throw new CliException("This command must be run from an elevated (Administrator) prompt.", exitCode: 5);
    }

    private static void WarnOnVolatileLocation(string exe, TextWriter output)
    {
        string[] volatileRoots =
        [
            Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ];
        if (volatileRoots.Any(r => !string.IsNullOrEmpty(r) && exe.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
            output.WriteLine(
                "warning: installing from a user/temp folder. Copy WatchTower to a permanent location such as " +
                @"'C:\Program Files\WatchTower' first (install.ps1 does this for you).");
    }

    internal static string? Normalize(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
}

internal sealed class CliException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}