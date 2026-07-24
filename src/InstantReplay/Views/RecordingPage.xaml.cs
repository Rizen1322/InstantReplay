using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using InstantReplay.Core.Capture;
using InstantReplay.Core.Encoding;
using InstantReplay.Core.Engine;
using InstantReplay.Core.Settings;

namespace InstantReplay.Views;

public sealed partial class RecordingPage : Page
{
    private bool _loading = true;

    private sealed record Preset(string Name, string Desc, int Res, int Fps, int Mbps, VideoCodec Codec);

    private static readonly Preset[] Presets =
    [
        new("Экономный", "720p · 30 FPS · 10 Мбит/с — минимум RAM и места, для слабых ПК", 720, 30, 10, VideoCodec.H264),
        new("Сбалансированный", "1080p · 60 FPS · 25 Мбит/с — хорошая картинка при умеренной нагрузке", 1080, 60, 25, VideoCodec.H264),
        new("Качественный", "1080p · 60 FPS · 45 Мбит/с — почти без потерь, для монтажа и YouTube", 1080, 60, 45, VideoCodec.H264),
        new("Киберспорт", "1080p · 120 FPS · 50 Мбит/с — плавность важнее веса файла", 1080, 120, 50, VideoCodec.H264),
        new("Максимальный", "1440p · 60 FPS · 65 Мбит/с · HEVC — топовое качество, нужен мощный GPU", 1440, 60, 65, VideoCodec.HEVC),
        new("4K", "2160p · 60 FPS · 80 Мбит/с · HEVC — только для сильных систем", 2160, 60, 80, VideoCodec.HEVC),
    ];

    private static readonly (string Label, int Seconds)[] LengthPresets =
    [
        ("15 сек", 15), ("30 сек", 30), ("1 мин", 60), ("3 мин", 180),
        ("5 мин", 300), ("10 мин", 600), ("20 мин", 1200)
    ];

    private readonly List<ToggleButton> _chips = [];
    private readonly List<ToggleButton> _presetButtons = [];
    /// <summary>Кодеки, которые реально поддерживает GPU (по энумерации MFT).</summary>
    private List<VideoCodec>? _availableCodecs;

    public RecordingPage()
    {
        InitializeComponent();
        BuildPresets();
        BuildChips();
        BuildMonitors();

        Loaded += (_, _) =>
        {
            LoadFromSettings();
            Services.Engine.StateChanged += OnEngineState;
            OnEngineState(Services.Engine.State);
            _ = LoadHwInfoAsync();
        };
        Unloaded += (_, _) => Services.Engine.StateChanged -= OnEngineState;
    }

    // ---------------- Построение UI ----------------

