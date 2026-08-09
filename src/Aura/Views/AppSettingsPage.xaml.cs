using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aura.Core.Notifications;
using Aura.Core.Settings;

namespace Aura.Views;

/// <summary>Настройки приложения. Всё применяется сразу — кнопки «Применить» здесь нет.</summary>
public partial class AppSettingsPage : PageBase
{
    private bool _loading;
    private readonly Button[] _positions = new Button[6];

    /// <summary>Порядок кнопок в сетке 3×2: верх слева → низ справа.</summary>
    private static readonly NotificationPosition?[] PositionOrder =
    [
        NotificationPosition.TopLeft, NotificationPosition.TopCenter, NotificationPosition.TopRight,
        NotificationPosition.BottomLeft, null, NotificationPosition.BottomRight
    ];

    public override string Title => "Приложение";

    public AppSettingsPage()
    {
        InitializeComponent();
        Duration.ValueChanged += Duration_Changed;
        BuildPositions();
        Loaded += (_, _) => Load();
    }

    public override void OnShown() => Load();

    private void BuildPositions()
    {
        for (int i = 0; i < PositionOrder.Length; i++)
        {
            var button = new Button
            {
                Style = (Style)FindResource("PositionCell"),
                Tag = PositionOrder[i],
                IsEnabled = PositionOrder[i] is not null,
                Margin = new Thickness(2)
            };
            button.Click += Position_Click;
            _positions[i] = button;
            PositionGrid.Children.Add(button);
        }
    }

    private void Load()
    {
        _loading = true;
        var s = Services.Settings.Current;

        AutoStart.IsChecked = s.AutoStartWithWindows;
        StartInTray.IsChecked = s.StartMinimizedToTray;
        AutoBuffer.IsChecked = s.AutoStartReplayBuffer;

        ShowNotifications.IsChecked = s.ShowNotifications;
        UpdateNotificationOptions();
        Duration.Value = s.NotificationDurationSeconds;
        DurationValue.Text = $"{s.NotificationDurationSeconds:0.#} с".Replace('.', ',');
        SelectTag(Sound, s.SaveSound.ToString());
        HighlightPosition(s.NotificationPosition);

        SelectTag(Theme, s.Theme.ToString());
        SelectTag(Scale, s.UiScale switch
        {
            0 => "0", 0.9 => "0.9", 1 => "1", 1.15 => "1.15", 1.35 => "1.35", _ => "0"
        });

        CheckUpdates.IsChecked = s.CheckForUpdates;
        DriverWatch.IsChecked = s.CheckNvidiaDriver;
        VersionText.Text = $"Версия {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        // Обновление могли уже найти при запуске — показываем его сразу, вместе
        // с кнопкой установки. Иначе переход по карточке «Есть обновление»
        // приводил на страницу, где ничего про обновление не сказано.
        if (Services.Updates.Available is { } found)
        {
            UpdateStatus.Text = $"Доступна версия {found.Version}";
            UpdateStatus.Foreground = (Brush)FindResource("AccentTxBrush");
            InstallButton.Visibility = Visibility.Visible;
            InstallButton.Tag = found;
        }
        else if (UpdateStatus.Text.Length == 0) UpdateStatus.Text = "Обновлений не искали";

        ShowThumbCacheSize();
        _loading = false;
    }

    // ---------------- Кэш превью ----------------

    /// <summary>Размер кэша считаем в фоне: это обход папки, иногда на тысячи файлов.</summary>
    private void ShowThumbCacheSize()
    {
        ThumbCacheSize.Text = "считаю…";
        _ = Task.Run(() =>
        {
            long bytes = Core.Library.ClipThumbnails.CacheBytes();
            Dispatcher.BeginInvoke(() => ThumbCacheSize.Text = bytes == 0
                ? "кэш пуст"
                : $"{Core.Storage.ByteSize.Format(bytes)} — кадры карточек, соберутся заново");
        });
    }

    private void ClearThumbs_Click(object sender, RoutedEventArgs e)
    {
        _ = Task.Run(() =>
        {
            Core.Library.ClipThumbnails.ClearCache();
            Dispatcher.BeginInvoke(ShowThumbCacheSize);
        });
    }

    private static void SelectTag(ItemsControl control, string tag)
    {
        foreach (var item in control.Items)
            if (item is FrameworkElement element && (string?)element.Tag == tag)
            {
                if (control is ListBox list) list.SelectedItem = item;
                if (control is ComboBox combo) combo.SelectedItem = item;
                return;
            }
        if (control is ListBox l) l.SelectedIndex = 0;
        if (control is ComboBox c) c.SelectedIndex = 0;
    }

    // ---------------- Обработчики ----------------

