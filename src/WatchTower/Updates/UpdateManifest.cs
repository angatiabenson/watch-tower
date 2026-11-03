using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace WatchTower.Updates;

/// <summary>
/// <c>watchtower-update.json</c> — published with every release (as a GitHub release asset or
/// at a self-hosted URL). Describes the downloadable package per runtime.
/// </summary>
public sealed record UpdateManifest
{
    public int Schema { get; init; } = 1;
    public required string Version { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Notes { get; init; }
    public bool Prerelease { get; init; }

    /// <summary>Optional: installs older than this must upgrade manually.</summary>
    public string? MinimumVersion { get; init; }

    public required IReadOnlyList<UpdateAsset> Assets { get; init; }

    [JsonIgnore]
    public SemanticVersion SemVer => SemanticVersion.Parse(Version);
}

public sealed record UpdateAsset
{
    /// <summary>Runtime identifier, e.g. <c>win-x64</c>.</summary>
    public required string Rid { get; init; }
    public required string Url { get; init; }
    public long Size { get; init; }

    /// <summary>Lower-case hex SHA-256 of the package.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Base64 ECDSA P-256 (IEEE P1363) signature of <see cref="UpdateSignature.Message"/>.</summary>
    public string? Signature { get; init; }
}

/// <summary>Package signature: binds version, runtime and content hash under the publisher's key.</summary>
public static class UpdateSignature
{
    public static byte[] Message(string version, string rid, string sha256) =>
        Encoding.UTF8.GetBytes($"watchtower-update|{version}|{rid}|{sha256.ToLowerInvariant()}");

    public static bool Verify(string publicKeyPem, string version, UpdateAsset asset, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(asset.Signature))
        {
            error = "the package is not signed, but a signing key is configured";
            return false;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            var ok = ecdsa.VerifyData(Message(version, asset.Rid, asset.Sha256),
                Convert.FromBase64String(asset.Signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            if (!ok)
                error = "the package signature is invalid";
            return ok;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
        {
            error = $"the signature could not be verified: {ex.Message}";
            return false;
        }
    }

    /// <summary>Produces a signature (used by the release pipeline and tests).</summary>
    public static string Sign(ECDsa privateKey, string version, string rid, string sha256) =>
        Convert.ToBase64String(privateKey.SignData(Message(version, rid, sha256),
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}