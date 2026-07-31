using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Aura.Controls;
using Aura.Core.Encoding;
using Aura.Core.Hardware;
using Aura.Core.Settings;

namespace Aura.Views;

/// <summary>Характеристики компьютера и что умеет видеокарта.</summary>
public partial class SystemPage : PageBase
{
    private HardwareInfo? _info;
    private NvidiaDriverStatus? _driver;
    private bool _loaded;

    public override string Title => "Система";

    public override UIElement[] ToolbarActions
    {
        get
        {
            var button = new Button { Content = "Обновить" };
            button.Click += (_, _) => { _loaded = false; _ = LoadAsync(); };
            return [button];
        }
    }

    public SystemPage()
    {
        InitializeComponent();
        BuildMatrix();
        Loaded += (_, _) => _ = LoadAsync();
    }

    public override void OnShown()
    {
        if (!_loaded) _ = LoadAsync();
    }

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
        _loaded = true;

        var gpu = MainGpu(_info);
        GpuName.Text = gpu?.Name ?? "Видеокарта не определена";
        GpuSub.Text = gpu is null ? "" : $"{gpu.Vram} · драйвер {gpu.DriverVersion}";

        Specs.Children.Clear();
        AddSpec("Процессор", $"{_info.Cpu} · {_info.CpuCores} ядер, {_info.CpuThreads} потоков");
        AddSpec("Оперативная память", _info.RamTotal);
        AddSpec("Материнская плата", _info.Motherboard);
        AddSpec("Система", _info.Os);
        AddSpec("Экран", _info.Display);
        AddSpec("Папка записей", Services.Settings.Current.SaveRootPath);

        BuildMatrix();
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

    /// <summary>Плитки «умеет / не умеет» по реальному опросу кодировщиков.</summary>
    private void BuildMatrix()
    {
        Matrix.Children.Clear();
        List<VideoCodec> supported = [];
        try { (_, supported) = VideoEncoder.ProbeSupport(); } catch { }

        // У AV1 два условия: энкодер в видеокарте и поддержка контейнера в системе.
        // Windows 10 кодирует, но не сохраняет — об этом честнее написать прямо.
        bool av1Encoder = supported.Contains(VideoCodec.AV1);
        bool av1Save = VideoEncoder.CanSaveToMp4(VideoCodec.AV1);
        string av1Detail = av1Encoder && av1Save ? "есть"
                         : !av1Encoder ? "нет в этой видеокарте"
                         : "Windows 10 не сохранит";

        (string Name, string Detail, bool Ok)[] items =
        [
            ("H.264", "самый совместимый", supported.Contains(VideoCodec.H264)),
            ("HEVC", "файлы меньше", supported.Contains(VideoCodec.HEVC)),
            ("AV1", av1Detail, av1Encoder && av1Save),
            ("Звук AAC", "48 кГц стерео", true)
        ];

        foreach (var (name, detail, ok) in items)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = name,
                FontFamily = (FontFamily)FindResource("DispFont"),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = detail,
                Style = (Style)FindResource("Caption3"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0)
            });
            panel.Children.Add(new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 9, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = (Brush)FindResource(ok ? "AccentSoftBrush" : "HoverBrush"),
                Child = new Icon
                {
                    Data = (Geometry)FindResource(ok ? "Ico.Check" : "Ico.X"),
                    Size = 13,
                    Foreground = (Brush)FindResource(ok ? "AccentTxBrush" : "RecBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });

            Matrix.Children.Add(new Border
            {
                Style = (Style)FindResource("Card"),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 0, 10, 0),
                Child = panel
            });
        }
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
