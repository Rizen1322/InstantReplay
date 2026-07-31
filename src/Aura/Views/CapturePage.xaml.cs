using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Aura.Controls;
using Aura.Core.Capture;
using Aura.Core.Encoding;
using Aura.Core.Engine;
using Aura.Core.Settings;

namespace Aura.Views;

/// <summary>
/// Настройки захвата. Параметры кодировщика копятся до «Применить»: менять их
/// на ходу нельзя — конвейер пересобирается, и запись прервалась бы посреди игры.
/// Звук и шумодав применяются сразу.
/// </summary>
public partial class CapturePage : PageBase
{
    private readonly DispatcherTimer _levels = new() { Interval = TimeSpan.FromMilliseconds(90) };
    private bool _loading;
    private bool _dirty;
    private List<VideoCodec> _supported = [VideoCodec.H264, VideoCodec.HEVC];

    public override string Title => "Захват";

    /// <summary>Готовый набор: одним нажатием ставит разрешение, кадры, битрейт и кодек.</summary>
    private sealed record Preset(string Name, string Detail, int Height, int Fps, int Bitrate, VideoCodec Codec);

    private static readonly Preset[] AllPresets =
    [
        new("Экономный", "720p · 30 · меньше всего места", 720, 30, 12, VideoCodec.H264),
        new("Обычный", "1080p · 60 · для стримов и клипов", 1080, 60, 35, VideoCodec.H264),
        new("Высокий", "1440p · 60 · чёткая картинка", 1440, 60, 55, VideoCodec.HEVC),
        new("Максимум", "4K · 60 · для монтажа", 2160, 60, 75, VideoCodec.HEVC)
    ];

    public CapturePage()
    {
        InitializeComponent();

        // Подписка ПОСЛЕ разбора разметки: присвоение Minimum само поднимает
        // ValueChanged, а обработчик читает поля, которых в тот момент ещё нет.
        Bitrate.ValueChanged += Bitrate_Changed;
        Gate.ValueChanged += Gate_Changed;

        BuildPresets();
        BuildLengths();
        _levels.Tick += (_, _) => ShowLevels();
        Loaded += (_, _) => LoadFromSettings();
    }

    public override void OnShown()
    {
        LoadFromSettings();
        _levels.Start();
    }

    public override void OnHidden()
    {
        _levels.Stop();
        (Window.GetWindow(this) as MainWindow)?.HideApplyBar();
    }

    // ---------------- Сборка ----------------

