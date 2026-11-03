using System.Security.AccessControl;
using System.Security.Principal;

namespace WatchTower.Hosting;

/// <summary>
/// Restricts the data directory (which holds encrypted secrets, the metrics database and
/// logs) to LocalSystem and the local Administrators group.
/// </summary>
public static class DataDirectorySecurity
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>True when the current process runs as LocalSystem or with an elevated admin token.</summary>
    public static bool IsPrivileged()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not null && identity.User.Equals(LocalSystem))
            return true;
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Replaces the directory ACL with: SYSTEM + Administrators full control (inherited by
    /// children), no inheritance from the parent. Only callable from a privileged process.
    /// </summary>
    public static void Harden(string directory)
    {
        var info = new DirectoryInfo(directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(Administrators);

        info.SetAccessControl(security);
    }

    /// <summary>Hardens the directory when privileged; returns false (and does nothing) otherwise.</summary>
    public static bool TryHarden(string directory, out string? error)
    {
        error = null;
        if (!IsPrivileged())
            return false;
        try
        {
            Harden(directory);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException
                                       or PrivilegeNotHeldException)
        {
            error = ex.Message;
            return false;
        }
    }
}