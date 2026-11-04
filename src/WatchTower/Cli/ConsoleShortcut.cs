namespace WatchTower.Cli;

/// <summary>
/// "WatchTower Console" entry in the all-users Start menu, so administrators can find the web
/// console without remembering its address. An Internet Shortcut (.url) needs no COM/shell APIs.
/// </summary>
internal static class ConsoleShortcut
{
    public static string PathFor(string serviceName)
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        var name = string.Equals(serviceName, Hosting.WatchTowerHost.DefaultServiceName, StringComparison.OrdinalIgnoreCase)
            ? "WatchTower Console.url"
            : $"WatchTower Console ({Sanitize(serviceName)}).url";
        return Path.Combine(programs, name);
    }

    public static void Create(string serviceName, string url, string iconExecutable)
    {
        try
        {
            File.WriteAllText(PathFor(serviceName),
                $"[InternetShortcut]\r\nURL={url}\r\nIconFile={iconExecutable}\r\nIconIndex=0\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cosmetic only (e.g. Server Core has no Start menu); `WatchTower open` still works.
        }
    }

    public static void Remove(string serviceName)
    {
        try
        {
            File.Delete(PathFor(serviceName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}