using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using InstantReplay.Core.Notifications;
using InstantReplay.Core.Settings;
using InstantReplay.Core.SystemIntegration;

namespace InstantReplay.Views;

public sealed partial class AppPage : Page
{
    private bool _loading = true;

    public AppPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        _loading = true;
        var s = Services.Settings.Current;

        // Реальное состояние реестра — источник правды (запись мог удалить
        // установщик/чистильщик, тогда переключатель врал бы «включено»)
        AutostartToggle.IsOn = StartupManager.IsEnabled();
        TrayStartToggle.IsOn = s.StartMinimizedToTray;
        AutoBufferToggle.IsOn = s.AutoStartReplayBuffer;
        UpdatesToggle.IsOn = s.CheckForUpdates;
        RepoBox.Text = s.UpdateRepo;

        NotifyToggle.IsOn = s.ShowNotifications;
        SelectByTag(NotifyPosBox, s.NotificationPosition.ToString());
        DurationBox.Value = s.NotificationDurationSeconds;
        SelectByTag(SoundBox, s.SaveSound.ToString());
        OpenAfterToggle.IsOn = s.OpenFolderAfterSave;
        SelectByTag(ThemeBox, s.Theme.ToString());

        string ver = typeof(AppPage).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        FooterText.Text = $"Instant Replay {ver}  ·  настройки: {Path.Combine(SettingsManager.Dir, "settings.json")}";
        VersionText.Text = $"Установленная версия: {ver}";
        UpdateStatus.Text = string.IsNullOrWhiteSpace(s.UpdateRepo)
            ? "Репозиторий не указан — проверка обновлений отключена"
            : "Нажми «Проверить», чтобы узнать о новой версии";

