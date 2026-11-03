namespace WatchTower.Ui;

/// <summary>Built-in configuration web UI.</summary>
public class UiOptions
{
    public const string SectionName = "Ui";
    public const string DefaultUrl = "http://127.0.0.1:8750";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Kestrel listen URLs. Loopback-only by default; binding any other address additionally
    /// requires <see cref="AllowRemoteAccess"/> and, unless <see cref="RequireHttpsForRemoteAccess"/> is
    /// off, an HTTPS URL with a certificate.
    /// </summary>
    // Empty means "use DefaultUrl". A non-empty default would be APPENDED to by the configuration
    // binder, so a configured URL list could never remove the default address.
    public List<string> Urls { get; set; } = [];

    /// <summary>Opt-in for listening on non-loopback addresses.</summary>
    public bool AllowRemoteAccess { get; set; }

    /// <summary>
    /// When true (default), non-loopback URLs must be https://. Turn off to serve the console to
    /// other machines over plain HTTP (e.g. a trusted management network) — the password and
    /// session cookie then cross the network unencrypted.
    /// </summary>
    public bool RequireHttpsForRemoteAccess { get; set; } = true;

    /// <summary>
    /// Host names remote browsers may use to reach the console (e.g. "srv-01.corp.local").
    /// Empty allows any name for remote connections. Connections from the server itself must
    /// always use 127.0.0.1 / localhost (DNS-rebinding protection).
    /// </summary>
    public List<string> AllowedHosts { get; set; } = [];

    /// <summary>Thumbprint of a certificate in LocalMachine\My used for HTTPS URLs.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>Idle timeout for UI sessions.</summary>
    public int SessionTimeoutMinutes { get; set; } = 30;

    /// <summary>PBKDF2 hash of the administrator password (set via the UI's first-run setup or the CLI).</summary>
    public string? AdminPasswordHash { get; set; }
}