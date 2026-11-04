using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Aura.Core.Audio;

/// <summary>
/// Декодирование кадров AAC в float PCM штатным декодером Windows (CLSID_CMSAACDecMFT).
/// Есть в любой Windows, в отличие от декодеров HEVC и AV1. Нужен редактору: свести
/// игру и микрофон во фрагменте без ffmpeg.
///
/// Пара с <see cref="AacEncoder"/> проверена замером: сквозная задержка нулевая,
/// кадр N даёт сэмплы [N·1024, N·1024 + 1024) — время считается подряд по кадрам.
/// </summary>
public sealed class AacDecoder : IDisposable
{
    private static readonly Guid ClsidAacDecoder = new("32D186A7-218F-4C75-8876-DD77273A8999");

    private readonly IMFTransform _transform;
    private readonly int _outputSize;
    private byte[] _outBuffer = new byte[16384];

    public int Channels { get; }

    public AacDecoder(int sampleRate, int channels, byte[] audioSpecificConfig)
    {
        Channels = channels;
        _transform = Create();

        // MF_MT_USER_DATA декодера — хвост HEAACWAVEINFO (12 байт) и AudioSpecificConfig:
        // тот же формат, что отдаёт энкодер (см. Mp4AudioFormat.AscFromMfUserData)
        var userData = new byte[12 + audioSpecificConfig.Length];
        audioSpecificConfig.CopyTo(userData, 12);
        using (var input = MediaFactory.MFCreateMediaType())
        {
            input.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Audio);
            input.Set(MediaTypeAttributeKeys.Subtype, AudioFormatGuids.Aac);
            input.Set(MediaTypeAttributeKeys.AudioSamplesPerSecond, (uint)sampleRate);
            input.Set(MediaTypeAttributeKeys.AudioNumChannels, (uint)channels);
            input.Set(MediaTypeAttributeKeys.AacPayloadType, 0u);   // сырой AAC, без ADTS
            input.SetBlob(MediaTypeAttributeKeys.UserData, userData);
            _transform.SetInputType(0, input, 0);
        }
        // Выход берём из предложенных декодером: собранный вручную тип он отвергает
        // (у него свои обязательные атрибуты). Нужен float с тем же числом каналов.
        bool chosen = false;
        for (int index = 0; !chosen; index++)
        {
            IMFMediaType offered;
            try { offered = _transform.GetOutputAvailableType(0, index); }
            catch { break; }
            using (offered)
            {
                if (offered.GetGUID(MediaTypeAttributeKeys.Subtype) != AudioFormatGuids.Float ||
                    offered.GetUInt32(MediaTypeAttributeKeys.AudioNumChannels) != (uint)channels) continue;
                _transform.SetOutputType(0, offered, 0);
                chosen = true;
            }
        }
        if (!chosen) throw new NotSupportedException("декодер AAC не отдаёт float");
        _outputSize = Math.Max(_transform.GetOutputStreamInfo(0).Size, 1024 * channels * 4 * 2);
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    private static IMFTransform Create()
    {
        var type = Type.GetTypeFromCLSID(ClsidAacDecoder, throwOnError: true)!;
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

    /// <summary>Декодировать кадр; сэмплы (чередование каналов) дописываются в <paramref name="pcm"/>.</summary>
    public void Decode(ReadOnlySpan<byte> frame, long ptsTicks, List<float> pcm)
    {
        using (var buffer = MediaFactory.MFCreateMemoryBuffer(frame.Length))
        using (var sample = MediaFactory.MFCreateSample())
        {
            buffer.Lock(out IntPtr ptr, out _, out _);
            unsafe { frame.CopyTo(new Span<byte>((void*)ptr, frame.Length)); }
            buffer.Unlock();
            buffer.CurrentLength = frame.Length;
            sample.AddBuffer(buffer);
            sample.SampleTime = ptsTicks;
            _transform.ProcessInput(0, sample, 0);
        }
        Drain(pcm);
    }

    private void Drain(List<float> pcm)
    {
        while (true)
        {
            using var sample = MediaFactory.MFCreateSample();
            using (var buffer = MediaFactory.MFCreateMemoryBuffer(_outputSize))
                sample.AddBuffer(buffer);
            var output = new OutputDataBuffer { StreamID = 0, Sample = sample };
            var hr = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref output, out _);
            output.Events?.Dispose();
            if (hr.Failure) return; // MF_E_TRANSFORM_NEED_MORE_INPUT

            using var contiguous = sample.ConvertToContiguousBuffer();
            contiguous.Lock(out IntPtr ptr, out _, out int length);
            try
            {
                if (_outBuffer.Length < length) _outBuffer = new byte[length * 2];
                Marshal.Copy(ptr, _outBuffer, 0, length);
            }
            finally { contiguous.Unlock(); }
            pcm.AddRange(MemoryMarshal.Cast<byte, float>(_outBuffer.AsSpan(0, length - length % 4)));
        }
    }

    public void Dispose()
    {
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch { }
        _transform.Dispose();
    }
}
