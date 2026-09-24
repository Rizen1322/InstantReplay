using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using Aura.Core.Logging;

namespace Aura.Core.Audio;

/// <summary>Обработчик готового кадра AAC. Байты валидны только на время вызова.</summary>
public delegate void AacFrameHandler(ReadOnlySpan<byte> frame, long ptsTicks);

/// <summary>
/// Кодирование PCM в AAC-LC штатным энкодером Windows (CLSID_AACMFTEncoder), по
/// мере записи.
///
/// ЗАЧЕМ СРАЗУ. Раньше буфер хранил звук несжатым (192 КБ/с на дорожку), а в AAC
/// его превращал писатель Media Foundation в момент сохранения — то есть под игрой,
/// пачкой на сотни секунд звука, и время сохранения росло с длиной клипа. Теперь
/// кодирование идёт ровно в темпе звука, по кадру в 21 мс, а буфер хранит готовые
/// кадры: в восемь раз меньше памяти, и сохранение сводится к копированию байт.
///
/// ВРЕМЯ. Проверено замером на этой паре энкодер/декодер Windows: сквозная задержка
/// нулевая, кадр N несёт сэмплы [N·1024, N·1024 + 1024) входа. Поэтому время кадра
/// считается от времени первого сэмпла, без поправки на «разогрев» энкодера.
///
/// Энкодер синхронный: вызывается из потока микшера после каждого блока 10 мс.
/// </summary>
public sealed class AacEncoder : IDisposable
{
    private static readonly Guid ClsidAacEncoder = new("93AF0C51-2275-45d2-A35B-F2BA21CAED00");

    public const int SampleRate = AudioTimeline.Rate;
    public const int FrameSamples = 1024;

    private readonly IMFTransform _transform;
    private readonly int _channels;
    private readonly int _outputSize;
    private long _anchorTicks = long.MinValue;   // время первого сэмпла после (пере)запуска
    private long _samplesIn;                      // сэмплов на канал подано с якоря
    private long _framesOut;                      // кадров выдано с якоря
    private byte[] _outBuffer = new byte[8192];
    private byte[] _inBytes = [];

    public event AacFrameHandler? FrameReady;

    /// <summary>AudioSpecificConfig — для описания дорожки в MP4.</summary>
    public byte[] AudioSpecificConfig { get; }

    public int Channels => _channels;

    public int Bitrate { get; }

