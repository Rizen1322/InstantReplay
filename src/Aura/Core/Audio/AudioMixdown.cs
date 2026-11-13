using Aura.Core.Saving.Mp4;

namespace Aura.Core.Audio;

/// <summary>Игра и микрофон в одну AAC-дорожку штатными кодеками Windows, без ffmpeg.</summary>
public static class AudioMixdown
{
    private const int BlockFrames = 480;

    public static Mp4Trim.AacClip Mix(IReadOnlyList<Mp4Trim.AacClip> tracks) => Mix(tracks, CancellationToken.None);

    public static Mp4Trim.AacClip Mix(IReadOnlyList<Mp4Trim.AacClip> tracks, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        const int rate = AacEncoder.SampleRate;
        if (tracks.Count == 0) throw new ArgumentException("нечего сводить");
        if (tracks.Any(t => t.SampleRate != rate || t.Channels is < 1 or > 2))
            throw new NotSupportedException("сведение умеет только 48 кГц моно и стерео");

        double start = tracks.Min(t => t.StartSeconds);
        var readers = new List<TrackReader>();
        try
        {
            foreach (var track in tracks) readers.Add(new TrackReader(track, start));
            int total = readers.Max(r => r.End);
            int bitrate = AudioMixerEngine.GameBitrate;
            var frames = new List<byte[]>();
            using var encoder = new AacEncoder(2, bitrate);
            encoder.FrameReady += (frame, _) => frames.Add(frame.ToArray());
            var limiter = new Limiter(2);
            var block = new float[BlockFrames * 2];
            int skip = Limiter.LookaheadFrames, emitted = 0;

            // Decode only the current block: full-length PCM would consume gigabytes on long recordings.
            for (int at = 0; at < checked(total + Limiter.LookaheadFrames); at = checked(at + BlockFrames))
            {
                ct.ThrowIfCancellationRequested();
                Array.Clear(block);
                foreach (var reader in readers) reader.AddTo(block, at);
                if (readers.All(r => r.Ended)) total = readers.Max(r => r.ActualEnd);
                limiter.Process(block, BlockFrames);
                int first = Math.Min(skip, BlockFrames);
                skip -= first;
                int have = Math.Min(BlockFrames - first, total - emitted);
                if (have <= 0) continue;
                encoder.Encode(block.AsSpan(first * 2, have * 2), have, (long)emitted * 10_000_000 / rate);
                emitted += have;
            }
            int expected = checked((total + Mp4AudioFormat.FrameSamples - 1) / Mp4AudioFormat.FrameSamples);
            Array.Clear(block);
            for (int tail = 0; frames.Count < expected && tail < 4 * Mp4AudioFormat.FrameSamples; tail += BlockFrames)
            {
                ct.ThrowIfCancellationRequested();
                encoder.Encode(block, BlockFrames, (long)(emitted + tail) * 10_000_000 / rate);
            }
            if (frames.Count < expected) throw new InvalidOperationException("AAC-энкодер не выдал весь сведённый звук");
            if (frames.Count > expected) frames.RemoveRange(expected, frames.Count - expected);
            return new Mp4Trim.AacClip(rate, 2, encoder.AudioSpecificConfig, bitrate, frames, start);
        }
        finally { foreach (var reader in readers) reader.Dispose(); }
    }

    private sealed class TrackReader : IDisposable
    {
        private readonly Mp4Trim.AacClip _clip;
        private readonly AacDecoder _decoder;
        private readonly List<float> _pcm = [];
        private readonly int _offset;
        private int _frame, _sample, _decodedFrames;
        public int End { get; }
        public int ActualEnd => checked(_offset + _decodedFrames);
        public bool Ended => _frame >= _clip.Frames.Count && _sample >= _pcm.Count;

        public TrackReader(Mp4Trim.AacClip clip, double start)
        {
            _clip = clip;
            _offset = checked((int)Math.Round((clip.StartSeconds - start) * clip.SampleRate));
            End = checked(_offset + clip.Frames.Count * Mp4AudioFormat.FrameSamples);
            _decoder = new AacDecoder(clip.SampleRate, clip.Channels, clip.Config);
        }

        public void AddTo(float[] block, int at)
        {
            int first = Math.Clamp(_offset - at, 0, BlockFrames);
            int last = Math.Clamp(End - at, 0, BlockFrames);
            while (first < last)
            {
                if (_sample >= _pcm.Count)
                {
                    if (_frame >= _clip.Frames.Count) return;
                    _pcm.Clear();
                    _sample = 0;
                    _decoder.Decode(_clip.Frames[_frame], (long)_frame * Mp4AudioFormat.FrameSamples *
                        10_000_000 / _clip.SampleRate, _pcm);
                    _frame++;
                    if (_pcm.Count % _clip.Channels != 0) throw new InvalidDataException("Декодер вернул неполный звуковой кадр");
                    _decodedFrames = checked(_decodedFrames + _pcm.Count / _clip.Channels);
                    continue;
                }
                int have = Math.Min(last - first, (_pcm.Count - _sample) / _clip.Channels);
                for (int i = 0; i < have; i++)
                {
                    block[(first + i) * 2] += _pcm[_sample++];
                    block[(first + i) * 2 + 1] += _clip.Channels == 1 ? _pcm[_sample - 1] : _pcm[_sample++];
                }
                first += have;
            }
        }

        public void Dispose() => _decoder.Dispose();
    }
}
