using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aura.Controls;
using Aura.Core.Encoding;
using Aura.Core.Hardware;
using Aura.Core.Logging;
using Aura.Core.Settings;   // VideoCodec
using Aura.Core.SystemIntegration;   // TrustedUrl

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
        SizeChanged += (_, _) => Load.Columns = ActualWidth < 560 ? 2 : ActualWidth < 760 ? 3 : 5;
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
        GpuSub.Text = gpu is null ? "" : GpuLine(gpu);
        // У NVIDIA — её логотип: приложение кодирует прямо через NVENC, и это
        // главная карта в системе. У остальных — нейтральный значок чипа.
        bool nvidia = gpu?.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true;
        GpuIcon.Data = (Geometry)FindResource(nvidia ? "Ico.Nvidia" : "Ico.Chip");
        GpuIcon.Filled = nvidia;
        GpuIcon.Foreground = nvidia
            ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
            : (Brush)FindResource("Tx2Brush");

        Specs.Children.Clear();
        AddSpec("Процессор", $"{CleanCpu(_info.Cpu)}, {_info.CpuCores} ядер и {_info.CpuThreads} потоков");
        AddSpec("Оперативная память", _info.RamTotal.Replace(" · ", ", "));
        if (gpu is not null)
        {
            _gpuSpec = AddSpec("Видеокарта", ShortGpu(gpu.Name) + (string.IsNullOrEmpty(gpu.Vram) ? "" : $" {gpu.Vram}"));
            _gpuSpecBase = _gpuSpec.Text;
        }
        AddSpec("Материнская плата", CleanBoard(_info.Motherboard));
        AddSpec("Система", _info.Os.Replace("Майкрософт ", "").Replace("Microsoft ", ""));
        // Папки записей здесь нет намеренно: это не характеристика компьютера,
        // а настройка, и живёт она в разделе «Файлы».
        AddSpec("Экран", _info.Display.Replace(" @ ", ", ").Replace(" · ", ", "));
    }

    private TextBlock? _gpuSpec;
    private string _gpuSpecBase = "";

    /// <summary>
    /// Версия драйвера так, как её пишет NVIDIA: из «32.0.16.1714» — «617.14».
    /// Windows хранит номер в своём формате, а на сайте и в уведомлениях он другой.
    /// </summary>
    internal static string NvidiaDriver(string windowsVersion)
    {
        string digits = new(windowsVersion.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return windowsVersion;
        string tail = digits[^5..];
        return $"{tail[..3]}.{tail[3..]}";
    }

    private static string GpuLine(GpuInfo gpu)
    {
        bool nvidia = gpu.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
        string driver = nvidia ? NvidiaDriver(gpu.DriverVersion) : gpu.DriverVersion;
        var codec = Services.Settings.Current.Codec;
        string encodes = codec switch
        {
            VideoCodec.HEVC => "HEVC",
            VideoCodec.AV1 => "AV1",
            _ => "H.264"
        };
        string bits = Core.Encoding.VideoEncoder.SupportsTenBit(codec) ? ", 10 бит" : "";
        return $"Драйвер {driver}. Пишет в {encodes}{bits}";
    }

    /// <summary>«AMD Ryzen 5 9600X 6-Core Processor» — число ядер и так стоит рядом.</summary>
    internal static string CleanCpu(string cpu)
    {
        string clean = System.Text.RegularExpressions.Regex.Replace(cpu, @"\s+\d+-Core Processor|\(R\)|\(TM\)|\s+CPU\b|\s+Processor\b", "");
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+@.*$", "");
        return System.Text.RegularExpressions.Regex.Replace(clean, @"\s{2,}", " ").Trim();
    }

    /// <summary>«Gigabyte Technology Co., Ltd. B650 GAMING X AX V2» — «Gigabyte B650 GAMING X AX V2».</summary>
    internal static string CleanBoard(string board)
    {
        string clean = System.Text.RegularExpressions.Regex.Replace(board,
            @"\b(Technology|Co\.,?\s*Ltd\.?|Ltd\.?|Inc\.?|Corporation|Corp\.?|COMPUTER|International)(?=\s|,|$),?",
            "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s{2,}", " ").Trim(' ', ',', '.');
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"^ASUSTeK\b", "ASUS", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"^Micro-Star\b", "MSI", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return clean.Length > 0 ? clean : board;
    }

    private static string ShortGpu(string name) =>
        name.Replace("NVIDIA GeForce ", "").Replace("NVIDIA ", "").Replace("AMD Radeon ", "Radeon ");

    private TextBlock AddSpec(string name, string value)
    {
        var row = new AdaptiveRow { Margin = new Thickness(16, 12, 16, 12) };
        row.Children.Add(new TextBlock { Text = name, Style = (Style)FindResource("RowLabel"),
                                         FontWeight = FontWeights.Normal, Foreground = (Brush)FindResource("Tx2Brush") });
        var text = new TextBlock
        {
            Text = value,
            Style = (Style)FindResource("RowLabel"),
            TextAlignment = TextAlignment.Right,
            MaxWidth = 460,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = value
        };
        row.Children.Add(text);
        Specs.Children.Add(row);
        return text;
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
    /// Одна плитка нагрузки: подпись, крупное число, пояснение и линия за последние
    /// сорок секунд. Ссылки держим на элементы, а не перестраиваем плитку каждую
    /// секунду: пересборка мигала бы текстом.
    /// </summary>
    private sealed record LoadTile(TextBlock Value, TextBlock Unit, TextBlock Sub,
                                   System.Windows.Shapes.Polyline Line, List<double> History);

    private readonly Dictionary<string, LoadTile> _tiles = [];

    private const int HistoryLength = 40;

    /// <summary>Зелёная линия у кадров: это главное, ради чего раздел открывают.</summary>
    private static readonly (string Name, bool Accent)[] LoadTiles =
    [
        ("Захват", true),
        ("Кодирование", true),
        ("Процессор", false),
        ("Оперативка", false),
        ("Буфер", false)
    ];

    private void BuildLoadRows()
    {
        Load.Children.Clear();
        _tiles.Clear();

        for (int n = 0; n < LoadTiles.Length; n++)
        {
            var (name, accent) = LoadTiles[n];
            var caption = new TextBlock { Text = name, Style = (Style)FindResource("Caption") };

            // Число и единица рядом: единица мельче и приглушена, иначе «60 кадр/с»
            // читается как одно длинное число и плитка теряет главное.
            var value = new TextBlock { Text = "…", Style = (Style)FindResource("Numeral"), FontSize = 22 };
            var unit = new TextBlock
            {
                Style = (Style)FindResource("Caption3"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(5, 0, 0, 4)
            };
            var valueRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            valueRow.Children.Add(value);
            valueRow.Children.Add(unit);

            var sub = new TextBlock
            {
                Style = (Style)FindResource("Caption3"),
                Margin = new Thickness(0, 4, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var line = new System.Windows.Shapes.Polyline
            {
                Stroke = (Brush)FindResource(accent ? "AccentBrush" : "Tx3Brush"),
                StrokeThickness = 1.5,
                StrokeLineJoin = PenLineJoin.Round,
                Opacity = accent ? 0.9 : 0.6,
                Stretch = Stretch.None
            };
            var chart = new Grid { Height = 18, Margin = new Thickness(0, 10, 0, 0), ClipToBounds = true };
            chart.Children.Add(line);
            var history = new List<double>();
            chart.SizeChanged += (_, _) => Draw(line, history, chart.ActualWidth, chart.ActualHeight);

            var panel = new StackPanel();
            panel.Children.Add(caption);
            panel.Children.Add(valueRow);
            panel.Children.Add(sub);
            panel.Children.Add(chart);

            _tiles[name] = new LoadTile(value, unit, sub, line, history);

            Load.Children.Add(new Border
            {
                Padding = new Thickness(16, 14, 16, 14),
                BorderBrush = (Brush)FindResource("SepBrush"),
                BorderThickness = new Thickness(n == 0 ? 0 : 1, 0, 0, 0),
                Child = panel
            });
        }
    }

    /// <summary>
    /// Линия за последние секунды. Масштаб по своим же значениям, но не уже 10%
    /// от среднего: иначе ровные 60 кадров с дрожью в сотые рисовались бы пилой.
    /// </summary>
    private static void Draw(System.Windows.Shapes.Polyline line, List<double> history, double width, double height)
    {
        var points = new PointCollection();
        if (history.Count >= 2 && width > 0 && height > 0)
        {
            double min = history.Min(), max = history.Max();
            double mean = history.Average();
            double span = Math.Max(max - min, Math.Max(Math.Abs(mean) * 0.1, 1e-6));
            double mid = (max + min) / 2;
            double step = width / (HistoryLength - 1);
            double x = width - step * (history.Count - 1);
            foreach (double v in history)
            {
                double y = height / 2 - (v - mid) / span * (height - 3);
                points.Add(new Point(x, Math.Clamp(y, 1.5, height - 1.5)));
                x += step;
            }
        }
        line.Points = points;
    }

    /// <summary>spark меньше нуля: точку в линию не добавляем.</summary>
    private void Show(string name, string value, string unit, string sub, double spark, bool alarm = false)
    {
        if (!_tiles.TryGetValue(name, out var tile)) return;
        tile.Value.Text = value;
        tile.Unit.Text = unit;
        tile.Sub.Text = sub;
        tile.Value.Foreground = (Brush)FindResource(alarm ? "RecBrush" : "TxBrush");

        if (spark >= 0)
        {
            tile.History.Add(spark);
            if (tile.History.Count > HistoryLength) tile.History.RemoveAt(0);
        }
        if (tile.Line.Parent is FrameworkElement chart)
            Draw(tile.Line, tile.History, chart.ActualWidth, chart.ActualHeight);
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
        LoadHint.Text = running ? "" : "повтор выключен";

        if (!running)
        {
            foreach (var tile in _tiles.Values) tile.History.Clear();
            foreach (var name in _tiles.Keys) Show(name, "…", "", "", -1);
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
        Show("Захват", $"{capture:0}", "кадр/с",
             duplicated > 0 ? $"из {s.Fps}, повторов кадра {duplicated}" : $"из {s.Fps} запрошенных",
             capture);

        Show("Кодирование", $"{encoded:0}", "кадр/с",
             dropped > 0 ? $"потеряно {dropped}, не успевает" : "без потерь",
             encoded, alarm: dropped > 0 || encoded < target * 0.9);

        // Рабочий набор — то же число, что показывает диспетчер задач в столбце
        // «Память». Именно оно волнует: буфер повтора живёт в оперативной памяти,
        // и при длинном повторе счёт идёт на гигабайты.
        long ram = process.WorkingSet64;
        long ramTotal = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        Show("Оперативка", Core.Storage.ByteSize.Format(ram), "", "занято приложением", ram);

        long buffer = Services.Engine.BufferedBytes + Services.Engine.BufferedAudioBytes;
        int buffered = (int)Services.Engine.BufferedDuration.TotalSeconds;
        Show("Буфер", Core.Storage.ByteSize.Format(buffer), "",
             $"в памяти, {Clock(buffered)} из {Clock(s.ReplayLengthSeconds)}", buffer);

        Show("Процессор", $"{cpuPercent:0.#}".Replace('.', ','), "%",
             $"от всех {Environment.ProcessorCount} потоков", cpuPercent);

        if (_gpuSpec is not null && Services.Engine.VideoMemory is { } vram)
            _gpuSpec.Text = $"{_gpuSpecBase}, запись занимает {vram.UsedMb} МБ";
    }

    private static string Clock(int seconds) => $"{seconds / 60}:{seconds % 60:00}";

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
                DriverStatus.Text = "";
                ShowDriverPill("Установлен свежий драйвер", "AccentTxBrush");
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

        // Ссылка пришла из ответа сервиса NVIDIA, то есть это просто строка из JSON.
        // UseShellExecute отработал бы и UNC-путь, и локальный exe, и протокол-хендлер —
        // причём с правами администратора и без запроса UAC. Проверяем схему и хост.
        if (!TrustedUrl.IsNvidiaDriver(_driver.DownloadUrl))
        {
            Log.Warn("Driver", $"Ссылка на драйвер ведёт не на nvidia.com — открытие отменено: {_driver.DownloadUrl}");
            DriverStatus.Text = "Ссылка на драйвер выглядит подозрительно. Откройте страницу NVIDIA вручную";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(_driver.DownloadUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // Молчаливый catch здесь означал «кнопка ничего не делает» без следов в логе
            Log.Warn("Driver", $"Не удалось открыть ссылку на драйвер: {ex.Message}");
            DriverStatus.Text = "Не удалось открыть браузер: " + ex.Message;
        }
    }
}
