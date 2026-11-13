using System.Runtime.Versioning;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Aura.Core.Tools;

[SupportedOSPlatform("windows")]
internal static class ExecutableTrust
{
    private const FileSystemRights Write = FileSystemRights.Write | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    // Elevated Aura must not execute a binary (or its DLLs) writable by ordinary processes.
    public static bool IsProtected(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full)!;
            if (root.StartsWith(@"\\") || new DriveInfo(root).DriveType == DriveType.Network) return false;
            var file = new FileInfo(full);
            if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                !Protected(file.GetAccessControl())) return false;
            for (var dir = file.Directory; dir is not null; dir = dir.Parent)
            {
                FileSystemRights rights = dir.FullName == file.DirectoryName ? Write :
                    FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                    FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0 || !Protected(dir.GetAccessControl(), rights)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    private static bool Trusted(string sid) => sid is "S-1-5-18" or "S-1-5-32-544" or
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static bool Protected(FileSystemSecurity security, FileSystemRights rights = Write)
    {
        if (!Trusted(security.GetOwner(typeof(SecurityIdentifier))!.Value)) return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & rights) != 0 && !Trusted(rule.IdentityReference.Value)) return false;
        return true;
    }

    public static string RequireProtected(string path) =>
        string.Equals(Path.GetFileName(path), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase) && IsProtected(path) ? Path.GetFullPath(path) :
        throw new InvalidOperationException("ffmpeg должен находиться в папке, защищённой от записи обычными пользователями (например, Program Files).");
}
