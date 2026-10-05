using System.Diagnostics;
using Aura.Core.Encoding;
using Aura.Core.Logging;
using Aura.Core.Settings;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Aura.Core.Diagnostics;

/// <summary>
/// Проверка в --dev: настоящий кодировщик Aura (хост NVENC, общий пул, пейсер) в
/// реальном времени получает сначала <c>warmup</c> секунд статичного кадра, как
/// рабочий стол перед игрой, а потом кадры из видеофайла (ffmpeg, 60 с с 60-й
/// секунды). Пишет в лог битрейт по 10 секунд и память процесса.
///
/// ЗАЧЕМ. Обычный VBR NVENC после минут статичного рабочего стола тратил на игре
/// вдвое больше заданного. Стенд rc_probe проверяет прослойку отдельно, а здесь
/// тот же сценарий проходит весь путь кадра в Aura.
/// </summary>
internal static class EncodeFileSelfTest
{
    public static void Run(string file, int warmupSeconds, int mbps, BitrateMode mode, int height = 1440)
    {
        const int fps = 60;
        int width = height * 16 / 9;
        D3D11.D3D11CreateDevice(null, Vortice.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0],
            out ID3D11Device? device, out _, out ID3D11DeviceContext? context).CheckError();
        using (var mt = device!.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);

        using var encoder = new VideoEncoder { BitrateMode = mode };
        long encodedBytes = 0;
        var perTen = new Dictionary<long, long>();
        long baseTicks = -1;
        encoder.FrameEncoded += f =>
        {
            Interlocked.Add(ref encodedBytes, f.Length);
            if (baseTicks < 0) baseTicks = f.PtsTicks;
            long bucket = (f.PtsTicks - baseTicks) / 100_000_000;   // 10 с
            lock (perTen) perTen[bucket] = perTen.GetValueOrDefault(bucket) + f.Length;
        };
        encoder.Initialize(device, width, height, fps, mbps * 1_000_000L, VideoCodec.HEVC, preferTenBit: true);
        bool tenBit = encoder.TenBit;
        Log.Info("SelfTest", $"кодировщик: {encoder.EncoderName}, {width}x{height}, {(tenBit ? "10" : "8")} бит, режим {mode}, {mbps} Мбит/с");

        var desc = new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = tenBit ? Format.P010 : Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        using var texture = device.CreateTexture2D(desc);
        int bpp = tenBit ? 2 : 1;
        var frame = new byte[width * height * bpp * 3 / 2];
        Array.Fill(frame, (byte)0x80);

        long interval = Stopwatch.Frequency / fps;
        long next = Stopwatch.GetTimestamp();
        void Submit()
        {
            unsafe
            {
                fixed (byte* p = frame)
                    lock (device) context!.UpdateSubresource(texture, 0, null, (IntPtr)p, (uint)(width * bpp), 0);
            }
            long ticks = (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
            encoder.SubmitFrame(texture, ticks, context!);
            next += interval;
            long wait = next - Stopwatch.GetTimestamp();
            if (wait > 0) Thread.Sleep(TimeSpan.FromTicks(wait * 10_000_000 / Stopwatch.Frequency));
        }

        for (int i = 0; i < warmupSeconds * fps; i++) Submit();
        long warmBytes = Interlocked.Read(ref encodedBytes);
        Log.Info("SelfTest", $"статичный кадр {warmupSeconds} с: {warmBytes * 8.0 / Math.Max(1, warmupSeconds) / 1e6:F2} Мбит/с");

        var psi = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string a in new[] { "-v", "error", "-ss", "60", "-t", "60", "-i", file, "-f", "rawvideo",
                                     "-pix_fmt", tenBit ? "p010le" : "nv12", "-s", $"{width}x{height}", "-" })
            psi.ArgumentList.Add(a);
        using var ffmpeg = Process.Start(psi)!;
        // stderr читаем всегда: иначе на битом файле ffmpeg встаёт на полном канале
        ffmpeg.ErrorDataReceived += (_, a) => { if (a.Data is not null) Log.Warn("SelfTest", $"ffmpeg: {a.Data}"); };
        ffmpeg.BeginErrorReadLine();
        var stdout = ffmpeg.StandardOutput.BaseStream;
        int frames = 0;
        long gameStart = Interlocked.Read(ref encodedBytes);
        while (ReadExactly(stdout, frame))
        {
            Submit();
            if (++frames % (fps * 10) == 0) Log.Info("SelfTest", $"подано {frames} кадров игры");
        }
        Thread.Sleep(500);
        long gameBytes = Interlocked.Read(ref encodedBytes) - gameStart;

        string buckets;
        lock (perTen) buckets = string.Join(" ", perTen.OrderBy(kv => kv.Key).Select(kv => $"{kv.Value * 8 / 10 / 1_000_000}"));
        using var self = Process.GetCurrentProcess();
        Log.Info("SelfTest", $"кадры игры: {frames}, средний {gameBytes * 8.0 / Math.Max(1, frames / (double)fps) / 1e6:F1} Мбит/с; " +
                             $"по 10 с от начала: {buckets}; частная память Aura {self.PrivateMemorySize64 >> 20} МБ");
        // Кодировщик раньше контекста: его пейсер дублирует кадры через контекст
        encoder.Dispose();
        context!.Dispose();
        device.Dispose();
    }

    private static bool ReadExactly(Stream stream, byte[] buffer)
    {
        int done = 0;
        while (done < buffer.Length)
        {
            int n = stream.Read(buffer, done, buffer.Length - done);
            if (n <= 0) return false;
            done += n;
        }
        return true;
    }
}
