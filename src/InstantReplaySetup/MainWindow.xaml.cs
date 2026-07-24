using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Media;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace InstantReplaySetup;

public partial class MainWindow : Window
{
    private const string AppName = "Instant Replay";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\InstantReplay";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private SoundPlayer? _music;
    private bool _muted;

    // Снимаются с UI ДО фоновой работы: PathBox трогать из другого потока нельзя
    private string _root = "", _appDir = "", _mainExe = "";

    private void SnapshotPaths()
    {
        _root = PathBox.Text.Trim();
        _appDir = Path.Combine(_root, "app");
        _mainExe = Path.Combine(_appDir, "InstantReplay.exe");
    }

    public MainWindow()
    {
        InitializeComponent();
        PathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "InstantReplay");

        long payload = GetPayloadSize();
        SizeText.Text = payload > 0 ? $"потребуется ~{payload * 2.2 / (1024 * 1024):0} МБ" : "";

        if (App.UpdateMode) { StartUpdate(); return; }

        StartMusic();

        if (App.UninstallMode) SwitchToUninstall();
    }

    // ---------------- Тихое обновление ----------------

    /// <summary>
    /// Режим «/update &lt;папка&gt;»: без музыки, вопросов и кнопок — сразу ставим новую
    /// версию в ту же папку и запускаем приложение. Ярлыки и запись в реестре
    /// обновляются попутно (пути те же), настройки и записи не трогаются.
    /// </summary>
    private async void StartUpdate()
    {
        if (!string.IsNullOrWhiteSpace(App.UpdateTarget))
            PathBox.Text = App.UpdateTarget!;
        SnapshotPaths();

        Title = "Обновление Instant Replay";
        TitleMode.Text = "· обновление";
        ProgressTitle.Text = "Обновление…";
        PageOptions.Visibility = Visibility.Collapsed;
        PageProgress.Visibility = Visibility.Visible;

        // Автозапуск при обновлении не переключаем: текущий выбор пользователя
        // уже лежит в settings.json, приложение сверит его само при старте.
        bool keepAutostart = ReadAutostartSetting();

        try
        {
            await Task.Run(() => DoInstall(desktopShortcut: false, autostart: keepAutostart));
            if (File.Exists(_mainExe))
                Process.Start(new ProcessStartInfo(_mainExe)
                { WorkingDirectory = _appDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log($"Обновление не удалось: {ex.Message}");
            MessageBox.Show(this,
                $"Не удалось обновить Instant Replay:\n\n{ex.Message}\n\nПрежняя версия осталась на месте.",
                "Ошибка обновления", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Close();
    }

    /// <summary>Текущее значение автозапуска из settings.json (чтобы не сбросить его обновлением).</summary>
    private static bool ReadAutostartSetting()
    {
        try
        {
            string file = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "InstantReplay", "settings.json");
            if (!File.Exists(file)) return false;
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file));
            return node?["AutoStartWithWindows"]?.GetValue<bool>() ?? false;
        }
        catch { return false; }
    }

    // ---------------- Музыка ----------------

    private void StartMusic()
    {
        try
        {
            // Приоритет — файл рядом с установщиком (можно подменить без пересборки),
            // иначе — встроенный setup_music.wav из корня репозитория.
            string beside = Path.Combine(AppContext.BaseDirectory, "setup_music.wav");
            if (File.Exists(beside))
                _music = new SoundPlayer(beside);
            else
            {
                var stream = OpenResource("setup_music.wav");
                if (stream is null) return;
                _music = new SoundPlayer(stream);
            }
            _music.PlayLooping();
        }
        catch { /* музыка — не повод падать */ }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _muted = !_muted;
        MuteBtn.Content = _muted ? "🔇" : "🔊";
        try
        {
            if (_muted) _music?.Stop();
            else _music?.PlayLooping();
        }
        catch { }
    }

    // ---------------- Ресурсы ----------------

    private static Stream? OpenResource(string nameEndsWith)
    {
        var asm = Assembly.GetExecutingAssembly();
        string? res = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(nameEndsWith, StringComparison.OrdinalIgnoreCase));
        return res is null ? null : asm.GetManifestResourceStream(res);
    }

    private static long GetPayloadSize()
    {
        using var s = OpenResource("payload.zip");
        return s?.Length ?? 0;
    }

    // ---------------- Установка ----------------

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Папка установки" };
        if (dlg.ShowDialog() == true)
            PathBox.Text = Path.Combine(dlg.FolderName, "InstantReplay");
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        SnapshotPaths();
        if (string.IsNullOrWhiteSpace(_root)) return;

        PageOptions.Visibility = Visibility.Collapsed;
        PageProgress.Visibility = Visibility.Visible;

        bool desktop = DesktopShortcut.IsChecked == true;
        bool autostart = Autostart.IsChecked == true;

        try
        {
            await Task.Run(() => DoInstall(desktop, autostart));
            DoneText.Text = $"Instant Replay установлен в\n{_root}";
            PageProgress.Visibility = Visibility.Collapsed;
            PageDone.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка установки", MessageBoxButton.OK, MessageBoxImage.Error);
            PageProgress.Visibility = Visibility.Collapsed;
            PageOptions.Visibility = Visibility.Visible;
        }
    }

    private void DoInstall(bool desktopShortcut, bool autostart)
    {
        SetStatus("Подготовка…", 2);

        // Если приложение запущено — мягко закрываем
        foreach (var p in Process.GetProcessesByName("InstantReplay"))
            try { p.Kill(); p.WaitForExit(3000); } catch { }

        Directory.CreateDirectory(_root);

        // Чистая переустановка: иначе от прошлых версий остаются лишние файлы
        // (языковые папки, выпиленные библиотеки) и папка «пухнет».
        // Пользовательские данные тут не живут — настройки и записи лежат отдельно.
        if (Directory.Exists(_appDir))
        {
            SetStatus("Удаление предыдущей версии…", 3);
            try { Directory.Delete(_appDir, recursive: true); }
            catch (Exception ex) { Log($"Не удалось очистить {_appDir}: {ex.Message}"); }
        }
        Directory.CreateDirectory(_appDir);

        // Распаковка полезной нагрузки: бинарники аккуратно в подпапку app\
        using (var stream = OpenResource("payload.zip")
            ?? throw new InvalidOperationException("В установщике нет полезной нагрузки (payload.zip). Соберите через build_setup.ps1."))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            int total = zip.Entries.Count, done = 0;
            long bytes = 0;
            foreach (var entry in zip.Entries)
            {
                string target = Path.Combine(_appDir, entry.FullName);
                if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
                bytes += entry.Length;
                done++;
                if (done % 12 == 0 || done == total)
                    SetStatus($"Распаковка файлов… {done} из {total}", 4 + done * 82.0 / total);
            }

            SetStatus("Создание ярлыков…", 90);
            string startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
            CreateShortcut(Path.Combine(startMenu, $"{AppName}.lnk"));
            if (desktopShortcut)
                CreateShortcut(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk"));

            SetStatus("Регистрация…", 95);
            // Копия установщика = деинсталлятор
            string uninstaller = Path.Combine(_root, "Uninstall.exe");
            try { File.Copy(Environment.ProcessPath!, uninstaller, overwrite: true); } catch { }

            using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", InstalledVersion());
                key.SetValue("Publisher", "InstantReplay");
                key.SetValue("DisplayIcon", _mainExe);
                key.SetValue("InstallLocation", _root);
                key.SetValue("UninstallString", $"\"{uninstaller}\" /uninstall");
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
            }

            // Автозапуск создаётся не здесь: приложение работает от администратора,
            // и задачу Планировщика (RunLevel=Highest) может создать только elevated
            // процесс. Установщик лишь записывает выбор в настройки — приложение при
            // первом запуске (с правами админа) само заведёт задачу через Reconcile.
            // Заодно чистим устаревший ключ Run от прежних версий.
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                run?.DeleteValue("InstantReplay", throwOnMissingValue: false);
            }
            catch { }
            SyncAutostartSetting(autostart);
        }
        SetStatus("Готово", 100);
    }

    /// <summary>
    /// Версия из только что распакованного exe — раньше здесь была захардкоженная
    /// строка, и «Установленные программы» показывали 1.0.0 после любого обновления.
    /// </summary>
    private string InstalledVersion()
    {
        try
        {
            if (File.Exists(_mainExe))
            {
                string? v = FileVersionInfo.GetVersionInfo(_mainExe).FileVersion;
                if (!string.IsNullOrWhiteSpace(v)) return v!;
            }
        }
        catch { }
        return "1.0.0";
    }

    /// <summary>Записывает AutoStartWithWindows в settings.json приложения (создаёт при отсутствии).</summary>
    private static void SyncAutostartSetting(bool enabled)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InstantReplay");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "settings.json");

            System.Text.Json.Nodes.JsonNode? node = null;
            if (File.Exists(file))
                try { node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)); } catch { }
            node ??= new System.Text.Json.Nodes.JsonObject();

            node["AutoStartWithWindows"] = enabled;
            File.WriteAllText(file, node.ToJsonString(
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* не критично для установки */ }
    }

    private void CreateShortcut(string lnkPath)
    {
        Type? t = Type.GetTypeFromProgID("WScript.Shell");
        if (t is null) return;
        dynamic shell = Activator.CreateInstance(t)!;
        try
        {
            var sc = shell.CreateShortcut(lnkPath);
            sc.TargetPath = _mainExe;
            sc.WorkingDirectory = _appDir;
            sc.IconLocation = _mainExe + ",0";
            sc.Description = "Мгновенные повторы геймплея";
            sc.Save();
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
        }
    }

    private static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InstantReplay");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "setup.log"),
                $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void SetStatus(string text, double percent) => Dispatcher.Invoke(() =>
    {
        ProgressStatus.Text = text;
        Progress.Value = percent;
    });

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        if (!App.UninstallMode && LaunchAfter.IsChecked == true && File.Exists(_mainExe))
            Process.Start(new ProcessStartInfo(_mainExe) { WorkingDirectory = _appDir, UseShellExecute = true });
        Close();
    }

    // ---------------- Деинсталляция ----------------

    private void SwitchToUninstall()
    {
        TitleMode.Text = "· удаление";
        Title = "Удаление Instant Replay";
        SubTitle.Text = "Приложение будет удалено. Ваши записи останутся на месте.";
        PageOptions.Visibility = Visibility.Collapsed;
        PageDone.Visibility = Visibility.Visible;
        DoneTitle.Text = "Удалить Instant Replay?";
        DoneText.Text = "Записи и настройки не удаляются.";
        DoneBtn.Content = "Удалить";
        DoneBtn.Click -= Done_Click;
        DoneBtn.Click += Uninstall_Click;
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        PageDone.Visibility = Visibility.Collapsed;
        PageProgress.Visibility = Visibility.Visible;
        ProgressTitle.Text = "Удаление…";

        string root = Path.GetDirectoryName(Environment.ProcessPath!)!;
        await Task.Run(() =>
        {
            SetStatus("Закрытие приложения…", 15);
            foreach (var p in Process.GetProcessesByName("InstantReplay"))
                try { p.Kill(); p.WaitForExit(3000); } catch { }

            SetStatus("Удаление ярлыков…", 40);
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", $"{AppName}.lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk"));

            SetStatus("Очистка реестра…", 60);
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                run?.DeleteValue("InstantReplay", throwOnMissingValue: false);
            }
            catch { }
            // Задача автозапуска в Планировщике
            try
            {
                Process.Start(new ProcessStartInfo("schtasks.exe", "/Delete /TN \"InstantReplay\" /F")
                { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000);
            }
            catch { }

            SetStatus("Удаление файлов…", 80);
            try { Directory.Delete(Path.Combine(root, "app"), recursive: true); } catch { }
        });

        // Сам установщик удаляем отложенно (файл занят, пока процесс жив)
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{root}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        Close();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---------------- Окно ----------------

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
