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
    private const string AppName = "Aura";
    private const string ExeName = "Aura.exe";
    private const string ProcessName = "Aura";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Aura";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // Имя файла установщика (InstantReplaySetup.exe) менять НЕЛЬЗЯ: установленные
    // версии 1.0.x ищут в релизе именно его. Всё остальное — папки, ярлыки, процесс,
    // задача автозапуска — переезжает на Aura, а старые следы убираются.
    private const string OldAppName = "Instant Replay";
    private const string OldProcessName = "InstantReplay";
    private const string OldUninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\InstantReplay";
    private const string OldTaskName = "InstantReplay";

    private SoundPlayer? _music;
    private bool _muted;

    // Снимаются с UI ДО фоновой работы: PathBox трогать из другого потока нельзя
    private string _root = "", _appDir = "", _mainExe = "";

    private void SnapshotPaths()
    {
        _root = PathBox.Text.Trim();
        _appDir = Path.Combine(_root, "app");
        _mainExe = Path.Combine(_appDir, ExeName);
    }

    public MainWindow()
    {
        InitializeComponent();
        PathBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Aura");

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

        Title = "Обновление Aura";
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
                $"Не удалось обновить Aura:\n\n{ex.Message}\n\nПрежняя версия осталась на месте.",
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
                "Aura", "settings.json");
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
            PathBox.Text = Path.Combine(dlg.FolderName, "Aura");
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

        // Закрываем и новое приложение, и старое: при обновлении с 1.0.x работает
        // ещё InstantReplay.exe и держит файлы в папке установки.
        foreach (string name in new[] { ProcessName, OldProcessName })
            foreach (var p in Process.GetProcessesByName(name))
                try { p.Kill(); p.WaitForExit(3000); } catch { }

        MigrateFromInstantReplay();

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
                key.SetValue("Publisher", "Aura");
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
                run?.DeleteValue(OldProcessName, throwOnMissingValue: false);
                run?.DeleteValue(ProcessName, throwOnMissingValue: false);
            }
            catch { }
            RemoveOldTraces();
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
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aura");
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

    /// <summary>
    /// Переезд с прежней версии: настройки и кэш миниатюр лежали в
    /// %LocalAppData%\InstantReplay. Копируем их в %LocalAppData%\Aura, если там
    /// ещё пусто — иначе после обновления человек увидел бы чистое приложение
    /// с настройками по умолчанию и не своей папкой записей.
    /// </summary>
    private static void MigrateFromInstantReplay()
    {
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string old = Path.Combine(local, "InstantReplay");
            string now = Path.Combine(local, "Aura");
            if (!Directory.Exists(old)) return;
            if (File.Exists(Path.Combine(now, "settings.json"))) return;   // уже переехали

            Directory.CreateDirectory(now);
            foreach (string file in Directory.GetFiles(old))
                File.Copy(file, Path.Combine(now, Path.GetFileName(file)), overwrite: false);

            string oldThumbs = Path.Combine(old, "thumbs");
            if (Directory.Exists(oldThumbs))
            {
                string newThumbs = Path.Combine(now, "thumbs");
                Directory.CreateDirectory(newThumbs);
                foreach (string file in Directory.GetFiles(oldThumbs))
                    File.Copy(file, Path.Combine(newThumbs, Path.GetFileName(file)), overwrite: false);
            }
            Log("Настройки перенесены из InstantReplay");
        }
        catch (Exception ex) { Log($"Перенос настроек: {ex.Message}"); }
    }

    /// <summary>
    /// Следы прежней версии: ярлыки «Instant Replay», запись в «Установленных
    /// программах» и задача автозапуска. Файлы приложения чистить не нужно —
    /// папка app\ пересоздаётся с нуля при каждой установке.
    /// </summary>
    private void RemoveOldTraces()
    {
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                               "Programs", $"{OldAppName}.lnk"));
        TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                               $"{OldAppName}.lnk"));
        try { Registry.CurrentUser.DeleteSubKeyTree(OldUninstallKey, throwOnMissingSubKey: false); } catch { }
        try
        {
            Process.Start(new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{OldTaskName}\" /F")
            { UseShellExecute = false, CreateNoWindow = true })?.WaitForExit(5000);
        }
        catch { }
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
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aura");
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
        Title = "Удаление Aura";
        SubTitle.Text = "Приложение будет удалено. Ваши записи останутся на месте.";
        PageOptions.Visibility = Visibility.Collapsed;
        PageDone.Visibility = Visibility.Visible;
        DoneTitle.Text = "Удалить Aura?";
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
            foreach (string name in new[] { ProcessName, OldProcessName })
                foreach (var p in Process.GetProcessesByName(name))
                    try { p.Kill(); p.WaitForExit(3000); } catch { }

            SetStatus("Удаление ярлыков…", 40);
            foreach (string name in new[] { AppName, OldAppName })
            {
                TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", $"{name}.lnk"));
                TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{name}.lnk"));
            }

            SetStatus("Очистка реестра…", 60);
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(OldUninstallKey, throwOnMissingSubKey: false); } catch { }
            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                run?.DeleteValue(ProcessName, throwOnMissingValue: false);
                run?.DeleteValue(OldProcessName, throwOnMissingValue: false);
            }
            catch { }
            // Задачи автозапуска в Планировщике — и новая, и от прежней версии
            foreach (string task in new[] { AppName, OldTaskName })
                try
                {
                    Process.Start(new ProcessStartInfo("schtasks.exe", $"/Delete /TN \"{task}\" /F")
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