        _loading = false;
    }

    // ---------------- Обновления ----------------

    private UpdateInfo? _pendingUpdate;

    private void Repo_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string repo = RepoBox.Text.Trim();
        if (repo == Services.Settings.Current.UpdateRepo) return;
        Services.Settings.Current.UpdateRepo = repo;
        Services.Settings.Save("system");
        UpdateStatus.Text = string.IsNullOrWhiteSpace(repo)
            ? "Репозиторий не указан — проверка обновлений отключена"
            : "Нажми «Проверить», чтобы узнать о новой версии";
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        string repo = RepoBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(repo))
        {
            ShowUpdateBar(InfoBarSeverity.Warning, "Не указан репозиторий",
                "Впиши «владелец/репозиторий» в поле ниже — оттуда берутся релизы.");
            return;
        }
        Repo_Changed(sender, e); // сохранить, если правили и сразу нажали

        CheckBtn.IsEnabled = false;
        UpdateStatus.Text = "Проверяю…";
        UpdatePanel.Visibility = Visibility.Collapsed;
        UpdateWarnBar.IsOpen = false;
        try
        {
            _pendingUpdate = await Services.Updates.CheckAsync(repo);
            if (_pendingUpdate is null)
            {
                UpdateStatus.Text = $"Обновлений нет — установлена последняя версия " +
                                    $"({UpdateService.CurrentVersion.ToString(3)})";
            }
            else
            {
                UpdateStatus.Text = $"Доступна версия {_pendingUpdate.Version}";
                UpdateProgress.Value = 0;
                InstallUpdateBtn.IsEnabled = true;
                InstallUpdateBtn.Content = "Скачать и установить";
                UpdatePanel.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Проверить не удалось";
            ShowUpdateBar(InfoBarSeverity.Error, "Ошибка проверки", ex.Message);
        }
        finally { CheckBtn.IsEnabled = true; }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        if (string.IsNullOrWhiteSpace(_pendingUpdate.DownloadUrl))
        {
            ShowUpdateBar(InfoBarSeverity.Warning, "Нет файла установщика",
                "В релизе не нашёлся .exe — открой страницу релиза и обнови вручную.");
            return;
        }

        // Обновление закроет приложение: предупреждаем, если идёт запись в файл
        if (Services.Engine.IsRecordingToFile)
        {
            var warn = new ContentDialog
            {
                Title = "Идёт запись в файл",
                Content = "Обновление закроет приложение и прервёт текущую запись. Продолжить?",
                PrimaryButtonText = "Продолжить",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await warn.ShowAsync() != ContentDialogResult.Primary) return;
        }

        InstallUpdateBtn.IsEnabled = false;
        InstallUpdateBtn.Content = "Скачиваю…";
        UpdateWarnBar.IsOpen = false;

        try
        {
            var progress = new Progress<double>(p => Services.Dispatcher.Enqueue(() =>
            {
                UpdateProgress.IsIndeterminate = p < 0;
                if (p >= 0)
                {
                    UpdateProgress.Value = p;
                    InstallUpdateBtn.Content = $"Скачиваю… {p * 100:0}%";
                }
            }));

            string installer = await Services.Updates.DownloadAsync(_pendingUpdate.DownloadUrl, progress);

            InstallUpdateBtn.Content = "Запускаю установщик…";
            if (!UpdateService.LaunchInstaller(installer, UpdateService.InstallRoot))
            {
                ShowUpdateBar(InfoBarSeverity.Error, "Не удалось запустить установщик",
                    "Запусти скачанный файл вручную: " + installer);
                InstallUpdateBtn.IsEnabled = true;
                InstallUpdateBtn.Content = "Скачать и установить";
                return;
            }

            // Установщик сам закроет приложение, но выходим сами — так движок
            // корректно освободит энкодер и захват, а не будет убит на полуслове.
            await Task.Delay(400);
            (Application.Current as App)?.ExitApp();
        }
        catch (Exception ex)
        {
            UpdateProgress.Value = 0;
            InstallUpdateBtn.IsEnabled = true;
            InstallUpdateBtn.Content = "Скачать и установить";
            ShowUpdateBar(InfoBarSeverity.Error, "Не удалось скачать обновление", ex.Message);
        }
    }

    private void OpenRelease_Click(object sender, RoutedEventArgs e)
    {
        string url = _pendingUpdate?.ReleaseUrl ?? "";
        if (string.IsNullOrWhiteSpace(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void ShowUpdateBar(InfoBarSeverity severity, string title, string message)
    {
        UpdateWarnBar.Severity = severity;
        UpdateWarnBar.Title = title;
        UpdateWarnBar.Message = message;
        UpdateWarnBar.IsOpen = true;
    }

    private void System_Changed(object sender, object e)
    {
        if (_loading) return;
        var s = Services.Settings.Current;
        s.AutoStartWithWindows = AutostartToggle.IsOn;
        s.StartMinimizedToTray = TrayStartToggle.IsOn;
        s.AutoStartReplayBuffer = AutoBufferToggle.IsOn;
        s.CheckForUpdates = UpdatesToggle.IsOn;
        s.ShowNotifications = NotifyToggle.IsOn;
        if (Enum.TryParse(Tag(NotifyPosBox), out NotificationPosition np)) s.NotificationPosition = np;
        s.OpenFolderAfterSave = OpenAfterToggle.IsOn;
        if (Enum.TryParse(Tag(ThemeBox), out AppTheme th)) s.Theme = th;
        Services.Settings.Save("system");
        (WindowTracker.Main as MainWindow)?.ApplyTheme();
    }

    private void Duration_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading || double.IsNaN(sender.Value)) return;
        Services.Settings.Current.NotificationDurationSeconds = Math.Clamp(sender.Value, 1, 15);
        Services.Settings.Save("system");
    }

    private async void Sound_Changed(object sender, object e)
    {
        if (_loading) return;
        var s = Services.Settings.Current;
        if (!Enum.TryParse(Tag(SoundBox), out SaveSound sound)) return;

        if (sound == SaveSound.Custom)
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".wav");
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(WindowTracker.Main!);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                SelectByTag(SoundBox, s.SaveSound.ToString()); // откат выбора
                return;
            }
            s.CustomSaveSoundPath = file.Path;
        }

        s.SaveSound = sound;
        Services.Settings.Save("system");
        NotificationSounds.Play(sound, s.CustomSaveSoundPath);
    }

    private void PlaySound_Click(object sender, RoutedEventArgs e)
    {
        var s = Services.Settings.Current;
        NotificationSounds.Play(s.SaveSound, s.CustomSaveSoundPath);
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
        OpenPath(Path.Combine(SettingsManager.Dir, "logs"));

    private void OpenSettings_Click(object sender, RoutedEventArgs e) =>
        OpenPath(SettingsManager.Dir);

    private static void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Сбросить все настройки?",
            Content = "Все параметры вернутся к значениям по умолчанию. Записи не удаляются.",
            PrimaryButtonText = "Сбросить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        Services.Settings.Reset();
        LoadFromSettings();
        (WindowTracker.Main as MainWindow)?.ApplyTheme();
    }

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
            if (item is ComboBoxItem c && (string?)c.Tag == tag) { box.SelectedItem = c; return; }
    }

    private static string Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
}
