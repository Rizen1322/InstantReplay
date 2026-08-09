using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aura.Controls;
using Aura.Core.Encoding;
using Aura.Core.Hardware;
using Aura.Core.Settings;   // VideoCodec

namespace Aura.Views;

/// <summary>Видеокарта, живая нагрузка конвейера записи и характеристики компьютера.</summary>
public partial class SystemPage : PageBase
{
    private HardwareInfo? _info;
    private NvidiaDriverStatus? _driver;

    public override string Title => "Система";

    public SystemPage()
    {
        InitializeComponent();
        BuildLoadRows();
        _load.Tick += (_, _) => ShowLoad();
        Loaded += (_, _) => _ = LoadAsync();

        // Пять плиток в ряд помещаются только на широком окне; дальше они
        // переносятся, а не сжимаются в нечитаемые столбики.
        SizeChanged += (_, _) => Load.Columns = ActualWidth < 560 ? 2 : ActualWidth < 800 ? 3 : 5;
    }

    /// <summary>
    /// Кнопки «Обновить» в шапке больше нет: железо между открытиями раздела не
    /// меняется, а нажимать её было незачем. Данные перечитываются сами при каждом
    /// заходе — этого достаточно и для замены видеокарты, и для смены драйвера.
    /// </summary>
    public override void OnShown()
    {
        _ = LoadAsync();
        ResetLoadBaseline();
        ShowLoad();
        _load.Start();
    }

    public override void OnHidden() => _load.Stop();

    /// <summary>
    /// Записью занимается дискретная видеокарта, а WMI обычно первой отдаёт
    /// встроенную — показываем ту, на которой реально работает энкодер.
    /// </summary>
    private static GpuInfo? MainGpu(HardwareInfo info)
    {
        string[] discrete = ["geforce", "rtx", "gtx", "radeon rx", "arc"];
        return info.Gpus.FirstOrDefault(g => discrete.Any(d => g.Name.Contains(d, StringComparison.OrdinalIgnoreCase)))
               ?? info.Gpus.FirstOrDefault();
    }

    private async Task LoadAsync()
    {
        _info = await HardwareInfoService.CollectAsync();

        var gpu = MainGpu(_info);
        GpuName.Text = gpu?.Name ?? "Видеокарта не определена";
        GpuSub.Text = gpu is null ? "" : $"{gpu.Vram} · драйвер {gpu.DriverVersion}";

        Specs.Children.Clear();
        AddSpec("Процессор", $"{_info.Cpu} · {_info.CpuCores} ядер, {_info.CpuThreads} потоков");
        AddSpec("Оперативная память", _info.RamTotal);
        AddSpec("Материнская плата", _info.Motherboard);
        AddSpec("Система", _info.Os);
        // Папки записей здесь нет намеренно: это не характеристика компьютера,
        // а настройка, и живёт она в разделе «Файлы».
        AddSpec("Экран", _info.Display);
    }

