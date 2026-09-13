namespace AuraUninstall;

internal static class UninstallLayout
{
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

        root = Path.GetFullPath(parent);
        if (Directory.GetParent(root) is null)
        {
            root = "";
            return false;
        }

        string expectedApp = Path.GetFullPath(Path.Combine(root, "app"));
        return expectedApp.Equals(Path.GetFullPath(appDirectory), StringComparison.OrdinalIgnoreCase);
    }
}
