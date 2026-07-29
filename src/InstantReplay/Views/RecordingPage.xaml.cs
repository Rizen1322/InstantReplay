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

    private DirtyState _dirty = null!;
    /// <summary>Живые уровни звука — только пока страница на экране.</summary>
    private readonly DispatcherTimer _levelTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public RecordingPage()
    {
        InitializeComponent();
        _dirty = new DirtyState(DirtyText, ApplyBtn, RevertBtn);
        _levelTimer.Tick += (_, _) => UpdateLevels();
        GateSlider.ValueChanged += Gate_Changed;

        // Только теперь, когда всё дерево построено, вешаем обработчики значений:
        // из разметки они срабатывали бы прямо во время разбора (Minimum поднимает
        // Value слайдера) и лезли к ещё не созданным элементам.
        BitrateSlider.ValueChanged += Bitrate_Changed;
        CustomLengthBox.ValueChanged += Length_Changed;
        CursorToggle.Toggled += (_, _) => { if (!_loading) { _dirty.Mark(); UpdateSourceSummary(); } };

        BuildPresets();
        BuildChips();
        BuildMonitors();

        Loaded += (_, _) =>
        {
            BuildAudioDevices();
            LoadFromSettings();
            Services.Engine.StateChanged += OnEngineState;
            OnEngineState(Services.Engine.State);
            _ = LoadHwInfoAsync();
            _levelTimer.Start();
        };
        Unloaded += (_, _) =>
        {
            _levelTimer.Stop();
            Services.Engine.StateChanged -= OnEngineState;
        };
    }

    // ---------------- Звук ----------------

    private void BuildAudioDevices()
    {
        RenderDeviceBox.Items.Clear();
        CaptureDeviceBox.Items.Clear();
        RenderDeviceBox.Items.Add(new ComboBoxItem { Content = "По умолчанию", Tag = "" });
        CaptureDeviceBox.Items.Add(new ComboBoxItem { Content = "По умолчанию", Tag = "" });
        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(
                         NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active))
                RenderDeviceBox.Items.Add(new ComboBoxItem { Content = d.FriendlyName, Tag = d.ID });
            foreach (var d in enumerator.EnumerateAudioEndPoints(
                         NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.DeviceState.Active))
                CaptureDeviceBox.Items.Add(new ComboBoxItem { Content = d.FriendlyName, Tag = d.ID });
        }
        catch { }
    }

    /// <summary>Любая правка звука — как и видео, вступает в силу по «Применить».</summary>
    private void Audio_Changed(object sender, object e)
    {
        if (_loading) return;
        _dirty.Mark();
        UpdateDevicesSummary();
    }

    private void NoiseGate_Toggled(object sender, RoutedEventArgs e)
    {
        GateRow.Visibility = NoiseToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
        if (_loading) return;
        _dirty.Mark();
    }

    private void Gate_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (GateLabel is null) return; // ещё идёт разбор XAML
        GateLabel.Text = $"{(int)GateSlider.Value} дБ · {GateHint((int)GateSlider.Value)}";
        if (_loading) return;
        _dirty.Mark();
    }

    private static string GateHint(int db) => db switch
    {
        <= -55 => "пропускает почти всё, режет только тихий гул",
        <= -45 => "обычный вариант для большинства микрофонов",
        <= -35 => "жёстче: уберёт клавиатуру, но может съесть тихую речь",
        _ => "очень жёстко: пройдёт только громкий голос"
    };

    private void UpdateDevicesSummary()
    {
        string render = (RenderDeviceBox.SelectedItem as ComboBoxItem)?.Content as string ?? "По умолчанию";
        string capture = (CaptureDeviceBox.SelectedItem as ComboBoxItem)?.Content as string ?? "По умолчанию";
        DevicesSummary.Text = $"Игра: {render} · Микрофон: {capture}";
    }

    private void UpdateLevels()
    {
        if (Services.Engine.State == EngineState.Stopped)
        {
            GameLevel.Value = 0;
            MicLevel.Value = 0;
            return;
        }
        var (game, mic) = Services.Engine.AudioLevels;
        GameLevel.Value = Math.Min(1, game);
        MicLevel.Value = Math.Min(1, mic);
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
        _dirty.Suspended = true;
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

        // Звук
        GameAudioToggle.IsOn = s.CaptureGameAudio;
        MicToggle.IsOn = s.CaptureMicrophone;
        SelectByTag(TrackModeBox, s.TrackMode.ToString());
        SelectByTag(RenderDeviceBox, s.RenderDeviceId ?? "");
        if (RenderDeviceBox.SelectedIndex < 0) RenderDeviceBox.SelectedIndex = 0;
        SelectByTag(CaptureDeviceBox, s.CaptureDeviceId ?? "");
        if (CaptureDeviceBox.SelectedIndex < 0) CaptureDeviceBox.SelectedIndex = 0;
        NoiseToggle.IsOn = s.MicNoiseSuppression;
        GateRow.Visibility = s.MicNoiseSuppression ? Visibility.Visible : Visibility.Collapsed;
        GateSlider.Value = Math.Clamp(s.MicNoiseGateDb, -70, -20);
        GateLabel.Text = $"{(int)GateSlider.Value} дБ · {GateHint((int)GateSlider.Value)}";
        UpdateDevicesSummary();

        _loading = false;
        _dirty.Suspended = false;
        _dirty.Clear();
        SyncPresets();
        UpdateCodecWarning();
        UpdateSourceSummary();
    }

    /// <summary>Сводка в заголовке свёрнутого блока «Источник захвата».</summary>
    private void UpdateSourceSummary()
    {
        string monitor = (MonitorBox.SelectedItem as ComboBoxItem)?.Content as string ?? "монитор по умолчанию";
        SourceSummary.Text = $"{monitor} · курсор {(CursorToggle.IsOn ? "записывается" : "не записывается")}";

        // На Windows 10 захват идёт через Desktop Duplication, а он курсор в кадр
        // не отдаёт вовсе. Раньше об этом было сказано только в логе.
        bool win11 = Environment.OSVersion.Version.Build >= 22000;
        CursorNote.Text = win11
            ? "Курсор виден в записи"
            : "На Windows 10 курсор в запись не попадает — ограничение системы";
    }

    /// <summary>Подсветить пресет, совпадающий с текущими настройками (или снять все).</summary>
    private void SyncPresets()
    {
        int res = int.TryParse(TagOf(ResolutionBox), out int r) ? r : 0;
        int fps = int.TryParse(TagOf(FpsBox), out int f) ? f : 0;
        int mbps = (int)BitrateSlider.Value;
        Enum.TryParse(TagOf(CodecBox), out VideoCodec codec);

        Preset? active = null;
        foreach (var btn in _presetButtons)
        {
            bool match = btn.Tag is Preset p
                && p.Res == res && p.Fps == fps && p.Mbps == mbps && p.Codec == codec;
            btn.IsChecked = match;
            if (match) active = (Preset)btn.Tag;
        }

        // Заголовок свёрнутого блока показывает текущий набор — чтобы не разворачивать
        PresetSummary.Text = active is not null
            ? $"Активен: {active.Name}"
            : $"Своя конфигурация · {res}p · {fps} FPS · {mbps} Мбит/с · {codec}";
    }

    /// <summary>
    /// Предупредить, если выбранный кодек этот GPU не тянет. Раньше об этом было
    /// известно только в момент включения буфера — падало с NotSupportedException.
    /// </summary>
    private void UpdateCodecWarning()
    {
        if (_availableCodecs is null || _availableCodecs.Count == 0) { CodecBar.IsOpen = false; return; }
        CodecBar.IsOpen = Enum.TryParse(TagOf(CodecBox), out VideoCodec codec)
                          && !_availableCodecs.Contains(codec);
    }

    private void Codec_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _dirty.Mark();
        UpdateCodecWarning();
        SyncPresets();
    }

    /// <summary>Любая правка вручную — пометить страницу изменённой и обновить сводки.</summary>
    private void VideoSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _dirty.Mark();
        SyncPresets();
        UpdateSourceSummary();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var s = Services.Settings.Current;
        s.ReplayLengthSeconds = (int)Math.Max(5, CustomLengthBox.Value);
        if (int.TryParse(TagOf(ResolutionBox), out int res)) s.VerticalResolution = res;
        if (int.TryParse(TagOf(FpsBox), out int fps)) s.Fps = fps;
        s.BitrateMbps = (int)BitrateSlider.Value;
        if (Enum.TryParse(TagOf(CodecBox), out VideoCodec codec)) s.Codec = codec;
        if (int.TryParse(TagOf(MonitorBox), out int mon)) s.MonitorIndex = mon;
        s.RecordCursor = CursorToggle.IsOn;

        // Звук сохраняется здесь же: настройки одной записи не должны быть размазаны
        // по двум вкладкам с двумя кнопками «Применить».
        s.CaptureGameAudio = GameAudioToggle.IsOn;
        s.CaptureMicrophone = MicToggle.IsOn;
        if (Enum.TryParse(TagOf(TrackModeBox), out AudioTrackMode trackMode)) s.TrackMode = trackMode;
        string render = TagOf(RenderDeviceBox);
        s.RenderDeviceId = render.Length == 0 ? null : render;
        string capture = TagOf(CaptureDeviceBox);
        s.CaptureDeviceId = capture.Length == 0 ? null : capture;
        s.MicNoiseSuppression = NoiseToggle.IsOn;
        s.MicNoiseGateDb = (float)GateSlider.Value;

        Services.Settings.Save("video"); // движок перезапустит конвейер целиком

        SyncPresets();
        UpdateCodecWarning();
        UpdateSourceSummary();
        _dirty.MarkSaved();
    }

    private void Revert_Click(object sender, RoutedEventArgs e) => LoadFromSettings();

    // ---------------- Обработчики ----------------

    /// <summary>
    /// Пресет только раскладывает значения по контролам. Раньше он применялся сразу,
    /// хотя соседние настройки ждали кнопку — из-за этого было непонятно, что уже
    /// вступило в силу, а что нет.
    /// </summary>
    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as ToggleButton)?.Tag is not Preset p) return;
        SelectByTag(ResolutionBox, p.Res.ToString());
        SelectByTag(FpsBox, p.Fps.ToString());
        BitrateSlider.Value = p.Mbps;
        SelectByTag(CodecBox, p.Codec.ToString());
        _dirty.Mark();
        SyncPresets();
        UpdateCodecWarning();
    }

    private void Chip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton chip || chip.Tag is not int seconds) return;
        CustomLengthBox.Value = seconds;
        SyncChips(seconds);
        UpdateRamEstimate();
        _dirty.Mark();
    }

    private void Length_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        SyncChips((int)sender.Value);
        UpdateRamEstimate();
        _dirty.Mark();
    }

    private void SyncChips(int seconds)
    {
        foreach (var chip in _chips)
            chip.IsChecked = chip.Tag is int s && s == seconds;
    }

    private void UpdateRamEstimate()
    {
        // Может вызваться во время разбора XAML: присвоение Minimum слайдеру
        // поднимает его Value и вызывает ValueChanged раньше, чем созданы
        // элементы ниже по разметке. Без этой проверки страница падала целиком.
        if (CustomLengthBox is null || BitrateSlider is null || RamEstimate is null) return;

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
        if (BitrateLabel is null) return; // ещё идёт разбор XAML
        BitrateLabel.Text = $"{(int)e.NewValue} Мбит/с";
        UpdateRamEstimate();
        if (_loading) return;
        _dirty.Mark();
        SyncPresets();
    }

    private void OnEngineState(EngineState st) => Services.Dispatcher.Enqueue(() =>
    {
        bool locked = st != EngineState.Stopped;
        ActiveBar.IsOpen = locked;
        UiUtil.SetEnabledRecursive(PresetsPanel, !locked);
        UiUtil.SetEnabledRecursive(LengthChips, !locked);
        SourceExpander.IsEnabled = !locked;
        CustomLengthBox.IsEnabled = !locked;
        ResolutionBox.IsEnabled = !locked;
        FpsBox.IsEnabled = !locked;
        BitrateSlider.IsEnabled = !locked;
        CodecBox.IsEnabled = !locked;
        MonitorBox.IsEnabled = !locked;
        CursorToggle.IsEnabled = !locked;
        // Гасим только органы управления звуком: индикаторы уровня во время записи
        // как раз и нужны — по ним видно, что микрофон живой.
        GameAudioToggle.IsEnabled = !locked;
        MicToggle.IsEnabled = !locked;
        TrackModeBox.IsEnabled = !locked;
        NoiseToggle.IsEnabled = !locked;
        GateSlider.IsEnabled = !locked;
        DevicesExpander.IsEnabled = !locked;
        _dirty.Locked = locked; // применить нельзя, пока идёт запись
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

    private static string TagOf(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => App.OpenRecordingsFolder();
}
