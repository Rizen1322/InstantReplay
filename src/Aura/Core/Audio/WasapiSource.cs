using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Aura.Core.Logging;

namespace Aura.Core.Audio;

/// <summary>
/// Источник звука WASAPI (звук системы через loopback или микрофон), который кладёт
/// пакеты на <see cref="AudioTimeline"/> по их настоящему времени.
///
/// Свой цикл захвата вместо WasapiCapture из NAudio: тот отдаёт байты без меток
/// времени, а без них звук нельзя положить точно рядом с кадрами видео. Здесь
/// каждый пакет берётся вместе с u64QPCPosition — моментом, когда его первый кадр
/// прозвучал (loopback) или был записан (микрофон), — в тех же 100-нс тиках QPC,
/// что и время кадров захвата экрана.
/// </summary>
public sealed class WasapiSource : IDisposable
{
    private readonly MMDevice _device;
    private readonly AudioClient _client;
    private readonly AudioCaptureClient _capture;
    private readonly WaveFormat _format;
    private readonly AudioTimeline _timeline;
    private readonly EventWaitHandle _packetReady = new(false, EventResetMode.AutoReset);
    private readonly Thread _thread;
    private readonly string _name;
    private readonly bool _loopback;
    private volatile bool _running = true;

    // Матрица приведения каналов устройства к каналам шкалы
    private readonly float[] _matrix;   // [выходной канал * входных каналов + входной]
    private readonly int _inChannels;
    private float[] _converted = new float[4096];

    public string? FellBackTo { get; }

    public event Action<Exception>? Failed;

    public WasapiSource(bool loopback, string? deviceId, AudioTimeline timeline)
    {
        _loopback = loopback;
        _timeline = timeline;
        using var enumerator = new MMDeviceEnumerator();
        var flow = loopback ? DataFlow.Render : DataFlow.Capture;
        (_device, FellBackTo) = Resolve(enumerator, flow, deviceId, loopback);
        _name = $"{(loopback ? "loopback" : "mic")} {_device.FriendlyName}";

        _client = _device.AudioClient;
        _format = _client.MixFormat;
        _inChannels = _format.Channels;
        _matrix = BuildMatrix(_inChannels, timeline.Channels);

        var flags = AudioClientStreamFlags.EventCallback;
        if (loopback) flags |= AudioClientStreamFlags.Loopback;
        // 100 мс буфера устройства: пакеты забираем по событию, но под нагрузкой
        // игры поток может проснуться с опозданием — запас не даёт потерять звук.
        _client.Initialize(AudioClientShareMode.Shared, flags, 1_000_000, 0, _format, Guid.Empty);
        _client.SetEventHandle(_packetReady.SafeWaitHandle.DangerousGetHandle());
        _capture = _client.AudioCaptureClient;

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = loopback ? "Audio.Loopback" : "Audio.Mic",
            Priority = ThreadPriority.Highest
        };
        _client.Start();
        _thread.Start();

