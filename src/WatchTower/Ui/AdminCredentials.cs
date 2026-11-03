using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

using WatchTower.Configuration.Settings;

namespace WatchTower.Ui;

/// <summary>
/// The console administrator password hash. Cached in memory so a password set or changed
/// through the console takes effect immediately (the settings file watcher lags ~250 ms), and
/// refreshed when the settings file changes (e.g. via <c>WatchTower set-password</c>).
/// </summary>
public sealed class AdminCredentials : IDisposable
{
    public const string HashKey = "Ui:AdminPasswordHash";

    private readonly ISettingsStore _store;
    private readonly IDisposable? _subscription;
    private volatile string? _hash;

    public AdminCredentials(ISettingsStore store, IOptionsMonitor<UiOptions> options)
    {
        _store = store;
        _hash = options.CurrentValue.AdminPasswordHash;
        _subscription = options.OnChange(o => _hash = o.AdminPasswordHash);
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_hash);

    public bool Verify(string password) => PasswordHasher.Verify(password, _hash);

    /// <summary>Short fingerprint of the current hash. Sessions carry it, so changing the password revokes them.</summary>
    public string Stamp => StampOf(_hash);

    public static string StampOf(string? hash) => string.IsNullOrEmpty(hash)
        ? string.Empty
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hash)))[..16];

    /// <summary>First-run setup. Returns false if a password already exists (checked under the store lock).</summary>
    public async Task<bool> TrySetInitialAsync(string password, CancellationToken ct)
    {
        var hash = PasswordHasher.Hash(password);
        var created = false;
        await _store.UpdateAsync(doc =>
        {
            if (doc["Ui"]?["AdminPasswordHash"]?.GetValue<string>() is { Length: > 0 })
                return;
            ConfigService.SetPath(doc, HashKey, hash);
            created = true;
        }, ct);

        if (created)
            _hash = hash;
        return created;
    }

    public async Task ChangeAsync(string newPassword, CancellationToken ct)
    {
        var hash = PasswordHasher.Hash(newPassword);
        await _store.UpdateAsync(doc => ConfigService.SetPath(doc, HashKey, hash), ct);
        _hash = hash;
    }

    public void Dispose() => _subscription?.Dispose();
}