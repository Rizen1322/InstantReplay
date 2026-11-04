using Aura.Core.Saving.Mp4;

namespace Aura.Core.Audio;

/// <summary>
/// Сведение звуковых дорожек фрагмента в одну — так же, как микшер сводит их вживую
/// (дорожка «Вместе» в записи): моно-микрофон в оба канала, сумма, лимитер −1 дБ,
/// AAC-LC стерео 192 кбит/с. Всё штатными средствами Windows, без ffmpeg.
/// </summary>
public static class AudioMixdown
{
    private const int BlockFrames = 480;   // 10 мс, как у живого микшера

    public static Mp4Trim.AacClip Mix(IReadOnlyList<Mp4Trim.AacClip> tracks)
    {
        const int rate = AacEncoder.SampleRate;
        if (tracks.Count == 0) throw new ArgumentException("нечего сводить");
        if (tracks.Any(t => t.SampleRate != rate || t.Channels is < 1 or > 2))
            throw new NotSupportedException("сведение умеет только 48 кГц моно и стерео");

        double start = tracks.Min(t => t.StartSeconds);
        var decoded = tracks.Select(t => (Offset: (int)Math.Round((t.StartSeconds - start) * rate), Stereo: DecodeStereo(t))).ToList();
        int total = decoded.Max(d => d.Offset + d.Stereo.Length / 2);

        // Сумма плюс хвост длиной в задержку лимитера: он отдаёт звук на 96 сэмплов позже
        var mix = new float[(total + Limiter.LookaheadFrames) * 2];
        foreach (var (offset, stereo) in decoded)
            for (int i = 0; i < stereo.Length; i++) mix[offset * 2 + i] += stereo[i];
        new Limiter(2).Process(mix, total + Limiter.LookaheadFrames);
        var aligned = mix.AsSpan(Limiter.LookaheadFrames * 2, total * 2);

        int bitrate = AudioMixerEngine.GameBitrate;
        var frames = new List<byte[]>();
        using var encoder = new AacEncoder(2, bitrate);
        encoder.FrameReady += (frame, _) => frames.Add(frame.ToArray());
        // Тишина в конце дотягивает последний неполный кадр
        int expected = (total + Mp4AudioFormat.FrameSamples - 1) / Mp4AudioFormat.FrameSamples;
        var block = new float[BlockFrames * 2];
        for (int at = 0; frames.Count < expected && at < total + 4 * Mp4AudioFormat.FrameSamples; at += BlockFrames)
        {
            Array.Clear(block);
            int have = Math.Clamp(total - at, 0, BlockFrames);
            if (have > 0) aligned.Slice(at * 2, have * 2).CopyTo(block);
            encoder.Encode(block, BlockFrames, (long)at * 10_000_000 / rate);
        }
        if (frames.Count > expected) frames.RemoveRange(expected, frames.Count - expected);
        return new Mp4Trim.AacClip(rate, 2, encoder.AudioSpecificConfig, bitrate, frames, start);
    }

    /// <summary>Декодировать дорожку в стерео float: моно раскладывается в оба канала.</summary>
    private static float[] DecodeStereo(Mp4Trim.AacClip clip)
    {
        var pcm = new List<float>(clip.Frames.Count * Mp4AudioFormat.FrameSamples * clip.Channels);
        using (var decoder = new AacDecoder(clip.SampleRate, clip.Channels, clip.Config))
            for (int i = 0; i < clip.Frames.Count; i++)
                decoder.Decode(clip.Frames[i], (long)i * Mp4AudioFormat.FrameSamples * 10_000_000 / clip.SampleRate, pcm);
        if (clip.Channels == 2) return pcm.ToArray();
        var stereo = new float[pcm.Count * 2];
        for (int i = 0; i < pcm.Count; i++) stereo[2 * i] = stereo[2 * i + 1] = pcm[i];
        return stereo;
    }
}