        Log.Info("Audio", $"Источник запущен: {_name} ({_format.SampleRate} Гц, {_format.Channels} кан., " +
                          $"{Describe(_format)})");
    }

    private static (MMDevice, string?) Resolve(MMDeviceEnumerator enumerator, DataFlow flow, string? deviceId, bool loopback)
    {
        if (deviceId is not null)
            try { return (enumerator.GetDevice(deviceId), null); }
            catch (Exception ex)
            {
                var fallback = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                Log.Warn("Audio", $"Выбранное устройство ({(loopback ? "звук игры" : "микрофон")}) " +
                                  $"не найдено ({ex.Message}) — беру по умолчанию: {fallback.FriendlyName}");
                return (fallback, fallback.FriendlyName);
            }

        return (enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia), null);
    }

    private void Loop()
    {
        using var mmcss = Interop.Mmcss.Join(Interop.Mmcss.ProAudio, _name);
        try
        {
            while (_running)
            {
                // Loopback при полной тишине пакетов не присылает вовсе — это норма,
                // шкала сама даёт тишину там, где звука нет.
                _packetReady.WaitOne(10); // и опрос раз в 10 мс: на части драйверов событие loopback приходит не всегда
                if (!_running) break;
                Drain();
            }
        }
        catch (Exception ex)
        {
            if (!_running) return;
            Log.Error("Audio", $"Источник {_name} остановился: {ex.Message}");
            Failed?.Invoke(ex);
        }
    }

    private void Drain()
    {
        while (_running && _capture.GetNextPacketSize() > 0)
        {
            IntPtr data = _capture.GetBuffer(out int frames, out AudioClientBufferFlags flags,
                                             out _, out long qpc);
            try
            {
                if (frames <= 0) continue;
                bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
                bool discontinuity = (flags & AudioClientBufferFlags.DataDiscontinuity) != 0;
                bool badTime = (flags & AudioClientBufferFlags.TimestampError) != 0;
                if (badTime) qpc = NowTicks();

                Convert(data, frames, silent);
                _timeline.Push(_converted, frames, _format.SampleRate, qpc, discontinuity);
            }
            finally
            {
                _capture.ReleaseBuffer(frames);
            }
        }
    }

    private static long NowTicks() =>
        (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (10_000_000.0 / System.Diagnostics.Stopwatch.Frequency));

    /// <summary>Байты устройства → float в каналах шкалы.</summary>
    private unsafe void Convert(IntPtr data, int frames, bool silent)
    {
        int outChannels = _timeline.Channels;
        if (_converted.Length < frames * outChannels) _converted = new float[frames * outChannels * 2];
        var dest = _converted.AsSpan(0, frames * outChannels);
        if (silent) { dest.Clear(); return; }

        int bits = _format.BitsPerSample;
        bool isFloat = IsFloat(_format);
        int blockAlign = _format.BlockAlign;
        int bytesPerSample = blockAlign / _inChannels;
        byte* src = (byte*)data;
        Span<float> frame = stackalloc float[Math.Max(1, _inChannels)];

        for (int f = 0; f < frames; f++)
        {
            byte* p = src + f * blockAlign;
            for (int c = 0; c < _inChannels; c++)
            {
                byte* s = p + c * bytesPerSample;
                frame[c] = isFloat && bits == 32 ? *(float*)s
                    : bits == 16 ? *(short*)s / 32768f
                    : bits == 24 ? ((s[0] << 8 | s[1] << 16 | s[2] << 24) >> 8) / 8388608f
                    : bits == 32 ? *(int*)s / 2147483648f
                    : 0f;
            }
            for (int o = 0; o < outChannels; o++)
            {
                float sum = 0;
                int row = o * _inChannels;
                for (int c = 0; c < _inChannels; c++) sum += frame[c] * _matrix[row + c];
                dest[f * outChannels + o] = sum;
            }
        }
    }

    private static bool IsFloat(WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat) return true;
        if (format is WaveFormatExtensible ext)
            return ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        return false;
    }

    private static string Describe(WaveFormat format) =>
        IsFloat(format) ? $"float{format.BitsPerSample}" : $"pcm{format.BitsPerSample}";

    /// <summary>
    /// Как свести каналы устройства в каналы шкалы. Для стерео — та же раскладка,
    /// что и прежде (<see cref="AudioFormat"/>): фронт по сторонам, центр поровну,
    /// LFE мимо. Для моно (микрофон) — среднее фронтальных каналов.
    /// </summary>
    internal static float[] BuildMatrix(int inChannels, int outChannels)
    {
        var m = new float[outChannels * inChannels];
        if (outChannels == 1)
        {
            int used = Math.Min(2, inChannels);
            for (int c = 0; c < used; c++) m[c] = 1f / used;
            return m;
        }

        var (left, right) = AudioFormat.StereoMatrix(inChannels);
        for (int c = 0; c < inChannels; c++)
        {
            m[c] = left[c];
            m[inChannels + c] = right[c];
        }
        return m;
    }

    public void Dispose()
    {
        _running = false;
        _packetReady.Set();
        if (!_thread.Join(1000))
        {
            // Поток всё ещё в GetBuffer (драйвер завис) — освободить клиента под ним
            // значит уронить процесс. Устройство бросаем, остальное сделает сборщик.
            Log.Warn("Audio", $"Поток захвата {_name} не завершился — источник оставлен");
            return;
        }
        try { _client.Stop(); } catch { }
        _capture.Dispose();
        _client.Dispose();
        _device.Dispose();
        _packetReady.Dispose();
    }
}
