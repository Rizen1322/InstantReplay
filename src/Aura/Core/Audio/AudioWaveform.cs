using Aura.Core.Saving.Mp4;

namespace Aura.Core.Audio;

public sealed class AudioWaveform
{
    public float[] Peaks { get; }
    public double StartSeconds { get; }
    public int PointsPerSecond { get; }
    private readonly List<float[]> _levels = [];

    public AudioWaveform(float[] peaks, double startSeconds, int pointsPerSecond = 50)
    {
        Peaks = peaks; StartSeconds = startSeconds; PointsPerSecond = pointsPerSecond;
        _levels.Add(peaks);
        while (_levels[^1].Length > 1)
        {
            var previous = _levels[^1];
            var next = new float[(previous.Length + 1) / 2];
            for (int i = 0; i < next.Length; i++) next[i] = Math.Max(previous[i * 2], previous[Math.Min(i * 2 + 1, previous.Length - 1)]);
            _levels.Add(next);
        }
    }

    public float Peak(double start, double end)
    {
        int first = Math.Clamp((int)((start - StartSeconds) * PointsPerSecond), 0, Peaks.Length);
        int last = Math.Clamp((int)Math.Ceiling((end - StartSeconds) * PointsPerSecond), first, Peaks.Length);
        float peak = 0;
        while (first < last)
        {
            int level = 0;
            while (level + 1 < _levels.Count && first % (1 << (level + 1)) == 0 && first + (1 << (level + 1)) <= last) level++;
            peak = Math.Max(peak, _levels[level][first >> level]);
            first += 1 << level;
        }
        return peak;
    }

    public static IReadOnlyList<AudioWaveform> Read(string path, CancellationToken ct)
    {
        var result = new List<AudioWaveform>();
        foreach (var audio in Mp4Trim.ReadAudio(path, ct))
        {
            using var decoder = new AacDecoder(audio.SampleRate, audio.Channels, audio.Config);
            var peaks = new List<float>();
            var pcm = new List<float>();
            long position = 0;
            for (int frame = 0; frame < audio.Frames.Count; frame++)
            {
                ct.ThrowIfCancellationRequested();
                pcm.Clear();
                decoder.Decode(audio.Frames[frame], (long)frame * Mp4AudioFormat.FrameSamples * 10_000_000 / audio.SampleRate, pcm);
                if (pcm.Count % audio.Channels != 0) throw new InvalidDataException("Неполный звуковой кадр.");
                for (int i = 0; i < pcm.Count; i += audio.Channels, position++)
                {
                    int bin = checked((int)(position * 50 / audio.SampleRate));
                    while (peaks.Count <= bin) peaks.Add(0);
                    for (int channel = 0; channel < audio.Channels; channel++)
                        peaks[bin] = Math.Max(peaks[bin], Math.Abs(pcm[i + channel]));
                }
            }
            result.Add(new(peaks.ToArray(), audio.StartSeconds));
        }
        return result;
    }
}
