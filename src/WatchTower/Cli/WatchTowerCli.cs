using System.CommandLine;
using System.Text.Json.Nodes;

using WatchTower.Configuration.Settings;
using WatchTower.Hosting;

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
            Description = $"Data directory for settings, database and logs (default: %ProgramData%\\WatchTower, " +
                          $"or the {WatchTowerPaths.DataDirectoryEnvironmentVariable} environment variable).",
            Recursive = true,
        };

        var serviceNameOption = new Option<string>("--service-name")
        {
            Description = "Windows service name (default: WatchTower). Use to run side-by-side instances.",
            Recursive = true,
            DefaultValueFactory = _ => WatchTowerHost.DefaultServiceName,
        };

        var root = new RootCommand(
            "WatchTower — Windows server monitoring agent. Run without a command to start the monitor.");
        root.Options.Add(dataDirOption);
        root.Options.Add(serviceNameOption);
        root.SetAction(async (parse, _) => await WatchTowerHost.RunAsync(Paths(parse), parse.GetValue(serviceNameOption)!));

        root.Subcommands.Add(BuildInstall(dataDirOption, serviceNameOption, output));
        root.Subcommands.Add(BuildUninstall(dataDirOption, serviceNameOption, output));
        root.Subcommands.Add(BuildControl("start", "Start the WatchTower service.", serviceNameOption, output,
            s => s.Start(TimeSpan.FromSeconds(60))));
        root.Subcommands.Add(BuildControl("stop", "Stop the WatchTower service.", serviceNameOption, output,
            s => s.Stop(TimeSpan.FromSeconds(60))));
        root.Subcommands.Add(BuildControl("restart", "Restart the WatchTower service.", serviceNameOption, output, s =>
        {
            s.Stop(TimeSpan.FromSeconds(60));
            s.Start(TimeSpan.FromSeconds(60));
        }));
        root.Subcommands.Add(BuildStatus(dataDirOption, serviceNameOption, output));
        root.Subcommands.Add(BuildVersion(output));
        root.Subcommands.Add(BuildConfig(dataDirOption, output));

        WatchTowerPaths Paths(ParseResult parse) => WatchTowerPaths.Resolve(Normalize(parse.GetValue(dataDirOption)));

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

    private static Command BuildInstall(Option<string?> dataDirOption, Option<string> serviceNameOption, TextWriter output)
    {
        var start = new Option<bool>("--start") { Description = "Start the service after installing." };
        var seed = new Option<FileInfo?>("--config")
        {
            Description = "JSON settings file to merge into the operator settings (e.g. portal URL, " +
                          "agent token, SMTP). Secrets are encrypted on import.",
        };

        var cmd = new Command("install", "Register WatchTower as a Windows service running from this folder.")
        {
            start, seed,
        };
        cmd.SetAction(parse =>
        {
            RequireAdministrator();
            var dataDir = Normalize(parse.GetValue(dataDirOption));
            var paths = WatchTowerPaths.Resolve(dataDir);
            var exe = Environment.ProcessPath ?? throw new CliException("Cannot determine the executable path.");

            WarnOnVolatileLocation(exe, output);

            paths.EnsureDirectories();
            DataDirectorySecurity.Harden(paths.DataDirectory);
            output.WriteLine($"Data directory: {paths.DataDirectory} (restricted to SYSTEM and Administrators)");

            if (parse.GetValue(seed) is { } seedFile)
                ImportSettings(paths, seedFile, output);

            var serviceName = parse.GetValue(serviceNameOption)!;
            var serviceArgs = new List<string>();
            if (dataDir is not null) serviceArgs.AddRange(["--data-dir", dataDir]);
            if (!string.Equals(serviceName, WatchTowerHost.DefaultServiceName, StringComparison.OrdinalIgnoreCase))
                serviceArgs.AddRange(["--service-name", serviceName]);

            var installer = new ServiceInstaller(serviceName, output);
            installer.InstallOrUpdate(exe, serviceArgs,
                displayName: serviceName == WatchTowerHost.DefaultServiceName
                    ? "WatchTower Server Monitor"
                    : $"WatchTower Server Monitor ({serviceName})",
                description: "WatchTower server monitoring agent: resource, service and IIS health with alerting.");
            output.WriteLine($"Service '{installer.ServiceName}' installed ({exe}).");

            if (parse.GetValue(start))
            {
                installer.Start(TimeSpan.FromSeconds(60));
                output.WriteLine("Service started.");
            }
            return 0;
        });
        return cmd;
    }

    private static Command BuildUninstall(Option<string?> dataDirOption, Option<string> serviceNameOption, TextWriter output)
    {
        var purge = new Option<bool>("--purge-data")
        {
            Description = "Also delete the data directory (settings, database, logs). Irreversible.",
        };
        var cmd = new Command("uninstall", "Stop and remove the WatchTower Windows service.") { purge };
        cmd.SetAction(parse =>
        {
            RequireAdministrator();
            var installer = new ServiceInstaller(parse.GetValue(serviceNameOption)!, output);
            installer.Uninstall();
            output.WriteLine($"Service '{installer.ServiceName}' removed.");

            if (parse.GetValue(purge))
            {
                var paths = WatchTowerPaths.Resolve(Normalize(parse.GetValue(dataDirOption)));
                if (Directory.Exists(paths.DataDirectory))
                {
                    Directory.Delete(paths.DataDirectory, recursive: true);
                    output.WriteLine($"Deleted data directory {paths.DataDirectory}.");
                }
            }
            return 0;
        });
        return cmd;
    }

    private static Command BuildControl(string name, string description, Option<string> serviceNameOption, TextWriter output, Action<ServiceInstaller> action)
    {
        var cmd = new Command(name, description);
        cmd.SetAction(parse =>
        {
            RequireAdministrator();
            var installer = new ServiceInstaller(parse.GetValue(serviceNameOption)!, output);
            if (!installer.IsInstalled())
                throw new CliException($"Service '{installer.ServiceName}' is not installed. Run 'WatchTower install' first.");
            action(installer);
            output.WriteLine($"Service '{installer.ServiceName}': {installer.GetStatus()}");
            return 0;
        });
        return cmd;
    }

    private static Command BuildStatus(Option<string?> dataDirOption, Option<string> serviceNameOption, TextWriter output)
    {
        var cmd = new Command("status", "Show service state, version and file locations.");
        cmd.SetAction(parse =>
        {
            var paths = WatchTowerPaths.Resolve(Normalize(parse.GetValue(dataDirOption)));
            var installer = new ServiceInstaller(parse.GetValue(serviceNameOption)!, output);
            var status = installer.GetStatus();
            output.WriteLine($"Version:        {AgentInfo.ProductVersion}");
            output.WriteLine($"Service:        {(status is null ? "not installed" : status.ToString())}");
            output.WriteLine($"Install folder: {paths.InstallDirectory}");
            output.WriteLine($"Data folder:    {paths.DataDirectory}");
            output.WriteLine($"Settings file:  {paths.SettingsFilePath}");
            output.WriteLine($"Logs:           {paths.LogsDirectory}");
            return 0;
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

    private static Command BuildConfig(Option<string?> dataDirOption, TextWriter output)
    {
        var file = new Argument<FileInfo>("file") { Description = "JSON file to merge into the operator settings." };
        var import = new Command("import", "Merge a JSON settings file into the operator settings (secrets are encrypted).")
        {
            file,
        };
        import.SetAction(parse =>
        {
            RequireAdministrator();
            var paths = WatchTowerPaths.Resolve(Normalize(parse.GetValue(dataDirOption)));
            paths.EnsureDirectories();
            ImportSettings(paths, parse.GetValue(file)!, output);
            return 0;
        });

        var path = new Command("path", "Print the operator settings file path.");
        path.SetAction(parse =>
        {
            output.WriteLine(WatchTowerPaths.Resolve(Normalize(parse.GetValue(dataDirOption))).SettingsFilePath);
            return 0;
        });

        return new Command("config", "Manage operator settings.") { import, path };
    }

    internal static void ImportSettings(WatchTowerPaths paths, FileInfo file, TextWriter output)
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

        var store = new JsonSettingsStore(paths.SettingsFilePath);
        store.ApplyMergePatchAsync(patch).GetAwaiter().GetResult();
        output.WriteLine($"Imported settings from {file.FullName} into {paths.SettingsFilePath}.");
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

    private static string? Normalize(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
}

internal sealed class CliException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}