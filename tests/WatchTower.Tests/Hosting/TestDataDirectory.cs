using System.Text;

using WatchTower.Configuration.Secrets;
using WatchTower.Hosting;

namespace WatchTower.Tests.Hosting;

/// <summary>Throwaway install + data directories for host/config tests.</summary>
internal sealed class TestDataDirectory : IDisposable
{
    public TestDataDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "wt-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(Root, "install"));
        Paths = new WatchTowerPaths(Path.Combine(Root, "install"), Path.Combine(Root, "data"));
    }

    public string Root { get; }
    public WatchTowerPaths Paths { get; }

    public void WriteInstallFile(string name, string content) =>
        File.WriteAllText(Path.Combine(Paths.InstallDirectory, name), content);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* SQLite handles may linger briefly; temp dir is disposable */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Deterministic, reversible stand-in for DPAPI (same "dpapi:" envelope).</summary>
internal sealed class FakeSecretProtector : ISecretProtector
{
    public string Protect(string plaintext) =>
        DpapiSecretProtector.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes("enc|" + plaintext));

    public string Unprotect(string protectedValue)
    {
        var raw = Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue[DpapiSecretProtector.Prefix.Length..]));
        return raw.StartsWith("enc|", StringComparison.Ordinal)
            ? raw[4..]
            : throw new System.Security.Cryptography.CryptographicException("bad blob");
    }
}