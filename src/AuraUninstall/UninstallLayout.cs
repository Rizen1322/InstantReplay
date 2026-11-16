namespace AuraUninstall;

internal static class UninstallLayout
{
    internal const string InstallMarkerName = ".aura-install-root";

    /// <summary>
    /// Resolve the only directory the delayed cleanup may remove. Requiring the
    /// executable to live directly in an `app` child prevents a copied uninstaller
    /// from deleting an arbitrary parent directory.
    /// </summary>
    public static bool TryResolveInstallRoot(string? executable, out string root)
    {
        root = "";
        string? appDirectory = executable is null ? null : Path.GetDirectoryName(executable);
        if (appDirectory is null ||
            !Path.GetFileName(appDirectory).Equals("app", StringComparison.OrdinalIgnoreCase))
            return false;

        string? parent = Directory.GetParent(appDirectory)?.FullName;
        if (string.IsNullOrWhiteSpace(parent)) return false;

        string fullRoot = Path.GetFullPath(parent);
        if (Directory.GetParent(fullRoot) is null)
            return false;

        string expectedApp = Path.GetFullPath(Path.Combine(fullRoot, "app"));
        if (!expectedApp.Equals(Path.GetFullPath(appDirectory), StringComparison.OrdinalIgnoreCase))
            return false;

        if (HasReparsePoint(expectedApp))
            return false;

        root = fullRoot;
        return true;
    }

    public static bool HasReparsePoint(string path)
    {
        try
        {
            for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
                if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                    return true;
            return false;
        }
        catch
        {
            return true;
        }
    }

    public static bool IsSafeInstallRoot(string root, string? executablePath = null)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            string fullRoot = Path.GetFullPath(root);
            if (Directory.GetParent(fullRoot) is null) return false;
            string appDir = Path.Combine(fullRoot, "app");
            if (HasReparsePoint(fullRoot) || HasReparsePoint(appDir)) return false;

            string marker = Path.Combine(fullRoot, InstallMarkerName);
            var markerInfo = new FileInfo(marker);
            if (!markerInfo.Exists || (markerInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                return false;

            if (!OperatingSystem.IsWindows()) return false;

            string mainExe = Path.Combine(appDir, "Aura.exe");
            if (File.Exists(mainExe) && Aura.Core.Tools.ExecutableTrust.IsProtected(mainExe))
                return true;

            if (!string.IsNullOrWhiteSpace(executablePath) &&
                File.Exists(executablePath) &&
                Aura.Core.Tools.ExecutableTrust.IsProtected(executablePath))
                return true;

            return false;
        }
        catch
        {
            return false;
        }
    }
}
