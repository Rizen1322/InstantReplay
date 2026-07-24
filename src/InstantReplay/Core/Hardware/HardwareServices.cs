using System.Management;
using System.Net.Http.Json;
using System.Text.Json;
using InstantReplay.Core.Logging;

namespace InstantReplay.Core.Hardware;

public sealed record HardwareInfo(
    string Cpu, int CpuCores, int CpuThreads,
    List<GpuInfo> Gpus,
    string RamTotal, List<string> RamModules,
    string Motherboard, string Os, string Display);

public sealed record GpuInfo(string Name, string DriverVersion, string Vram);

public sealed record NvidiaDriverStatus(
    string InstalledVersion, string LatestVersion, bool UpdateAvailable, string DownloadUrl);

/// <summary>Сбор характеристик ПК через WMI (CPU/GPU/RAM/материнская плата/ОС).</summary>
public static class HardwareInfoService
{
    public static Task<HardwareInfo> CollectAsync() => Task.Run(Collect);

    private static HardwareInfo Collect()
    {
        string cpu = "Unknown"; int cores = 0, threads = 0;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
            {
                cpu = (o["Name"] as string)?.Trim() ?? cpu;
                cores += Convert.ToInt32(o["NumberOfCores"] ?? 0);
                threads += Convert.ToInt32(o["NumberOfLogicalProcessors"] ?? 0);
            }
        }
        catch (Exception ex) { Log.Warn("HW", $"CPU WMI: {ex.Message}"); }

        // VRAM берём из DXGI (DedicatedVideoMemory) — WMI AdapterRAM uint32 и врёт для карт >4 ГБ
        var dxgiVram = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out Vortice.DXGI.IDXGIAdapter1 adapter).Success; a++)
                using (adapter)
                {
                    var d = adapter.Description1;
                    if (!dxgiVram.ContainsKey(d.Description))
                        dxgiVram[d.Description] = (long)(ulong)d.DedicatedVideoMemory;
                }
        }
        catch (Exception ex) { Log.Warn("HW", $"DXGI: {ex.Message}"); }

        var gpus = new List<GpuInfo>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
            {
                string name = (o["Name"] as string)?.Trim() ?? "GPU";
                string drv = (o["DriverVersion"] as string) ?? "";
                long vram = dxgiVram.TryGetValue(name, out long dedicated) ? dedicated
                          : Convert.ToInt64(o["AdapterRAM"] ?? 0L);
                gpus.Add(new GpuInfo(name, drv, vram > 0 ? $"{Math.Round(vram / (1024.0 * 1024 * 1024)):0} ГБ" : "—"));
            }
        }
        catch (Exception ex) { Log.Warn("HW", $"GPU WMI: {ex.Message}"); }

        long ramTotal = 0; uint ramSpeed = 0; var modules = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Capacity, Speed, Manufacturer FROM Win32_PhysicalMemory");
            foreach (ManagementObject o in s.Get())
            {
                long cap = Convert.ToInt64(o["Capacity"] ?? 0L);
                ramTotal += cap;
                uint speed = Convert.ToUInt32(o["Speed"] ?? 0u);
                if (speed > ramSpeed) ramSpeed = speed;
                modules.Add($"{cap / (1024L * 1024 * 1024)} ГБ {(o["Manufacturer"] as string)?.Trim()} @ {speed} МГц");
            }
        }
        catch (Exception ex) { Log.Warn("HW", $"RAM WMI: {ex.Message}"); }

        string mb = "Unknown";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject o in s.Get())
                mb = $"{(o["Manufacturer"] as string)?.Trim()} {(o["Product"] as string)?.Trim()}";
        }
        catch (Exception ex) { Log.Warn("HW", $"MB WMI: {ex.Message}"); }

        string os = $"{Environment.OSVersion.VersionString} (x64)";
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Caption, BuildNumber FROM Win32_OperatingSystem");
            foreach (ManagementObject o in s.Get())
                os = $"{(o["Caption"] as string)?.Trim()} (сборка {o["BuildNumber"]})";
        }
        catch { }

        string display = "—";
        try
        {
            var dm = new Interop.NativeMethods.DEVMODEW();
            dm.dmSize = (ushort)System.Runtime.InteropServices.Marshal.SizeOf<Interop.NativeMethods.DEVMODEW>();
            if (Interop.NativeMethods.EnumDisplaySettingsW(null, Interop.NativeMethods.ENUM_CURRENT_SETTINGS, ref dm))
                display = $"{dm.dmPelsWidth}×{dm.dmPelsHeight} @ {dm.dmDisplayFrequency} Гц";
        }
        catch { }

        string ram = $"{ramTotal / (1024.0 * 1024 * 1024):0} ГБ" + (ramSpeed > 0 ? $" · {ramSpeed} МГц" : "");
        return new HardwareInfo(cpu, cores, threads, gpus, ram, modules, mb, os, display);
    }
}

