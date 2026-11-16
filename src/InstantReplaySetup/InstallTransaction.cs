using System.IO;

namespace InstantReplaySetup;

/// <summary>Keep the previous binaries until all installation steps succeed.</summary>
internal sealed class InstallTransaction : IDisposable
{
    private readonly string _app;
    private readonly string _backup;
    private bool _installed;
    private bool _complete;

    public string StagingDirectory { get; }

    public InstallTransaction(string root)
    {
        root = Path.GetFullPath(root);
        RejectLinks(root);
        _app = Path.Combine(root, "app");
        RejectLinks(_app);
        string suffix = Guid.NewGuid().ToString("N");
        _backup = Path.Combine(root, "app.old-" + suffix);
        StagingDirectory = Path.Combine(root, "app.new-" + suffix);
        Directory.CreateDirectory(StagingDirectory);
    }

    internal static void ValidateTargetRoot(string root)
    {
        root = Path.GetFullPath(root);
        RejectLinks(root);
        if (Path.TrimEndingDirectorySeparator(root).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!), StringComparison.OrdinalIgnoreCase) ||
            (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() &&
             !File.Exists(Path.Combine(root, ".aura-install-root"))))
            throw new InvalidOperationException("Выберите пустую папку или существующую установку Aura.");
    }

    internal static void ValidateUpdateRoot(string root)
    {
        root = Path.GetFullPath(root);
        RejectLinks(root);
        if (!File.Exists(Path.Combine(root, ".aura-install-root")) ||
            !(File.Exists(Path.Combine(root, "app", "Aura.exe")) ||
              File.Exists(Path.Combine(root, "app", "InstantReplay.exe"))))
            throw new InvalidOperationException("Папка обновления не является установкой Aura.");
        RejectLinks(Path.Combine(root, "app"));
    }

    internal static void EnsureMarker(string root)
    {
        string marker = Path.Combine(root, ".aura-install-root");
        if (File.Exists(marker)) return;
        // CreateNew must fail if a file/link appeared after the existence check; never truncate its target.
        using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(file);
        writer.WriteLine("Aura installation root. This marker is required by AuraUninstall.exe.");
    }

    internal static bool IsLegacyInstallRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root)) return false;
            RejectLinks(root);
            string app = Path.Combine(root, "app");
            if (!Directory.Exists(app)) return false;
            RejectLinks(app);
            if (!File.Exists(Path.Combine(app, "Aura.exe")) &&
                !File.Exists(Path.Combine(app, "InstantReplay.exe")))
                return false;

            foreach (string entry in Directory.EnumerateFileSystemEntries(root))
            {
                string name = Path.GetFileName(entry);
                if (!name.Equals("app", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals(".aura-install-root", StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void RejectLinks(string path)
    {
        for (var dir = new DirectoryInfo(path); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Папка установки не должна проходить через ссылку: " + dir.FullName);
    }

    public void Install()
    {
        if (!File.Exists(Path.Combine(StagingDirectory, "Aura.exe")) ||
            !File.Exists(Path.Combine(StagingDirectory, "AuraUninstall.exe")))
            throw new InvalidDataException("В поставке нет Aura.exe или AuraUninstall.exe.");
        bool movedOld = false;
        if (Directory.Exists(_app))
        {
            Directory.Move(_app, _backup);
            movedOld = true;
        }
        try
        {
            Directory.Move(StagingDirectory, _app);
            _installed = true;
        }
        catch
        {
            if (movedOld && !Directory.Exists(_app) && Directory.Exists(_backup))
            {
                try { Directory.Move(_backup, _app); }
                catch { }
            }
            throw;
        }
    }

    public void Complete()
    {
        _complete = true;
        // A locked old binary may remain; it must not invalidate a successful install.
        try { if (Directory.Exists(_backup)) Directory.Delete(_backup, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (!_complete)
        {
            if (_installed)
            {
                try
                {
                    if (Directory.Exists(StagingDirectory))
                        Directory.Delete(StagingDirectory, recursive: true);
                    if (Directory.Exists(_app))
                        Directory.Move(_app, StagingDirectory);
                }
                catch
                {
                    try { if (Directory.Exists(_app)) Directory.Delete(_app, recursive: true); }
                    catch { }
                }
            }
            if (!Directory.Exists(_app) && Directory.Exists(_backup))
            {
                try { Directory.Move(_backup, _app); }
                catch { }
            }
        }
        try { if (Directory.Exists(StagingDirectory)) Directory.Delete(StagingDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
