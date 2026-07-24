using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using InstantReplay.Core.Hardware;

namespace InstantReplay.Views;

public sealed partial class HardwarePage : Page
{
    private HardwareInfo? _hw;
    private string? _driverDownloadUrl;

    public HardwarePage()
    {
        InitializeComponent();
        Loaded += async (_, _) => { if (_hw is null) await LoadAsync(); };
    }

    private async Task LoadAsync()
    {
        LoadingRing.IsActive = true;
        InfoPanel.Visibility = Visibility.Collapsed;

        _hw = await HardwareInfoService.CollectAsync();
        var hw = _hw;

        // Герой-карточка: дискретный GPU в приоритете
        var gpu = hw.Gpus.FirstOrDefault(g =>
                g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                g.Name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) ||
                g.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase))
            ?? hw.Gpus.FirstOrDefault();
        GpuName.Text = gpu?.Name ?? "GPU не найден";
        GpuVram.Text = gpu is null ? "" : $"Видеопамять: {gpu.Vram}";

        InfoRows.Children.Clear();
        AddRow("", "Процессор", hw.Cpu);
        AddRow("", "Ядра / потоки", $"{hw.CpuCores} ядер · {hw.CpuThreads} потоков");
        AddRow("", "Оперативная память", hw.RamTotal);
        AddRow("", "Разрешение экрана", hw.Display);
        AddRow("", "Материнская плата", hw.Motherboard);
        AddRow("", "Система", hw.Os);

        CheckDriverBtn.Visibility = hw.Gpus.Any(g => g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
            ? Visibility.Visible : Visibility.Collapsed;

        LoadingRing.IsActive = false;
        LoadingRing.Visibility = Visibility.Collapsed; // иначе пустое место под заголовком
        InfoPanel.Visibility = Visibility.Visible;
    }

    private void AddRow(string glyph, string label, string value)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new FontIcon { Glyph = glyph, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Left };
        grid.Children.Add(icon);

        var name = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        var val = new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(val, 2);
        grid.Children.Add(val);

        InfoRows.Children.Add(grid);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void CheckDriver_Click(object sender, RoutedEventArgs e)
    {
        var nvidia = _hw?.Gpus.FirstOrDefault(g => g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        if (nvidia is null) return;

        CheckDriverBtn.IsEnabled = false;
        DownloadDriverBtn.Visibility = Visibility.Collapsed;
        DriverStatusText.Text = "Проверяю драйвер NVIDIA…";

        var status = await Services.Nvidia.CheckAsync(nvidia);
        CheckDriverBtn.IsEnabled = true;

        if (status is null)
        {
            DriverStatusText.Text =
                $"Установлен: {NvidiaDriverService.WmiToGeforceVersion(nvidia.DriverVersion)}. " +
                "Не удалось проверить последнюю версию (нет сети?).";
            return;
        }

        if (status.UpdateAvailable)
        {
            DriverStatusText.Text = $"Установлен: {status.InstalledVersion} → доступен {status.LatestVersion}";
            _driverDownloadUrl = status.DownloadUrl;
            DownloadDriverBtn.Visibility = Visibility.Visible;
        }
        else
        {
            DriverStatusText.Text = $"Установлен: {status.InstalledVersion} — это последняя версия ✔";
        }
    }

    private string? _installerPath;

    /// <summary>Качаем официальный установщик с download.nvidia.com прямо в Загрузки.</summary>
    private async void DownloadDriver_Click(object sender, RoutedEventArgs e)
    {
        if (_driverDownloadUrl is null) return;

        DownloadDriverBtn.IsEnabled = false;
        DriverProgress.Visibility = Visibility.Visible;
        DriverProgress.IsIndeterminate = true;

        var progress = new Progress<(long Done, long Total)>(p => Services.Dispatcher.Enqueue(() =>
        {
            if (p.Total > 0)
            {
                DriverProgress.IsIndeterminate = false;
                DriverProgress.Value = p.Done * 100.0 / p.Total;
                DriverStatusText.Text =
                    $"Скачивание: {p.Done / (1024.0 * 1024):0} / {p.Total / (1024.0 * 1024):0} МБ";
            }
            else DriverStatusText.Text = $"Скачивание: {p.Done / (1024.0 * 1024):0} МБ";
        }));

        try
        {
            _installerPath = await Services.Nvidia.DownloadDriverAsync(_driverDownloadUrl, progress);
            DriverStatusText.Text = $"Скачано: {_installerPath}";
            DownloadDriverBtn.Visibility = Visibility.Collapsed;
            RunInstallerBtn.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            DriverStatusText.Text = $"Не удалось скачать: {ex.Message}";
            DownloadDriverBtn.IsEnabled = true;
        }
        finally
        {
            DriverProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void RunInstaller_Click(object sender, RoutedEventArgs e)
    {
        if (_installerPath is null || !File.Exists(_installerPath)) return;
        Process.Start(new ProcessStartInfo(_installerPath) { UseShellExecute = true });
    }
}