    private void AddSpec(string name, string value)
    {
        if (Specs.Children.Count > 0)
            Specs.Children.Add(new Border { Style = (Style)FindResource("RowSeparator") });

        var row = new AdaptiveRow { Margin = new Thickness(16, 11, 16, 11) };
        row.Children.Add(new TextBlock { Text = name, Style = (Style)FindResource("RowLabel") });
        row.Children.Add(new TextBlock
        {
            Text = value,
            Style = (Style)FindResource("RowValue"),
            TextAlignment = TextAlignment.Right,
            MaxWidth = 420,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Specs.Children.Add(row);
    }

    // ---------------- Нагрузка сейчас ----------------

    /// <summary>
    /// Живые счётчики конвейера. Обновляются раз в секунду и только пока раздел
    /// открыт: крутить таймер на невидимой странице незачем.
    ///
    /// Скорости считаются разницей нарастающих счётчиков движка за реальный
    /// промежуток между тиками, а не делением на «1 секунду» — таймер WPF может
    /// опоздать под нагрузкой, и деление на константу давало бы завышенные fps.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _load =
        new() { Interval = TimeSpan.FromSeconds(1) };

    private (long Received, long Accepted, long Encoded, long Dropped, long Duplicated) _prevFrames;
    private TimeSpan _prevCpu;
    private long _prevStamp;

    /// <summary>
    /// Одна плитка нагрузки: крупное число, подпись под ним и полоска заполнения.
    /// Ссылки держим на элементы, а не перестраиваем плитку каждую секунду —
    /// пересборка сбрасывала бы анимацию полоски и мигала бы текстом.
    /// </summary>
    private sealed record LoadTile(TextBlock Value, TextBlock Unit, TextBlock Sub, Border Fill, Border Track);

    private readonly Dictionary<string, LoadTile> _tiles = [];

    private static readonly (string Name, string Icon, string Color)[] LoadTiles =
    [
        ("Захват",      "Ico.Monitor", "BlueBrush"),
        ("Кодирование", "Ico.Bolt",    "PurpleBrush"),
        ("Оперативка",  "Ico.Ram",     "IndigoBrush"),
        ("Буфер",       "Ico.Clock",   "AccentBrush"),
        ("Процессор",   "Ico.Gauge",   "TealBrush")
    ];

    private void BuildLoadRows()
    {
        Load.Children.Clear();
        _tiles.Clear();

        foreach (var (name, icon, color) in LoadTiles)
        {
            var caption = new StackPanel { Orientation = Orientation.Horizontal };
            caption.Children.Add(new Icon
            {
                Data = (Geometry)FindResource(icon),
                Size = 12,
                Foreground = (Brush)FindResource("Tx3Brush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            caption.Children.Add(new TextBlock
            {
                Text = name,
                Style = (Style)FindResource("Caption"),
                Margin = new Thickness(6, 0, 0, 0)
            });

            // Число и единица рядом: единица мельче и приглушена, иначе «60 кадр/с»
            // читается как одно длинное число и плитка теряет главное.
            var value = new TextBlock
            {
                Text = "—",
                Style = (Style)FindResource("Numeral"),
                FontSize = 21
            };
            var unit = new TextBlock
            {
                Style = (Style)FindResource("Caption3"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 0, 3)
            };
            var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
            valueRow.Children.Add(value);
            valueRow.Children.Add(unit);

            var fill = new Border
            {
                Height = 4,
                Width = 0,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = (Brush)FindResource(color)
            };
            var trackBase = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = (Brush)FindResource("TrackBrush")
            };
            var track = new Grid { Margin = new Thickness(0, 9, 0, 0) };
            track.Children.Add(trackBase);
            track.Children.Add(fill);

            var sub = new TextBlock
            {
                Style = (Style)FindResource("Caption3"),
                Margin = new Thickness(0, 7, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var panel = new StackPanel();
            panel.Children.Add(caption);
            panel.Children.Add(valueRow);
            panel.Children.Add(track);
            panel.Children.Add(sub);

            _tiles[name] = new LoadTile(value, unit, sub, fill, trackBase);

            Load.Children.Add(new Border
            {
                Style = (Style)FindResource("CardSoft"),
                Padding = new Thickness(14, 12, 14, 13),
                Margin = new Thickness(0, 0, 10, 10),
                Child = panel
            });
        }
    }

    /// <summary>Ширина полоски задаётся анимацией — иначе она дёргалась бы рывками.</summary>
    private static void SetFill(LoadTile tile, double part)
    {
        double full = tile.Track.ActualWidth;
        if (full <= 0) return;
        tile.Fill.BeginAnimation(WidthProperty,
            new System.Windows.Media.Animation.DoubleAnimation(
                Math.Clamp(part, 0, 1) * full, TimeSpan.FromSeconds(0.35)));
    }

    /// <summary>part меньше нуля — у величины нет предела, полоску не рисуем вовсе:
    /// вечно пустая шкала читается как поломка.</summary>
    private void Show(string name, string value, string unit, string sub, double part, bool alarm = false)
    {
        if (!_tiles.TryGetValue(name, out var tile)) return;
        tile.Value.Text = value;
        tile.Unit.Text = unit;
        tile.Sub.Text = sub;
        tile.Value.Foreground = (Brush)FindResource(alarm ? "RecBrush" : "TxBrush");

        if (tile.Track.Parent is UIElement track)
            track.Visibility = part < 0 ? Visibility.Hidden : Visibility.Visible;
        if (part >= 0) SetFill(tile, part);
    }

    private void ResetLoadBaseline()
    {
        _prevFrames = Services.Engine.FrameCounters;
        _prevCpu = Process.GetCurrentProcess().TotalProcessorTime;
        _prevStamp = Stopwatch.GetTimestamp();
    }

    private void ShowLoad()
    {
        bool running = Services.Engine.State != Core.Engine.EngineState.Stopped;
        LoadHint.Text = running ? "обновляется каждую секунду" : "повтор выключен";

        if (!running)
        {
            foreach (var name in _tiles.Keys) Show(name, "—", "", "", 0);
            ResetLoadBaseline();
            return;
        }

        long stamp = Stopwatch.GetTimestamp();
        double seconds = (stamp - _prevStamp) / (double)Stopwatch.Frequency;
        if (seconds < 0.2) return;   // тик пришёл слишком рано — числа были бы шумом

        var frames = Services.Engine.FrameCounters;
        // Один снимок процесса на тик: и время процессора, и память берём из него,
        // чтобы числа в плитках относились к одному и тому же моменту.
        var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        var s = Services.Settings.Current;

        double capture = (frames.Accepted - _prevFrames.Accepted) / seconds;
        double encoded = (frames.Encoded - _prevFrames.Encoded) / seconds;
        long dropped = frames.Dropped - _prevFrames.Dropped;
        long duplicated = frames.Duplicated - _prevFrames.Duplicated;

        // Доля одного ядра была бы бессмысленной на 12 потоках: делим на все.
        double cpuPercent = Math.Max(0, (cpu - _prevCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100);

        _prevFrames = frames;
        _prevCpu = cpu;
        _prevStamp = stamp;

        double target = Math.Max(1, s.Fps);

        // Дубликаты — не ошибка: на статичной картинке система новых кадров не даёт,
        // и конвейер повторяет последний, чтобы поток остался ровным.
        Show("Захват", $"{capture:0.#}", "кадр/с",
             duplicated > 0 ? $"повторов кадра {duplicated}" : $"из {s.Fps} запрошенных",
             capture / target);

        Show("Кодирование", $"{encoded:0.#}", "кадр/с",
             dropped > 0 ? $"дропнуто {dropped} — не успевает" : "дропов нет",
             encoded / target, alarm: dropped > 0);

        // Рабочий набор — то же число, что показывает диспетчер задач в столбце
        // «Память». Именно оно волнует: буфер повтора живёт в оперативной памяти,
        // и при длинном повторе счёт идёт на гигабайты.
        long ram = process.WorkingSet64;
        long ramTotal = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        Show("Оперативка", Core.Storage.ByteSize.Format(ram), "",
             ramTotal > 0 ? $"из {Core.Storage.ByteSize.Format(ramTotal)} всего" : "занято приложением",
             ramTotal > 0 ? ram / (double)ramTotal : -1);

        long buffer = Services.Engine.BufferedBytes + Services.Engine.BufferedAudioBytes;
        int buffered = (int)Services.Engine.BufferedDuration.TotalSeconds;
        Show("Буфер", Core.Storage.ByteSize.Format(buffer), "",
             $"{buffered} из {s.ReplayLengthSeconds} сек",
             s.ReplayLengthSeconds > 0 ? buffered / (double)s.ReplayLengthSeconds : 0);

        Show("Процессор", $"{cpuPercent:0.#}", "%",
             $"от всех {Environment.ProcessorCount} потоков", cpuPercent / 100);
    }

    // ---------------- Драйвер ----------------

    private async void CheckDriver_Click(object sender, RoutedEventArgs e)
    {
        var gpu = _info is null ? null : MainGpu(_info);
        if (gpu is null) return;

        DriverButton.IsEnabled = false;
        DriverStatus.Text = "Смотрю на сайте NVIDIA…";
        try
        {
            _driver = await Services.Nvidia.CheckAsync(gpu);
            if (_driver is null)
            {
                DriverStatus.Text = "Проверка доступна только для видеокарт NVIDIA";
            }
            else if (_driver.UpdateAvailable)
            {
                DriverStatus.Text = $"Есть новее: {_driver.LatestVersion}";
                DownloadDriverButton.Visibility = Visibility.Visible;
                ShowDriverPill("Есть обновление", "OrangeBrush");
            }
            else
            {
                DriverStatus.Text = "Установлен свежий драйвер";
                ShowDriverPill("Драйвер свежий", "AccentTxBrush");
            }
        }
        catch (Exception ex) { DriverStatus.Text = "Не удалось проверить: " + ex.Message; }
        finally { DriverButton.IsEnabled = true; }
    }

    private void ShowDriverPill(string text, string brush)
    {
        DriverPillText.Text = text;
        DriverPillText.Foreground = (Brush)FindResource(brush);
        DriverPill.Visibility = Visibility.Visible;
    }

    private void DownloadDriver_Click(object sender, RoutedEventArgs e)
    {
        if (_driver is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_driver.DownloadUrl) { UseShellExecute = true });
        }
        catch { }
    }
}
