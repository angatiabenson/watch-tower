using System.Security.Cryptography;
using System.Text;

namespace WatchTower.Configuration.Secrets;

/// <summary>Encrypts configuration secrets at rest.</summary>
public interface ISecretProtector
{
    /// <summary>Returns the protected form (prefixed) of <paramref name="plaintext"/>.</summary>
    string Protect(string plaintext);

    /// <summary>Decrypts a value produced by <see cref="Protect"/>. Throws when it cannot.</summary>
    string Unprotect(string protectedValue);
}

/// <summary>
/// Windows DPAPI with <see cref="DataProtectionScope.LocalMachine"/> scope: the value can only
/// be decrypted on this machine (the service runs as LocalSystem while the CLI runs as an
/// administrator, so user scope would not work). Confidentiality against other local users
/// comes from the data directory ACL (SYSTEM + Administrators only).
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public const string Prefix = "dpapi:";

    public static readonly DpapiSecretProtector Instance = new();

    // Application-specific entropy so blobs from other DPAPI users are not interchangeable.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WatchTower.Settings.v1");

    public static bool IsProtected(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.LocalMachine);
        return Prefix + Convert.ToBase64String(bytes);
    }

    public string Unprotect(string protectedValue)
    {
        if (!IsProtected(protectedValue))
            throw new FormatException("Value is not a DPAPI-protected secret.");
        var bytes = Convert.FromBase64String(protectedValue[Prefix.Length..]);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.LocalMachine));
    }
}