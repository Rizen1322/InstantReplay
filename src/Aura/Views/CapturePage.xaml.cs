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

    /// <summary>
    /// Готовый набор: одним нажатием ставит разрешение, кадры и битрейт.
    ///
    /// Кодек набор НЕ трогает. Это отдельный осознанный выбор — от него зависит,
    /// откроется ли файл на чужом компьютере, — и молча переключать его из-за
    /// нажатия на «Высокий» неправильно. Числа битрейта рассчитаны на HEVC,
    /// который здесь и стоит по умолчанию.
    /// </summary>
    private sealed record Preset(string Name, string Detail, int Height, int Fps, int Bitrate);

    private static readonly Preset[] AllPresets =
    [
        new("Экономный", "720p · 30 · меньше всего места", 720, 30, 5),
        new("Обычный", "1080p · 60 · для стримов и клипов", 1080, 60, 12),
        new("Высокий", "1440p · 60 · чёткая картинка", 1440, 60, 25),
        new("Максимум", "4K · 60 · для монтажа", 2160, 60, 48)
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
        // Курсор пишется на любой версии Windows: на «десятке» кадры берутся через
        // Desktop Duplication, и раньше переключатель там был неактивен с подписью
        // «не работает» — теперь приложение дорисовывает курсор само.
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
                            Text = $"{preset.Detail} · {preset.Bitrate} Мбит/с",
                            Style = (Style)FindResource("RowSub"),
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
            // Разделитель без отступа под иконку: в остальных карточках он идёт
            // от края до края, и «ступенька» только у кодеков выглядела ошибкой.
            if (!first) Codecs.Children.Add(new Border { Style = (Style)FindResource("RowSeparator") });
            first = false;

            // Мало уметь кодировать — файл ещё должен собраться: AV1 на Windows 10
            // кодируется, но в MP4 не пакуется, и сохранение падает уже после записи.
            bool hasEncoder = _supported.Contains(codec);
            bool canSave = HardwareEncoders.CanSaveToMp4(codec);
            bool available = hasEncoder && canSave;
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
                Text = available ? detail
                     : !hasEncoder ? "Видеокарта не поддерживается"
                     : "Данная Windows не поддерживает AV1",
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

        try { (_, _supported) = HardwareEncoders.ProbeSupport(); } catch { }
        if (_supported.Count == 0) _supported = [VideoCodec.H264, VideoCodec.HEVC];
        BuildCodecs();

        CustomLength.Text = s.ReplayLengthSeconds.ToString();
        HighlightLength(s.ReplayLengthSeconds);
        ShowRam();
        ShowBitrate();   // вес повтора считается от длины буфера — она задана только сейчас


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
        // Разрешение или частота изменились — держимся того же УРОВНЯ качества,
        // а не числа: 30 Мбит/с это «высокое» для 1080p и «лёгкое» для 4K.
        if (QualityForCurrent() is string quality)
            Bitrate.Value = BitrateFor(quality, SelectedHeight(), SelectedFps(), CurrentCodec());
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
        // Кодек остаётся тем, который выбрали: от него зависит совместимость файла,
        // и менять его за человека из-за нажатия на набор — недопустимо.
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
        // Битрейт не трогаем: человек выбрал число сам или взял его из набора,
        // и подмена под другой кодек ломала бы подсветку набора без спроса.
        HighlightPreset();
        SetDirty(true);
    }

    private void Length_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not int seconds) return;
        CustomLength.Text = seconds.ToString();
        HighlightLength(seconds);
        ShowRam();
        ShowBitrate();   // вес повтора считается от длины буфера
        SetDirty(true);
    }

    private void CustomLength_Commit(object sender, RoutedEventArgs e)
    {
        int seconds = ParseLength();
        CustomLength.Text = seconds.ToString();
        HighlightLength(seconds);
        ShowRam();
        ShowBitrate();   // вес повтора считается от длины буфера
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

    /// <summary>
    /// Битрейт под разрешение и частоту для каждого уровня качества, Мбит/с.
    ///
    /// Ползунок 10–80 сам по себе не подсказывал ничего: человек ставил 45 на 1080p,
    /// потому что «больше — лучше», и получал гигабайт памяти под буфер и вдвое
    /// более тяжёлые файлы без видимой разницы. Числа здесь — те, на которых
    /// картинка перестаёт улучшаться на глаз для соответствующего разрешения.
    /// </summary>
    private static int BitrateFor(string quality, int height, int fps, VideoCodec codec)
    {
        // Таблица задана для 60 кадров и HEVC — того кодека, под который посчитаны
        // готовые наборы. Уровень «normal» на 60 кадрах совпадает с их числами
        // ровно, чтобы набор оставался подсвеченным после смены разрешения.
        int baseAt60 = height switch
        {
            <= 720  => quality switch { "light" => 4,  "normal" => 7,  "high" => 11, _ => 16 },
            <= 1080 => quality switch { "light" => 8,  "normal" => 12, "high" => 18, _ => 26 },
            <= 1440 => quality switch { "light" => 16, "normal" => 25, "high" => 36, _ => 50 },
            _       => quality switch { "light" => 30, "normal" => 48, "high" => 65, _ => 80 }
        };

        // Частота кадров меняет объём почти линейно, но не совсем: на 30 кадрах
        // хватает меньшего битрейта, на 120+ нужен не вдвое больший.
        double byFps = fps switch { <= 30 => 0.7, <= 60 => 1.0, <= 120 => 1.35, _ => 1.5 };

        // H.264 на том же битрейте заметно хуже — ему нужно примерно в полтора раза
        // больше, чтобы картинка не отличалась. AV1, наоборот, экономнее HEVC.
        double byCodec = codec switch { VideoCodec.H264 => 1.4, VideoCodec.AV1 => 0.8, _ => 1.0 };

        return Math.Clamp((int)Math.Round(baseAt60 * byFps * byCodec), MinBitrate, MaxBitrate);
    }

    /// <summary>Границы ползунка. Совпадают с Minimum/Maximum в разметке.</summary>
    private const int MinBitrate = 4, MaxBitrate = 80;

    private static VideoCodec CurrentCodec() => Services.Settings.Current.Codec;

    /// <summary>Какому уровню соответствует текущий битрейт; null — ни одному.</summary>
    private string? QualityForCurrent()
    {
        int height = SelectedHeight(), fps = SelectedFps(), value = (int)Bitrate.Value;
        foreach (string quality in new[] { "light", "normal", "high", "max" })
            if (BitrateFor(quality, height, fps, CurrentCodec()) == value) return quality;
        return null;
    }

    private int SelectedHeight() =>
        int.TryParse((ResolutionSeg.SelectedItem as ListBoxItem)?.Tag?.ToString(), out int h) ? h : 1080;

    private int SelectedFps() =>
        int.TryParse((FpsSeg.SelectedItem as ListBoxItem)?.Tag?.ToString(), out int f) ? f : 60;

    private void ShowBitrate()
    {
        int value = (int)Bitrate.Value;
        BitrateValue.Text = $"{value} Мбит/с";

        // Показываем вес именно ВАШЕГО повтора, а не абстрактной минуты: длина
        // буфера задаётся рядом, и человек хочет знать, во что обойдётся файл,
        // который он сохранит хоткеем. Плюс, для ориентира, вес часа обычной записи.
        int seconds = ParseLength();
        double clipMb = value * 0.125 * seconds;
        double hourGb = value * 0.125 * 3600 / 1024;
        // Явно пишем, что за мегабайты: рядом в интерфейсе есть оценка памяти,
        // и два числа без пометок читаются как одно и то же.
        BitrateSub.Text = $"{value} Мбит/с · файл повтора ≈ {clipMb:0} МБ · " +
                          $"час записи ≈ {hourGb:0.0} ГБ";

    }

    /// <summary>«30 секунд», «2 минуты», «3 мин 30 сек» — как человек это произносит.</summary>
    private static string LengthWords(int seconds)
    {
        if (seconds < 60) return $"{seconds} сек";
        int minutes = seconds / 60, rest = seconds % 60;
        string m = (minutes % 10, minutes % 100) switch
        {
            (1, not 11) => $"{minutes} минуту",
            (2 or 3 or 4, not (12 or 13 or 14)) => $"{minutes} минуты",
            _ => $"{minutes} минут"
        };
        return rest == 0 ? m : $"{m} {rest} сек";
    }

    private void ShowRam()
    {
        // Столько же считает ReplayVideoBuffer.Allocate: длительность плюс запас на
        // GOP и всплески битрейта, плюс блок под сохранение, округлённые вверх до
        // блоков по 16 МБ. Показываем именно занимаемое, а не полезное — иначе цифра
        // в настройках расходится с тем, что видно в диспетчере задач.
        int seconds = ParseLength();
        double wanted = (int)Bitrate.Value * 0.125 * (seconds + 15) * 1.05 + 64;
        double megabytes = Math.Ceiling(Math.Max(wanted, 16) / 16) * 16;
        RamEstimate.Text = $"≈ {megabytes:0} МБ RAM";
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
