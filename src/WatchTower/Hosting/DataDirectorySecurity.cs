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
    /// children), no inheritance from the parent — then resets EVERY descendant to inherit only
    /// that ACL (explicit grants removed, protected DACLs re-opened, owner = Administrators),
    /// equivalent to <c>icacls /reset /T</c>. Without the descendant pass, a pre-existing child
    /// (say <c>updates\</c>) with its own protected ACL could stay writable by ordinary users
    /// while SYSTEM later launches executables staged in it. Reparse points (junctions,
    /// symlinks) inside the tree are removed, never followed. Only callable when privileged.
    /// </summary>
    public static void Harden(string directory)
    {
        var root = new DirectoryInfo(directory);
        if (root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidOperationException($"The data directory '{directory}' is a junction or symbolic link.");

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(Administrators);

        root.SetAccessControl(security);
        ResetDescendants(root);
    }

    private static void ResetDescendants(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                // Removes the link itself (not its target).
                if (entry is DirectoryInfo link) link.Delete();
                else entry.Delete();
                continue;
            }

            switch (entry)
            {
                case DirectoryInfo dir:
                    var dirSecurity = new DirectorySecurity();
                    dirSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    dirSecurity.SetOwner(Administrators);
                    dir.SetAccessControl(dirSecurity);
                    ResetDescendants(dir);
                    break;
                case FileInfo file:
                    var fileSecurity = new FileSecurity();
                    fileSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                    fileSecurity.SetOwner(Administrators);
                    file.SetAccessControl(fileSecurity);
                    break;
            }
        }
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