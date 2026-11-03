using System.Collections.Concurrent;

using Microsoft.Extensions.Configuration.Json;

namespace WatchTower.Configuration.Secrets;

/// <summary>
/// A JSON configuration source that transparently decrypts <c>dpapi:</c>-prefixed values, so
/// options classes see plain values while the file on disk only holds ciphertext.
/// </summary>
public sealed class ProtectedJsonConfigurationSource : JsonConfigurationSource
{
    public ISecretProtector Protector { get; init; } = DpapiSecretProtector.Instance;

    public override IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        EnsureDefaults(builder);
        return new ProtectedJsonConfigurationProvider(this);
    }
}

public sealed class ProtectedJsonConfigurationProvider(ProtectedJsonConfigurationSource source)
    : JsonConfigurationProvider(source)
{
    /// <summary>
    /// Keys whose secret could not be decrypted on the last load (e.g. the settings file was
    /// copied from another machine). Surfaced as a startup warning and in the UI.
    /// </summary>
    public ConcurrentDictionary<string, string> UndecryptableKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>All undecryptable secret keys across the protected providers of <paramref name="configuration"/>.</summary>
    public static IReadOnlyCollection<string> FindUndecryptableKeys(IConfiguration configuration) =>
        configuration is IConfigurationRoot root
            ? root.Providers.OfType<ProtectedJsonConfigurationProvider>().SelectMany(p => p.UndecryptableKeys.Keys).ToArray()
            : [];

    public override void Load(Stream stream)
    {
        base.Load(stream);
        UndecryptableKeys.Clear();

        foreach (var (key, value) in Data.ToArray())
        {
            if (!DpapiSecretProtector.IsProtected(value))
                continue;
            try
            {
                Data[key] = source.Protector.Unprotect(value!);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
            {
                // Never surface ciphertext as if it were the secret.
                Data[key] = string.Empty;
                UndecryptableKeys[key] = ex.Message;
            }
        }
    }
}

public static class ProtectedJsonConfigurationExtensions
{
    public static IConfigurationBuilder AddProtectedJsonFile(
        this IConfigurationBuilder builder, string path, bool optional, bool reloadOnChange,
        ISecretProtector? protector = null)
    {
        var directory = Path.GetDirectoryName(path)!;
        return builder.Add(new ProtectedJsonConfigurationSource
        {
            // Absolute file provider rooted at the file's folder so reload-on-change works
            // regardless of the content root.
            FileProvider = Directory.Exists(directory)
                ? new Microsoft.Extensions.FileProviders.PhysicalFileProvider(directory)
                : null,
            Path = Path.GetFileName(path),
            Optional = optional,
            ReloadOnChange = reloadOnChange,
            Protector = protector ?? DpapiSecretProtector.Instance,
        });
    }
}