using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Options;

using WatchTower.Hosting;

namespace WatchTower.Updates;

[JsonConverter(typeof(JsonStringEnumConverter<UpdatePhase>))]
public enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    Installing,
    Failed,
}

/// <summary>Outcome of the last install, written by the installer helper.</summary>
public sealed record UpdateResult(string FromVersion, string ToVersion, bool Success, string Message, DateTimeOffset CompletedAt);

public sealed record UpdateStatus(
    string CurrentVersion,
    UpdatePhase Phase,
    UpdateManifest? Available,
    double? Progress,
    DateTimeOffset? RestartAt,
    DateTimeOffset? LastCheckedAt,
    string? Error,
    UpdateResult? LastResult,
    bool CanInstall,
    string? CannotInstallReason);

/// <summary>
/// Self-update state machine: check → download (verified) → staged → countdown → install.
/// State lives in the service (not the browser), so a countdown keeps running — and can be
/// cancelled — from any console session or the portal.
/// </summary>
public sealed class UpdateService
{
    private const long MaxPackageBytes = 512L * 1024 * 1024;

    private readonly IUpdateFeedClient _feed;
    private readonly IHttpClientFactory _http;
    private readonly IOptionsMonitor<UpdateOptions> _options;
    private readonly WatchTowerPaths _paths;
    private readonly AgentInfo _agent;
    private readonly InstanceIdentity _instance;
    private readonly TimeProvider _time;
    private readonly ILogger<UpdateService> _logger;
    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _operation = new(1, 1);

    private UpdatePhase _phase = UpdatePhase.Idle;
    private UpdateManifest? _available;
    private double? _progress;
    private DateTimeOffset? _restartAt;
    private DateTimeOffset? _lastChecked;
    private string? _error;
    private string? _stagedDirectory;
    private UpdateResult? _lastResult;

    public UpdateService(
        IUpdateFeedClient feed,
        IHttpClientFactory http,
        IOptionsMonitor<UpdateOptions> options,
        WatchTowerPaths paths,
        AgentInfo agent,
        InstanceIdentity instance,
        TimeProvider time,
        ILogger<UpdateService> logger)
    {
        _feed = feed;
        _http = http;
        _options = options;
        _paths = paths;
        _agent = agent;
        _instance = instance;
        _time = time;
        _logger = logger;
        LoadPersistedState();
    }

    public static SemanticVersion CurrentVersion { get; } =
        SemanticVersion.TryParse(AgentInfo.Version, out var v) ? v : SemanticVersion.Parse("0.0.0");

    public static string RuntimeId =>
        RuntimeInformation.RuntimeIdentifier.StartsWith("win", StringComparison.OrdinalIgnoreCase)
            ? RuntimeInformation.RuntimeIdentifier
            : "win-x64";

    /// <summary>Checks the extracted package (overridable in tests, which cannot produce a versioned exe).</summary>
    internal Action<string, string> PackageValidator { get; set; } = ValidateStagedPackage;

    internal string StagingRoot => Path.Combine(_paths.UpdatesDirectory, "staged");
    internal string DownloadRoot => Path.Combine(_paths.UpdatesDirectory, "download");
    internal string StatePath => Path.Combine(_paths.UpdatesDirectory, "status.json");
    internal string LastResultPath => Path.Combine(_paths.UpdatesDirectory, "last-result.json");

    public UpdateStatus GetStatus()
    {
        RefreshLastResult();
        _ = IsInstalling(); // applies the stuck-installer timeout
        lock (_lock)
        {
            var (canInstall, reason) = CanInstall();
            return new UpdateStatus(CurrentVersion.ToString(), _phase, _available, _progress, _restartAt,
                _lastChecked, _error, _lastResult, canInstall, reason);
        }
    }

    /// <summary>Asks the feed for the newest release.</summary>
    public async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        if (!await _operation.WaitAsync(0, ct))
            return GetStatus(); // a check/download/install launch is already running

        // Checked UNDER the operation lock, so an installer launched in between cannot have its
        // Installing phase overwritten by this check.
        if (IsInstalling())
        {
            _operation.Release();
            return GetStatus();
        }