/// <summary>
/// Проверка драйвера NVIDIA через официальный GeForce driver lookup API
/// (gfwsl.geforce.com/services_toolkit). pfid карты определяется по имени GPU
/// через gpu-data.json из репозитория ZenitH-AT/nvidia-data (тот же источник,
/// что использует NVCleanstall/TinyNvidiaUpdateChecker).
/// </summary>
public sealed class NvidiaDriverService
{
    private static readonly HttpClient Http = new()
    {
        // Общий таймаут щедрый; на каждый запрос ставим свой через CancellationToken.
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders =
        {
            // Обычный браузерный UA: с кастомным NVIDIA-эндпоинт иногда рвёт соединение
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) InstantReplay/1.0" }
        }
    };

    // Для скачивания установщика (сотни МБ): без 15-сек таймаута основного клиента
    private static readonly HttpClient DownloadHttp = new()
    {
        Timeout = Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { { "User-Agent", "InstantReplay" } }
    };

    private const string GpuDataUrl = "https://raw.githubusercontent.com/ZenitH-AT/nvidia-data/main/gpu-data.json";

    public async Task<NvidiaDriverStatus?> CheckAsync(GpuInfo gpu)
    {
        if (!gpu.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            // 1) pfid по имени карты (gpu-data.json из nvidia-data)
            int? pfid = await ResolvePfidAsync(gpu.Name);
            if (pfid is null) { Log.Warn("Nvidia", $"pfid не найден для '{gpu.Name}'"); return null; }

            // 2) osID по версии ОС (без отдельного запроса — os-data только добавляет
            //    точку отказа). Win11 = build >= 22000.
            int osId = Environment.OSVersion.Version.Build >= 22000 ? 135 : 57;

            // 3) Официальный lookup (psid не нужен — достаточно pfid). Эндпоинт
            //    NVIDIA часто отвечает пусто/000 — ретраим несколько раз.
            string url = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
                         $"?func=DriverManualLookup&pfid={pfid}&osID={osId}&languageCode=1033" +
                         "&dch=1&upCRD=0&qnf=0&sort1=0&numberOfResults=1";

            JsonDocument? resp = await GetJsonWithRetryAsync(url, attempts: 4, perAttemptSeconds: 10);
            if (resp is null) { Log.Warn("Nvidia", "Lookup-эндпоинт не ответил (сеть/таймаут)"); return null; }

            using (resp)
            {
                if (!resp.RootElement.TryGetProperty("IDS", out var ids) ||
                    ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0)
                {
                    Log.Warn("Nvidia", "В ответе нет драйверов для этой карты/ОС");
                    return null;
                }
                if (!ids[0].TryGetProperty("downloadInfo", out var info)) return null;
                string latest = info.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "";
                string download = info.TryGetProperty("DownloadURL", out var u) ? u.GetString() ?? "" : "";
                if (latest.Length == 0) return null;

                string installed = WmiToGeforceVersion(gpu.DriverVersion);
                bool update = CompareVersions(installed, latest) < 0;
                return new NvidiaDriverStatus(installed, latest, update, download);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Nvidia", $"Проверка драйвера не удалась: {ex.Message}");
            return null;
        }
    }

    /// <summary>pfid карты из gpu-data.json (с ретраями — GitHub raw тоже бывает флапает).</summary>
    private static async Task<int?> ResolvePfidAsync(string gpuName)
    {
        JsonDocument? gpuData = await GetJsonWithRetryAsync(GpuDataUrl, attempts: 3, perAttemptSeconds: 10);
        if (gpuData is null) return null;
        using (gpuData)
        {
            // gpu-data.json: { "desktop": { "GeForce RTX 3070": 933, ... }, "notebook": {...} }
            // Значение — сразу pfid. Сначала ТОЧНОЕ совпадение по очищенному имени,
            // иначе "GeForce RTX 3070" по подстроке зацепит "GeForce RTX 3070 Ti".
            string clean = CleanGpuName(gpuName);
            foreach (bool exact in new[] { true, false })
            {
                foreach (var type in gpuData.RootElement.EnumerateObject())
                {
                    if (type.Value.ValueKind != JsonValueKind.Object) continue;
                    foreach (var entry in type.Value.EnumerateObject())
                    {
                        bool match = exact
                            ? string.Equals(entry.Name, clean, StringComparison.OrdinalIgnoreCase)
                            : gpuName.Contains(entry.Name, StringComparison.OrdinalIgnoreCase);
                        if (!match) continue;
                        int? pfid = entry.Value.ValueKind switch
                        {
                            JsonValueKind.Number when entry.Value.TryGetInt32(out int n) => n,
                            JsonValueKind.String when int.TryParse(entry.Value.GetString(), out int n) => n,
                            JsonValueKind.Object => ReadFlexibleInt(entry.Value, "id"),
                            _ => null
                        };
                        if (pfid is not null) return pfid;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>GET + разбор JSON с ретраями и таймаутом на попытку. null, если все попытки провалились.</summary>
    private static async Task<JsonDocument?> GetJsonWithRetryAsync(string url, int attempts, int perAttemptSeconds)
    {
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(perAttemptSeconds));
                string body = await Http.GetStringAsync(url, cts.Token);
                if (!string.IsNullOrWhiteSpace(body))
                    return JsonDocument.Parse(body);
            }
            catch (Exception ex)
            {
                Log.Warn("Nvidia", $"Попытка {i + 1}/{attempts} не удалась: {ex.Message}");
            }
            if (i < attempts - 1) await Task.Delay(1200);
        }
        return null;
    }

    /// <summary>
    /// Скачивает установщик драйвера в Загрузки с прогрессом (скачано, всего байт).
    /// Возвращает путь к exe. Повторный вызов доиспользует уже скачанный файл.
    /// </summary>
    public async Task<string> DownloadDriverAsync(
        string url, IProgress<(long Done, long Total)> progress, CancellationToken ct = default)
    {
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Directory.CreateDirectory(downloads);
        string fileName = Path.GetFileName(new Uri(url).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "nvidia-driver.exe";
        string dest = Path.Combine(downloads, fileName);
        string tmp = dest + ".part";

        using var response = await DownloadHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? -1;

        // Уже скачан целиком? Не качаем заново.
        if (File.Exists(dest) && total > 0 && new FileInfo(dest).Length == total)
        {
            progress.Report((total, total));
            return dest;
        }

        await using (var http = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            var lastReport = DateTime.MinValue;
            while ((read = await http.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 100)
                {
                    lastReport = DateTime.UtcNow;
                    progress.Report((done, total));
                }
            }
            progress.Report((done, total));
        }
        File.Move(tmp, dest, overwrite: true);
        Log.Info("Nvidia", $"Драйвер скачан: {dest}");
        return dest;
    }

    /// <summary>WMI-версия "32.0.15.6603" → GeForce-версия "566.03" (последние 5 цифр).</summary>
    public static string WmiToGeforceVersion(string wmiVersion)
    {
        string digits = new(wmiVersion.Where(char.IsDigit).ToArray());
        if (digits.Length < 5) return wmiVersion;
        string last5 = digits[^5..];
        return $"{last5[..3]}.{last5[3..]}";
    }

    private static int CompareVersions(string a, string b)
    {
        double.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out double va);
        double.TryParse(b, System.Globalization.CultureInfo.InvariantCulture, out double vb);
        return va.CompareTo(vb);
    }

    private static string CleanGpuName(string name) =>
        name.Replace("NVIDIA", "", StringComparison.OrdinalIgnoreCase).Trim();

    /// <summary>id/series в nvidia-data встречаются и числом, и строкой — читаем оба варианта.</summary>
    private static int? ReadFlexibleInt(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out int n) => n,
            JsonValueKind.String when int.TryParse(v.GetString(), out int n) => n,
            _ => null
        };
    }
}