    private void BuildPresets()
    {
        foreach (var p in Presets)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Spacing = 2 };
            left.Children.Add(new TextBlock { Text = p.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            left.Children.Add(new TextBlock
            {
                Text = p.Desc,
                FontSize = 11.5,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            grid.Children.Add(left);

            var right = new TextBlock
            {
                // Мбит/с → МБ/с (/8) → МБ/мин (*60) → ГБ/мин (/1000)
                Text = $"~{p.Mbps / 8.0 * 60 / 1000:0.#} ГБ/мин".Replace('.', ','),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            // ToggleButton, а не Button: активный пресет теперь видно — раньше после
            // клика ничего не подсвечивалось и было непонятно, какой набор применён.
            var btn = new ToggleButton
            {
                Content = grid,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 12, 16, 12),
                Tag = p
            };
            btn.Click += Preset_Click;
            _presetButtons.Add(btn);
            PresetsPanel.Children.Add(btn);
        }
    }

    private void BuildChips()
    {
        foreach (var (label, seconds) in LengthPresets)
        {
            var chip = new ToggleButton { Content = label, Tag = seconds, MinWidth = 62 };
            chip.Click += Chip_Click;
            _chips.Add(chip);
            LengthChips.Children.Add(chip);
        }
    }

    private void BuildMonitors()
    {
        MonitorBox.Items.Clear();
        foreach (var m in MonitorEnumerator.Enumerate())
            MonitorBox.Items.Add(new ComboBoxItem { Content = m.Label, Tag = m.Index.ToString() });
    }

    // ---------------- Загрузка/сохранение ----------------

    private void LoadFromSettings()
    {
        _loading = true;
        var s = Services.Settings.Current;

        CustomLengthBox.Value = s.ReplayLengthSeconds;
        SyncChips(s.ReplayLengthSeconds);
        UpdateRamEstimate();

        SelectByTag(ResolutionBox, s.VerticalResolution.ToString());
        SelectByTag(FpsBox, s.Fps.ToString());
        BitrateSlider.Value = s.BitrateMbps;
        BitrateLabel.Text = $"{s.BitrateMbps} Мбит/с";
        SelectByTag(CodecBox, s.Codec.ToString());
        SelectByTag(MonitorBox, s.MonitorIndex.ToString());
        if (MonitorBox.SelectedIndex < 0 && MonitorBox.Items.Count > 0) MonitorBox.SelectedIndex = 0;
        CursorToggle.IsOn = s.RecordCursor;

        _loading = false;
        SyncPresets();
        UpdateCodecWarning();
    }

    /// <summary>Подсветить пресет, совпадающий с текущими настройками (или снять все).</summary>
    private void SyncPresets()
    {
        int res = int.TryParse(Tag(ResolutionBox), out int r) ? r : 0;
        int fps = int.TryParse(Tag(FpsBox), out int f) ? f : 0;
        int mbps = (int)BitrateSlider.Value;
        Enum.TryParse(Tag(CodecBox), out VideoCodec codec);

        foreach (var btn in _presetButtons)
            btn.IsChecked = btn.Tag is Preset p
                && p.Res == res && p.Fps == fps && p.Mbps == mbps && p.Codec == codec;
    }

    /// <summary>
    /// Предупредить, если выбранный кодек этот GPU не тянет. Раньше об этом было
    /// известно только в момент включения буфера — падало с NotSupportedException.
    /// </summary>
    private void UpdateCodecWarning()
    {
        if (_availableCodecs is null || _availableCodecs.Count == 0) { CodecBar.IsOpen = false; return; }
        CodecBar.IsOpen = Enum.TryParse(Tag(CodecBox), out VideoCodec codec)
                          && !_availableCodecs.Contains(codec);
    }

    private void Codec_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateCodecWarning();
        SyncPresets();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var s = Services.Settings.Current;
        s.ReplayLengthSeconds = (int)Math.Max(5, CustomLengthBox.Value);
        if (int.TryParse(Tag(ResolutionBox), out int res)) s.VerticalResolution = res;
        if (int.TryParse(Tag(FpsBox), out int fps)) s.Fps = fps;
        s.BitrateMbps = (int)BitrateSlider.Value;
        if (Enum.TryParse(Tag(CodecBox), out VideoCodec codec)) s.Codec = codec;
        if (int.TryParse(Tag(MonitorBox), out int mon)) s.MonitorIndex = mon;
        s.RecordCursor = CursorToggle.IsOn;
        Services.Settings.Save("video");

        SyncPresets();
        UpdateCodecWarning();
        ApplyBtn.Content = "Применено ✓";
        _ = ResetApplyLabelAsync();
    }

    private async Task ResetApplyLabelAsync()
    {
        await Task.Delay(1500);
        ApplyBtn.Content = "Применить";
    }

    // ---------------- Обработчики ----------------

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as ToggleButton)?.Tag is not Preset p) return;
        SelectByTag(ResolutionBox, p.Res.ToString());
        SelectByTag(FpsBox, p.Fps.ToString());
        BitrateSlider.Value = p.Mbps;
        SelectByTag(CodecBox, p.Codec.ToString());
        Apply_Click(sender, e);
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton chip || chip.Tag is not int seconds) return;
        CustomLengthBox.Value = seconds;
        SyncChips(seconds);
        UpdateRamEstimate();
    }

    private void Length_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        SyncChips((int)sender.Value);
        UpdateRamEstimate();
    }

    private void SyncChips(int seconds)
    {
        foreach (var chip in _chips)
            chip.IsChecked = chip.Tag is int s && s == seconds;
    }

    private void UpdateRamEstimate()
    {
        int seconds = (int)Math.Max(5, double.IsNaN(CustomLengthBox.Value) ? 180 : CustomLengthBox.Value);
        int mbps = (int)BitrateSlider.Value;
        double gb = mbps / 8.0 * seconds / 1000.0;
        string len = seconds >= 60 ? $"{seconds / 60} мин" : $"{seconds} сек";
        RamEstimate.Text = $"{len} · ~{gb:0.#} ГБ в RAM".Replace('.', ',');

        // Буфер целиком живёт в оперативке (битрейт × длина), и на длинных значениях
        // это гигабайты. Цветом предупреждаем ДО того, как система начнёт свопиться.
        string brush = gb >= 8 ? "SystemFillColorCriticalBrush"
                     : gb >= 4 ? "SystemFillColorCautionBrush"
                               : "TextFillColorSecondaryBrush";
        RamEstimate.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[brush];
    }

    private void Bitrate_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        BitrateLabel.Text = $"{(int)e.NewValue} Мбит/с";
        UpdateRamEstimate();
    }

    private void OnEngineState(EngineState st) => Services.Dispatcher.Enqueue(() =>
    {
        bool locked = st != EngineState.Stopped;
        ActiveBar.IsOpen = locked;
        UiUtil.SetEnabledRecursive(PresetsPanel, !locked);
        UiUtil.SetEnabledRecursive(LengthChips, !locked);
        CustomLengthBox.IsEnabled = !locked;
        ResolutionBox.IsEnabled = !locked;
        FpsBox.IsEnabled = !locked;
        BitrateSlider.IsEnabled = !locked;
        CodecBox.IsEnabled = !locked;
        MonitorBox.IsEnabled = !locked;
        CursorToggle.IsEnabled = !locked;
        ApplyBtn.IsEnabled = !locked;
    });

    private async Task LoadHwInfoAsync()
    {
        var (vendor, codecs) = await Task.Run(VideoEncoder.ProbeSupport);
        string running = Services.Engine.EncoderVendor;
        if (!string.IsNullOrEmpty(running)) vendor = running;

        _availableCodecs = codecs;
        UpdateCodecWarning();

        if (codecs.Count > 0)
        {
            HwBar.Severity = InfoBarSeverity.Success;
            HwBar.Title = $"Аппаратное кодирование: {vendor}";
            HwBar.Message = $"Доступные кодеки: {string.Join(", ", codecs)}. Нагрузка на CPU минимальна.";
        }
        else
        {
            HwBar.Severity = InfoBarSeverity.Warning;
            HwBar.Title = "Аппаратный энкодер не найден";
            HwBar.Message = "Проверьте драйвер GPU — запись не сможет запуститься.";
        }
    }

    // ---------------- Утилиты ----------------

    private static void SelectByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items)
            if (item is ComboBoxItem c && (string?)c.Tag == tag) { box.SelectedItem = c; return; }
    }

    private static string Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenRecordingsFolder();
}