        try
        {
            SetPhase(UpdatePhase.Checking);
            var manifest = await _feed.GetLatestAsync(_options.CurrentValue, ct);
            lock (_lock)
            {
                _lastChecked = _time.GetUtcNow();
                _error = null;
                if (manifest is not null && manifest.SemVer > CurrentVersion)
                {
                    var alreadyStaged = _stagedDirectory is not null && _available?.Version == manifest.Version;
                    _available = manifest;
                    _phase = alreadyStaged ? UpdatePhase.ReadyToInstall : UpdatePhase.Available;
                    if (!alreadyStaged) _stagedDirectory = null;

                    // Offered, but not installable automatically: explain why instead of failing later.
                    _blockedReason = BlockedReason(manifest);
                    if (_blockedReason is not null)
                    {
                        _error = _blockedReason;
                        _stagedDirectory = null;
                        _phase = UpdatePhase.Available;
                    }
                }
                else
                {
                    _available = null;
                    _blockedReason = null;
                    _stagedDirectory = null;
                    _phase = UpdatePhase.UpToDate;
                }
            }
            if (manifest is not null && manifest.SemVer > CurrentVersion)
                _logger.LogInformation("WatchTower {Version} is available (running {Current})", manifest.Version, CurrentVersion);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException
                                       or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("Update check failed: {Error}", ex.Message);
            Fail($"Could not check for updates: {ex.Message}");
        }
        finally
        {
            PersistState();
            _operation.Release();
        }
        return GetStatus();
    }

    /// <summary>
    /// Starts downloading the available release in the background. When it completes and
    /// <paramref name="countdown"/> is set, the restart countdown begins.
    /// </summary>
    public bool TryStartDownload(bool countdown, out string message)
    {
        // Take the operation lock FIRST, then inspect state and pick the manifest under it: a
        // concurrent check can no longer swap the offered release between selection and staging
        // (which would stage release A while advertising — and health-checking — release B).
        if (!_operation.Wait(0))
        {
            message = "A check or download is already in progress.";
            return false;
        }

        UpdateManifest? manifest;
        var handedOff = false;
        try
        {
            if (IsInstalling())
            {
                message = "An installation is already in progress.";
                return false;
            }
            lock (_lock)
            {
                manifest = _available;
                if (manifest is null)
                {
                    message = "No update is available. Check for updates first.";
                    return false;
                }
                if (_blockedReason is not null)
                {
                    message = _blockedReason;
                    return false;
                }
                if (_phase == UpdatePhase.ReadyToInstall)
                {
                    if (countdown) StartCountdownLocked();
                    message = "The update is already downloaded.";
                    return true;
                }
            }
            handedOff = true;
        }
        finally
        {
            if (!handedOff) _operation.Release();
        }

        _ = Task.Run(async () =>
        {
            try { await DownloadAndStageAsync(manifest, countdown, CancellationToken.None); }
            finally { _operation.Release(); }
        });
        message = $"Downloading WatchTower {manifest.Version}.";
        return true;
    }

    public void CancelCountdown()
    {
        lock (_lock)
        {
            if (_restartAt is null) return;
            _restartAt = null;
        }
        _logger.LogInformation("Update restart countdown cancelled");
    }

    public void StartCountdown()
    {
        lock (_lock)
        {
            if (_phase == UpdatePhase.ReadyToInstall)
                StartCountdownLocked();
        }
    }

    /// <summary>Called every second by <see cref="UpdateWorker"/>: installs when the countdown ends.</summary>
    internal bool CountdownElapsed()
    {
        lock (_lock)
            return _phase == UpdatePhase.ReadyToInstall && _restartAt is { } at && at <= _time.GetUtcNow();
    }

    /// <summary>Launches the installer helper, which stops this service, swaps binaries and restarts it.</summary>
    public bool TryInstallNow(out string message)
    {
        // Exclude a concurrent check/download for the moment the installer is launched; after
        // that the Installing phase itself blocks them (see IsInstalling).
        if (!_operation.Wait(0))
        {
            message = "A check or download is in progress; try again in a moment.";
            return false;
        }
        try
        {
            return LaunchInstaller(out message);
        }
        finally
        {
            _operation.Release();
        }
    }

    private bool LaunchInstaller(out string message)
    {
        string staged;
        UpdateManifest manifest;
        lock (_lock)
        {
            var (canInstall, reason) = CanInstall();
            if (!canInstall)
            {
                message = reason!;
                return false;
            }
            staged = _stagedDirectory!;
            manifest = _available!;
            _phase = UpdatePhase.Installing;
            _installingSince = _time.GetUtcNow();
            _restartAt = null;
        }

        var exe = Path.Combine(staged, "WatchTower.exe");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _paths.UpdatesDirectory,
        };
        foreach (var arg in new[]
                 {
                     "apply-update",
                     "--service-name", _instance.ServiceName,
                     "--data-dir", _paths.DataDirectory,
                     "--source", staged,
                     "--target", _paths.InstallDirectory,
                     "--pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "--from", CurrentVersion.ToString(),
                     "--to", manifest.Version,
                     "--health-timeout", Math.Clamp(_options.CurrentValue.HealthTimeoutSeconds, 30, 900)
                         .ToString(System.Globalization.CultureInfo.InvariantCulture),
                 })
            psi.ArgumentList.Add(arg);

        try
        {
            using var _ = Process.Start(psi) ?? throw new InvalidOperationException("The installer did not start.");
            _logger.LogWarning("Installing WatchTower {Version}; the service will restart now", manifest.Version);
            PersistState();
            message = $"Installing WatchTower {manifest.Version}. The console will be back in a moment.";
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogError(ex, "Could not start the update installer");
            Fail($"Could not start the installer: {ex.Message}");
            message = _error!;
            return false;
        }
    }

    private async Task DownloadAndStageAsync(UpdateManifest manifest, bool countdown, CancellationToken ct)
    {
        try
        {
            var asset = manifest.Assets.FirstOrDefault(a => string.Equals(a.Rid, RuntimeId, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"Release {manifest.Version} has no package for {RuntimeId}.");
            var url = UpdateFeedClient.RequireHttps(asset.Url, "package URL", _options.CurrentValue.RequireHttps);

            lock (_lock)
            {
                _phase = UpdatePhase.Downloading;
                _progress = 0;
                _error = null;
            }

            Directory.CreateDirectory(DownloadRoot);
            var zipPath = Path.Combine(DownloadRoot, $"WatchTower-{manifest.Version}-{asset.Rid}.zip");
            var partial = zipPath + ".partial";

            string actualHash;
            using (var http = _http.CreateClient(UpdateFeedClient.HttpClientName))
            {
                http.Timeout = TimeSpan.FromMinutes(15);
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                var total = asset.Size > 0 ? asset.Size : response.Content.Headers.ContentLength ?? 0;
                if (total > MaxPackageBytes)
                    throw new InvalidDataException("The update package is unexpectedly large.");

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using (var target = File.Create(partial))
                {
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    long read = 0;
                    int n;
                    while ((n = await source.ReadAsync(buffer, ct)) > 0)
                    {
                        read += n;
                        if (read > MaxPackageBytes)
                            throw new InvalidDataException("The update package is unexpectedly large.");
                        sha.AppendData(buffer, 0, n);
                        await target.WriteAsync(buffer.AsMemory(0, n), ct);
                        if (total > 0)
                            lock (_lock) _progress = Math.Min(1.0, (double)read / total);
                    }
                    actualHash = Convert.ToHexStringLower(sha.GetHashAndReset());
                }
            }

            if (!string.Equals(actualHash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded package does not match its published SHA-256 checksum.");

            var publicKey = _options.CurrentValue.PublicKey;
            if (!string.IsNullOrWhiteSpace(publicKey) &&
                !UpdateSignature.Verify(publicKey, manifest.Version, asset, out var sigError))
                throw new InvalidDataException($"Rejected the update: {sigError}.");

            File.Move(partial, zipPath, overwrite: true);

            var staged = Path.Combine(StagingRoot, manifest.Version);
            if (Directory.Exists(staged))
                Directory.Delete(staged, recursive: true);
            Directory.CreateDirectory(staged);
            // ExtractToDirectory rejects entries that would escape the destination folder.
            ZipFile.ExtractToDirectory(zipPath, staged);
            PackageValidator(staged, manifest.Version);
            File.Delete(zipPath);

            lock (_lock)
            {
                _stagedDirectory = staged;
                _phase = UpdatePhase.ReadyToInstall;
                _progress = 1;
                if (countdown) StartCountdownLocked();
            }
            _logger.LogInformation("WatchTower {Version} downloaded and verified", manifest.Version);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException
                                       or UnauthorizedAccessException or TaskCanceledException)
        {
            _logger.LogWarning("Update download failed: {Error}", ex.Message);
            Fail($"Download failed: {ex.Message}");
        }
        finally
        {
            PersistState();
        }
    }

    internal static void ValidateStagedPackage(string staged, string expectedVersion)
    {
        var exe = Path.Combine(staged, "WatchTower.exe");
        if (!File.Exists(exe))
            throw new InvalidDataException("The update package does not contain WatchTower.exe.");

        var productVersion = FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0];
        if (!string.Equals(productVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"The package contains version '{productVersion}', but the release says '{expectedVersion}'.");
    }

    /// <summary>
    /// True while an installer launched by this process should be running. The installer stops
    /// this service within seconds; if we are still alive after <see cref="InstallerTimeout"/>
    /// it never got going, so the update becomes installable again instead of staying stuck.
    /// </summary>
    private bool IsInstalling()
    {
        lock (_lock)
        {
            if (_phase != UpdatePhase.Installing)
                return false;
            if (_installingSince is { } since && _time.GetUtcNow() - since < InstallerTimeout)
                return true;

            _phase = _stagedDirectory is not null ? UpdatePhase.ReadyToInstall : UpdatePhase.Available;
            _error = "The installer did not complete. Check Logs/update-*.log, then try again.";
            _installingSince = null;
            return false;
        }
    }

    internal static readonly TimeSpan InstallerTimeout = TimeSpan.FromMinutes(10);

    private DateTimeOffset? _installingSince;

    private (bool, string?) CanInstall()
    {
        if (_phase == UpdatePhase.Installing) return (false, "An installation is already in progress.");
        if (_stagedDirectory is null || _available is null) return (false, "No update has been downloaded.");
        if (!_agent.IsWindowsService)
            return (false, "Updates can only be installed while WatchTower runs as a Windows service.");
        return (true, null);
    }

    /// <summary>Newest manifest schema this version understands.</summary>
    internal const int SupportedManifestSchema = 1;

    private string? _blockedReason;

    /// <summary>Why <paramref name="manifest"/> cannot be installed automatically, or null.</summary>
    internal static string? BlockedReason(UpdateManifest manifest)
    {
        if (manifest.Schema > SupportedManifestSchema)
            return $"WatchTower {manifest.Version} uses a newer release format than this version understands. " +
                   "Install it manually from the release page.";
        if (!string.IsNullOrWhiteSpace(manifest.MinimumVersion) &&
            SemanticVersion.TryParse(manifest.MinimumVersion, out var minimum) && CurrentVersion < minimum)
            return $"WatchTower {manifest.Version} can only be installed over version {minimum} or later " +
                   $"(this server runs {CurrentVersion}). Upgrade to {minimum} first, or install manually.";
        return null;
    }

    private void StartCountdownLocked()
    {
        // No countdown when nothing could be installed at the end of it (e.g. console mode).
        if (!_agent.IsWindowsService)
            return;
        var seconds = Math.Clamp(_options.CurrentValue.RestartCountdownSeconds, 5, 3600);
        _restartAt = _time.GetUtcNow().AddSeconds(seconds);
        _logger.LogInformation("WatchTower will restart to install {Version} in {Seconds}s", _available?.Version, seconds);
    }

    private void SetPhase(UpdatePhase phase)
    {
        lock (_lock) _phase = phase;
    }

    private void Fail(string error)
    {
        lock (_lock)
        {
            _error = error;
            _restartAt = null;
            _progress = null;
            _phase = _stagedDirectory is not null ? UpdatePhase.ReadyToInstall
                : _available is not null ? UpdatePhase.Available
                : UpdatePhase.Failed;
        }
    }

    // ── persistence ──────────────────────────────────────────────────────────

    private DateTime _lastResultStamp;

    /// <summary>
    /// The installer writes its outcome only after the new service has started (it waits for
    /// the health beacon), so the file is re-read whenever it changes rather than only at start-up.
    /// </summary>
    private void RefreshLastResult()
    {
        try
        {
            var info = new FileInfo(LastResultPath);
            if (!info.Exists || info.LastWriteTimeUtc == _lastResultStamp)
                return;
            var result = JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(LastResultPath), UpdateFeedClient.Json);
            lock (_lock)
            {
                _lastResult = result;
                _lastResultStamp = info.LastWriteTimeUtc;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(ex, "Could not read the last update result");
        }
    }

    private sealed record PersistedState(DateTimeOffset? LastCheckedAt, UpdateManifest? Available, string? StagedDirectory);

    private void PersistState()
    {
        try
        {
            PersistedState state;
            lock (_lock) state = new PersistedState(_lastChecked, _available, _stagedDirectory);
            Directory.CreateDirectory(_paths.UpdatesDirectory);
            var temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, UpdateFeedClient.Json));
            File.Move(temp, StatePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not persist update state");
        }
    }

    private void LoadPersistedState()
    {
        try
        {
            if (File.Exists(StatePath) &&
                JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(StatePath), UpdateFeedClient.Json) is { } state)
            {
                _lastChecked = state.LastCheckedAt;
                if (state.Available is { } available && SemanticVersion.TryParse(available.Version, out var v) && v > CurrentVersion)
                {
                    _available = available;
                    _phase = UpdatePhase.Available;
                    _blockedReason = BlockedReason(available);
                    _error = _blockedReason;
                    if (_blockedReason is null && state.StagedDirectory is { } staged && Directory.Exists(staged))
                    {
                        _stagedDirectory = staged;
                        _phase = UpdatePhase.ReadyToInstall;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(ex, "Could not read persisted update state");
        }

        CleanUpStaleStaging();
    }

    /// <summary>Removes staged packages that are not newer than the running version (e.g. just installed).</summary>
    private void CleanUpStaleStaging()
    {
        try
        {
            if (!Directory.Exists(StagingRoot)) return;
            foreach (var dir in Directory.GetDirectories(StagingRoot))
            {
                if (string.Equals(dir, _stagedDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                if (SemanticVersion.TryParse(Path.GetFileName(dir), out var v) && v > CurrentVersion) continue;
                Directory.Delete(dir, recursive: true);
            }
            if (Directory.Exists(DownloadRoot))
                foreach (var partial in Directory.GetFiles(DownloadRoot, "*.partial"))
                    File.Delete(partial);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not clean up old update packages");
        }
    }
}