    private void BuildPresets()
    {
        foreach (var preset in AllPresets)
        {
            var button = new Button
            {
                Style = (Style)FindResource("ActionTile"),
                Tag = preset,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = preset.Name, FontSize = 13.5, FontWeight = FontWeights.SemiBold },
                        new TextBlock
                        {
                            Text = preset.Detail, Style = (Style)FindResource("RowSub"),
                            Margin = new Thickness(0, 4, 0, 0)
                        }
                    }
                }
            };
            button.Click += Preset_Click;
            Presets.Children.Add(button);
        }
    }

    private void BuildLengths()
    {
        foreach (int seconds in new[] { 30, 60, 180, 300, 600, 900 })
        {
            var button = new Button
            {
                Style = (Style)FindResource("Btn"),
                Content = LengthText(seconds),
                Tag = seconds,
                Margin = new Thickness(0, 0, 7, 7),
                Height = 30
            };
            button.Click += Length_Click;
            Lengths.Children.Add(button);
        }
    }

    private static string LengthText(int seconds) =>
        seconds < 60 ? $"{seconds} сек" : $"{seconds / 60} мин";

    private void BuildCodecs()
    {
        Codecs.Children.Clear();
        (VideoCodec Codec, string Name, string Detail, string Tile, string Color)[] all =
        [
            (VideoCodec.H264, "H.264", "Открывается везде и всегда", "Ico.Film", "BlueBrush"),
            (VideoCodec.HEVC, "HEVC", "Файлы вдвое меньше при том же качестве", "Ico.Film", "PurpleBrush"),
            (VideoCodec.AV1, "AV1", "Самые лёгкие файлы, нужна новая видеокарта", "Ico.Cpu", "GrayBrush")
        ];

        bool first = true;
        foreach (var (codec, name, detail, tile, color) in all)
        {
            if (!first) Codecs.Children.Add(new Border { Style = (Style)FindResource("RowSeparator"), Margin = new Thickness(52, 0, 0, 0) });
            first = false;

            bool available = _supported.Contains(codec);
            var row = new AdaptiveRow { Margin = new Thickness(16, 12, 16, 12) };
            row.Children.Add(new IconTile
            {
                Data = (Geometry)FindResource(tile),
                Background = (Brush)FindResource(color)
            });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = name, Style = (Style)FindResource("RowLabel") });
            text.Children.Add(new TextBlock
            {
                Text = available ? detail : "Эта видеокарта не умеет",
                Style = (Style)FindResource("RowSub")
            });
            row.Children.Add(text);

            var check = new Icon
            {
                Data = (Geometry)FindResource("Ico.Check"),
                Size = 16,
                Foreground = (Brush)FindResource("AccentTxBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Services.Settings.Current.Codec == codec ? Visibility.Visible : Visibility.Hidden
            };
            row.Children.Add(check);

            var button = new Button
            {
                Style = (Style)FindResource("RowButton"),
                Content = row,
                Tag = codec,
                IsEnabled = available,
                Opacity = available ? 1 : 0.45
            };
            button.Click += Codec_Click;
            Codecs.Children.Add(button);
        }
    }

    // ---------------- Загрузка и сохранение ----------------

    private void LoadFromSettings()
    {
        _loading = true;
        var s = Services.Settings.Current;

        Select(ResolutionSeg, s.VerticalResolution.ToString());
        Select(FpsSeg, s.Fps.ToString());
        Bitrate.Value = s.BitrateMbps;
        ShowBitrate();

        try { (_, _supported) = VideoEncoder.ProbeSupport(); } catch { }
        if (_supported.Count == 0) _supported = [VideoCodec.H264, VideoCodec.HEVC];
        BuildCodecs();

        CustomLength.Text = s.ReplayLengthSeconds.ToString();
        HighlightLength(s.ReplayLengthSeconds);
        ShowRam();

        GameAudio.IsChecked = s.CaptureGameAudio;
        MicAudio.IsChecked = s.CaptureMicrophone;
        NoiseGate.IsChecked = s.MicNoiseSuppression;
        Gate.Value = s.MicNoiseGateDb;
        GateValue.Text = $"−{Math.Abs((int)s.MicNoiseGateDb)} дБ";
        UpdateGateRow();
        SelectTag(TrackMode, s.TrackMode.ToString());

        FillAudioDevices(s);
        FillMonitors(s);
        CursorSwitch.IsChecked = s.RecordCursor;

        HighlightPreset();
        _loading = false;
        SetDirty(false);
    }

    private void FillAudioDevices(AppSettings s)
    {
        RenderDevice.Items.Clear();
        CaptureDevice.Items.Clear();
        RenderDevice.Items.Add(new ComboBoxItem { Content = "Устройство по умолчанию", Tag = null });
        CaptureDevice.Items.Add(new ComboBoxItem { Content = "Устройство по умолчанию", Tag = null });

        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(
                         NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                RenderDevice.Items.Add(new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID });
            foreach (var device in enumerator.EnumerateAudioEndPoints(
                         NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
                CaptureDevice.Items.Add(new ComboBoxItem { Content = device.FriendlyName, Tag = device.ID });
        }
        catch { }

        SelectTag(RenderDevice, s.RenderDeviceId);
        SelectTag(CaptureDevice, s.CaptureDeviceId);
        GameDeviceName.Text = (RenderDevice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        MicDeviceName.Text = (CaptureDevice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    }

    private void FillMonitors(AppSettings s)
    {
        Monitor.Items.Clear();
        try
        {
            foreach (var monitor in MonitorEnumerator.Enumerate())
                Monitor.Items.Add(new ComboBoxItem
                {
                    Content = $"{monitor.Label} · {monitor.Width}×{monitor.Height}",
                    Tag = monitor.Index
                });
        }
        catch { }
        if (Monitor.Items.Count == 0) Monitor.Items.Add(new ComboBoxItem { Content = "Основной экран", Tag = 0 });
        Monitor.SelectedIndex = Math.Min(s.MonitorIndex, Monitor.Items.Count - 1);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var s = Services.Settings.Current;

        s.VerticalResolution = int.Parse((string)((ListBoxItem)ResolutionSeg.SelectedItem).Tag);
        s.Fps = int.Parse((string)((ListBoxItem)FpsSeg.SelectedItem).Tag);
        s.BitrateMbps = (int)Bitrate.Value;
        s.ReplayLengthSeconds = ParseLength();
        s.CaptureGameAudio = GameAudio.IsChecked == true;
        s.CaptureMicrophone = MicAudio.IsChecked == true;
        s.MicNoiseSuppression = NoiseGate.IsChecked == true;
        s.MicNoiseGateDb = (float)Gate.Value;
        s.RecordCursor = CursorSwitch.IsChecked == true;
        s.TrackMode = Enum.Parse<AudioTrackMode>((string)((ComboBoxItem)TrackMode.SelectedItem).Tag);
        s.RenderDeviceId = (string?)((ComboBoxItem)RenderDevice.SelectedItem)?.Tag;
        s.CaptureDeviceId = (string?)((ComboBoxItem)CaptureDevice.SelectedItem)?.Tag;
        s.MonitorIndex = (int)((ComboBoxItem)Monitor.SelectedItem).Tag;

        Services.Settings.Save("video");
        SetDirty(false);

        // Конвейер пересобирается только если он работает
        if (Services.Engine.State != EngineState.Stopped)
        {
            Services.Engine.Stop();
            App.SafeStartEngine();
        }
    }

    private void Revert_Click(object sender, RoutedEventArgs e) => LoadFromSettings();

    // ---------------- Обработчики ----------------

    // У Click и SelectionChanged разные делегаты, поэтому две обёртки на одну логику
    private void VideoSelection_Changed(object sender, SelectionChangedEventArgs e) => Video_Changed(sender, e);
    private void AudioSelection_Changed(object sender, SelectionChangedEventArgs e) => Audio_Changed(sender, e);

    private void Video_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        HighlightPreset();
        ShowRam();
        SetDirty(true);
    }

    private void Audio_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        GameDeviceName.Text = (RenderDevice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        MicDeviceName.Text = (CaptureDevice.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        SetDirty(true);
    }

    private void Bitrate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        ShowBitrate();
        if (_loading) return;
        HighlightPreset();
        ShowRam();
        SetDirty(true);
    }

    /// <summary>Шумодав и порог применяются сразу: значение подбирают на слух.</summary>
    private void NoiseGate_Changed(object sender, RoutedEventArgs e)
    {
        UpdateGateRow();
        if (_loading) return;
        Services.Settings.Current.MicNoiseSuppression = NoiseGate.IsChecked == true;
        Services.Settings.Save("audio-live");
    }

    private void Gate_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        GateValue.Text = $"−{Math.Abs((int)Gate.Value)} дБ";
        if (_loading) return;
        Services.Settings.Current.MicNoiseGateDb = (float)Gate.Value;
        Services.Settings.Save("audio-live");
    }

    private void UpdateGateRow()
    {
        var visible = NoiseGate.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GateRow.Visibility = visible;
        GateRowSeparator.Visibility = visible;
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not Preset preset) return;
        _loading = true;
        Select(ResolutionSeg, preset.Height.ToString());
        Select(FpsSeg, preset.Fps.ToString());
        Bitrate.Value = preset.Bitrate;
        if (_supported.Contains(preset.Codec)) Services.Settings.Current.Codec = preset.Codec;
        BuildCodecs();
        ShowBitrate();
        ShowRam();
        _loading = false;
        HighlightPreset();
        SetDirty(true);
    }

    private void Codec_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not VideoCodec codec) return;
        Services.Settings.Current.Codec = codec;
        BuildCodecs();
        HighlightPreset();
        SetDirty(true);
    }

    private void Length_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not int seconds) return;
        CustomLength.Text = seconds.ToString();
        HighlightLength(seconds);
        ShowRam();
        SetDirty(true);
    }

    private void CustomLength_Commit(object sender, RoutedEventArgs e)
    {
        int seconds = ParseLength();
        CustomLength.Text = seconds.ToString();
        HighlightLength(seconds);
        ShowRam();
        SetDirty(true);
    }

    private void CustomLength_Key(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) CustomLength_Commit(sender, e);
    }

    private void Digits_Only(object sender, TextCompositionEventArgs e) =>
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");

    // ---------------- Вспомогательное ----------------

    private int ParseLength()
    {
        if (!int.TryParse(CustomLength.Text, out int seconds)) seconds = 180;
        return Math.Clamp(seconds, 5, 1800);
    }

    private static void Select(Segmented segmented, string tag)
    {
        foreach (ListBoxItem item in segmented.Items)
            if ((string)item.Tag == tag) { segmented.SelectedItem = item; return; }
        segmented.SelectedIndex = 1;
    }

    private static void SelectTag(ComboBox box, string? tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((string?)item.Tag == tag) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }

    private void HighlightLength(int seconds)
    {
        foreach (Button button in Lengths.Children)
        {
            bool active = (int)button.Tag == seconds;
            button.Style = (Style)FindResource(active ? "BtnPri" : "Btn");
        }
    }

    /// <summary>Подсветка набора, если текущие значения точно совпали с ним.</summary>
    private void HighlightPreset()
    {
        int height = int.TryParse((string?)(ResolutionSeg.SelectedItem as ListBoxItem)?.Tag, out int h) ? h : 1080;
        int fps = int.TryParse((string?)(FpsSeg.SelectedItem as ListBoxItem)?.Tag, out int f) ? f : 60;
        int bitrate = (int)Bitrate.Value;

        foreach (Button button in Presets.Children)
        {
            var preset = (Preset)button.Tag;
            bool active = preset.Height == height && preset.Fps == fps && preset.Bitrate == bitrate;
            button.BorderBrush = active ? (Brush)FindResource("AccentBrush") : null;
            if (button.Content is StackPanel panel && panel.Children[0] is TextBlock title)
                title.Foreground = (Brush)FindResource(active ? "AccentTxBrush" : "TxBrush");
        }
    }

    private void ShowBitrate()
    {
        int value = (int)Bitrate.Value;
        BitrateValue.Text = $"{value} Мбит/с";
        BitrateSub.Text = $"Минута записи весит около {value * 7.5:0} МБ";
    }

    private void ShowRam()
    {
        int seconds = ParseLength();
        double megabytes = (int)Bitrate.Value * 0.125 * seconds + 8;
        RamEstimate.Text = $"≈ {megabytes:0} МБ памяти";
    }

    private void ShowLevels()
    {
        var (game, mic) = Services.Engine.AudioLevels;
        SetLevel(GameLevel, GameAudio.IsChecked == true ? game : 0);
        SetLevel(MicLevel, MicAudio.IsChecked == true ? mic : 0);
    }

    private static void SetLevel(FrameworkElement bar, double level)
    {
        double full = ((FrameworkElement)bar.Parent).ActualWidth;
        bar.Width = Math.Clamp(level, 0, 1) * full;
    }

    /// <summary>
    /// Плашка «есть несохранённое» живёт в окне, а не на странице: так она видна
    /// всегда, а не только если долистать до низа.
    /// </summary>
    private void SetDirty(bool dirty)
    {
        if (_dirty == dirty) return;
        _dirty = dirty;

        var window = Window.GetWindow(this) as MainWindow;
        if (dirty) window?.ShowApplyBar("Изменения ещё не применены", () => Apply_Click(this, new RoutedEventArgs()),
                                        () => Revert_Click(this, new RoutedEventArgs()));
        else window?.HideApplyBar();
    }
}