    public AacEncoder(int channels, int bitrate)
    {
        _channels = channels;
        Bitrate = bitrate;
        _transform = Create();

        // Порядок важен: у этого энкодера сначала вход, потом выход — наоборот
        // он отвечает MF_E_INVALIDTYPE. И значения атрибутов — строго UINT32.
        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Pcm);
            input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)SampleRate);
            input.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
            input.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            input.Set(MediaTypeAttributeKeys.AudioBlockAlignment, (uint)(channels * 2));
            input.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(SampleRate * channels * 2));
            _transform.SetInputType(0, input, 0);
        }
        using (var output = MediaFactory.MFCreateMediaType())
        {
            output.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            output.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            output.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)SampleRate);
            output.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
            output.Set(MediaTypeAttributeKeys.AudioBitsPerSample, 16u);
            // Энкодер понимает ровно четыре значения: 96, 128, 160 и 192 кбит/с
            output.Set(MediaTypeAttributeKeys.AudioAvgBytesPerSecond, (uint)(bitrate / 8));
            output.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);   // сырой AAC, без ADTS
            _transform.SetOutputType(0, output, 0);
        }

        using (var current = _transform.GetOutputCurrentType(0))
            AudioSpecificConfig = Saving.Mp4.Mp4AudioFormat.AscFromMfUserData(
                current.GetBlob(MediaTypeAttributeKeys.UserData));

        _outputSize = Math.Max(_transform.GetOutputStreamInfo(0).Size, 4096);
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    /// <summary>
    /// Кадр AAC полной тишины с теми же параметрами, что у этого кодера. Им
    /// заполняются разрывы в звуке (перезапуск звука при смене устройства): кадры в
    /// MP4 идут подряд без меток времени, и дыру нельзя оставить пустой — звук после
    /// неё съехал бы назад.
    /// </summary>
    public static byte[]? CreateSilentFrame(int channels, int bitrate)
    {
        try
        {
            using var encoder = new AacEncoder(channels, bitrate);
            byte[]? last = null;
            encoder.FrameReady += (frame, _) => last = frame.ToArray();
            // Несколько кадров: первые кодер тратит на разгон, дальше идёт ровная тишина
            var zeros = new float[480 * channels];
            for (int i = 0; i < 20; i++) encoder.Encode(zeros, 480, i * 100_000L);
            return last;
        }
        catch (Exception ex)
        {
            Log.Info("Audio", $"Кадр тишины AAC не собран: {ex.Message}");
            return null;
        }
    }

    private static IMFTransform Create()
    {
        var type = Type.GetTypeFromCLSID(ClsidAacEncoder, throwOnError: true)!;
        object instance = Activator.CreateInstance(type)!;
        IntPtr unknown = Marshal.GetIUnknownForObject(instance);
        try
        {
            Guid iid = typeof(IMFTransform).GUID;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out IntPtr transform));
            return new IMFTransform(transform);
        }
        finally
        {
            Marshal.Release(unknown);
            Marshal.FinalReleaseComObject(instance);
        }
    }

    /// <summary>
    /// Подать блок PCM (float, чередование каналов). <paramref name="ptsTicks"/> —
    /// время первого сэмпла. Если оно не продолжает предыдущий блок (разрыв шкалы),
    /// энкодер сбрасывается и кадры отсчитываются заново от нового времени.
    /// </summary>
    public void Encode(ReadOnlySpan<float> samples, int frames, long ptsTicks)
    {
        long expected = _anchorTicks == long.MinValue ? long.MinValue
            : _anchorTicks + _samplesIn * 10_000_000 / SampleRate;
        if (_anchorTicks == long.MinValue || Math.Abs(ptsTicks - expected) > 10_000)
        {
            if (_anchorTicks != long.MinValue)
            {
                Log.Info("Audio", $"AAC: разрыв шкалы звука ({(ptsTicks - expected) / 10_000} мс) — энкодер начинает заново");
                _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
            }
            _anchorTicks = ptsTicks;
            _samplesIn = 0;
            _framesOut = 0;
        }

        int count = frames * _channels;
        int bytes = count * sizeof(short);
        if (_inBytes.Length < bytes) _inBytes = new byte[bytes];
        var pcm = MemoryMarshal.Cast<byte, short>(_inBytes.AsSpan(0, bytes));
        for (int i = 0; i < count; i++)
        {
            float v = samples[i] * 32767f;
            pcm[i] = (short)(v >= 32767f ? 32767 : v <= -32768f ? -32768 : MathF.Round(v));
        }

        using (var buffer = MediaFactory.MFCreateMemoryBuffer(bytes))
        using (var sample = MediaFactory.MFCreateSample())
        {
            buffer.Lock(out IntPtr ptr, out _, out _);
            Marshal.Copy(_inBytes, 0, ptr, bytes);
            buffer.Unlock();
            buffer.CurrentLength = bytes;
            sample.AddBuffer(buffer);
            sample.SampleTime = ptsTicks;
            sample.SampleDuration = (long)frames * 10_000_000 / SampleRate;
            _transform.ProcessInput(0, sample, 0);
        }
        _samplesIn += frames;
        Drain();
    }

    private void Drain()
    {
        while (true)
        {
            using var sample = MediaFactory.MFCreateSample();
            using (var buffer = MediaFactory.MFCreateMemoryBuffer(_outputSize))
                sample.AddBuffer(buffer);
            var output = new OutputDataBuffer { StreamID = 0, Sample = sample };
            var hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();
            if (hr.Failure) return; // MF_E_TRANSFORM_NEED_MORE_INPUT — кадр ещё не набрался

            using var contiguous = sample.ConvertToContiguousBuffer();
            contiguous.Lock(out IntPtr ptr, out _, out int length);
            try
            {
                if (_outBuffer.Length < length) _outBuffer = new byte[length * 2];
                Marshal.Copy(ptr, _outBuffer, 0, length);
            }
            finally { contiguous.Unlock(); }

            long pts = _anchorTicks + _framesOut * FrameSamples * 10_000_000 / SampleRate;
            _framesOut++;
            if (length > 0) FrameReady?.Invoke(_outBuffer.AsSpan(0, length), pts);
        }
    }

    public void Dispose()
    {
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        _transform.Dispose();
    }
}