    private void System_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = Services.Settings.Current;
        s.AutoStartWithWindows = AutoStart.IsChecked == true;
        s.StartMinimizedToTray = StartInTray.IsChecked == true;
        s.AutoStartReplayBuffer = AutoBuffer.IsChecked == true;
        s.ShowNotifications = ShowNotifications.IsChecked == true;
        s.CheckForUpdates = CheckUpdates.IsChecked == true;
        s.CheckNvidiaDriver = DriverWatch.IsChecked == true;
        Services.Settings.Save("system");
    }

    /// <summary>Общий выключатель уведомлений заодно убирает их настройки с глаз.</summary>
    private void Notifications_Changed(object sender, RoutedEventArgs e)
    {
        UpdateNotificationOptions();
        System_Changed(sender, e);
    }

    private void UpdateNotificationOptions() =>
        NotificationOptions.Visibility =
            ShowNotifications.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void Duration_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        DurationValue.Text = $"{Duration.Value:0.#} с".Replace('.', ',');
        if (_loading) return;
        Services.Settings.Current.NotificationDurationSeconds = Duration.Value;
        Services.Settings.Save("ui");
    }

    private void Position_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not NotificationPosition position) return;
        Services.Settings.Current.NotificationPosition = position;
        Services.Settings.Save("ui");
        HighlightPosition(position);
        Services.Notifications.Show(NotificationKind.ReplayOn, "Уведомления теперь здесь");
    }

    private void HighlightPosition(NotificationPosition current)
    {
        for (int i = 0; i < _positions.Length; i++)
        {
            bool active = PositionOrder[i] == current;
            _positions[i].Background = active
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("TrackBrush");
        }
    }

    private void Sound_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var tag = (string?)((ComboBoxItem)Sound.SelectedItem).Tag ?? "Soft";
        var sound = Enum.Parse<SaveSound>(tag);

        if (sound == SaveSound.Custom)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Звук сохранения",
                Filter = "Звук (*.wav)|*.wav"
            };
            if (dialog.ShowDialog() == true)
                Services.Settings.Current.CustomSaveSoundPath = dialog.FileName;
            else { SelectTag(Sound, Services.Settings.Current.SaveSound.ToString()); return; }
        }

        Services.Settings.Current.SaveSound = sound;
        Services.Settings.Save("ui");
        PlaySound_Click(sender, e);
    }

    private void PlaySound_Click(object sender, RoutedEventArgs e)
    {
        var s = Services.Settings.Current;
        NotificationSounds.Play(s.SaveSound, s.CustomSaveSoundPath);
    }

    private void DemoReplay_Click(object sender, RoutedEventArgs e) =>
        Services.Notifications.Show(NotificationKind.Saved, "Повтор сохранён · 3:00", "Counter-Strike 2 · 214 МБ");

    private void DemoShot_Click(object sender, RoutedEventArgs e) =>
        Services.Notifications.Show(NotificationKind.Screenshot, "Скриншот сохранён", "2560×1440 · 4,1 МБ");

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Theme.SelectedItem is not ListBoxItem item) return;
        Services.Settings.Current.Theme = Enum.Parse<AppTheme>((string)item.Tag);
        Services.Settings.Save("ui");
        App.ApplyTheme(Services.Settings.Current.Theme);
    }

    private void Scale_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || Scale.SelectedItem is not ListBoxItem item) return;
        double value = double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture);
        Services.Settings.Current.UiScale = value;
        Services.Settings.Save("ui");
        (Window.GetWindow(this) as MainWindow)?.ApplyUiScale(value);
    }

    // ---------------- Обновления ----------------

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        UpdateStatus.Text = "Проверяю…";
        try
        {
            var info = await Services.Updates.CheckAsync();
            if (info is null)
            {
                UpdateStatus.Text = "Установлена последняя версия";
                InstallButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateStatus.Text = $"Доступна версия {info.Version}";
                UpdateStatus.Foreground = (Brush)FindResource("AccentTxBrush");
                InstallButton.Visibility = Visibility.Visible;
                InstallButton.Tag = info;
            }
        }
        catch (Exception ex) { UpdateStatus.Text = "Не удалось проверить: " + ex.Message; }
        finally { CheckButton.IsEnabled = true; }
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (InstallButton.Tag is not Core.SystemIntegration.UpdateInfo info) return;

        InstallButton.IsEnabled = false;
        UpdateProgress.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<double>(value => UpdateProgress.Value = value);
            string installer = await Services.Updates.DownloadAsync(info.DownloadUrl, progress);

            // Установщик сам погасит приложение, обновит файлы и запустит новую версию
            string? root = Core.SystemIntegration.UpdateService.InstallRoot;
            if (root is null || !Core.SystemIntegration.UpdateService.LaunchInstaller(installer, root))
                throw new InvalidOperationException("не удалось запустить установщик");
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Не удалось обновить: " + ex.Message;
            UpdateProgress.Visibility = Visibility.Collapsed;
            InstallButton.IsEnabled = true;
        }
    }

    // ---------------- Обслуживание ----------------

    private void OpenLogs_Click(object sender, RoutedEventArgs e) =>
        Open(Path.Combine(SettingsManager.Dir, "logs"));

    private void OpenSettings_Click(object sender, RoutedEventArgs e) =>
        Open(Path.Combine(SettingsManager.Dir, "settings.json"));

    private static void Open(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show("Вернуть все настройки к исходным?", "Aura",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        Services.Settings.Reset();
        App.ApplyTheme(Services.Settings.Current.Theme);
        (Window.GetWindow(this) as MainWindow)?.ApplyUiScale(Services.Settings.Current.UiScale);
        Load();
    }
}
