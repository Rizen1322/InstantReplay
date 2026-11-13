using Aura.Core.Audio;
using Aura.Core.Saving.Mp4;
using Vortice.MediaFoundation;

// Run on Windows: dotnet run --project tests/AudioMixdown.SelfCheck -c Release -p:Platform=x64
static void Require(bool passed, string message) { if (!passed) throw new Exception(message); }

static Mp4Trim.AacClip Tone(double frequency, int channels)
{
    var frames = new List<byte[]>();
    using var encoder = new AacEncoder(channels, 192000);
    encoder.FrameReady += (data, _) => frames.Add(data.ToArray());
    var pcm = new float[480 * channels];
    for (int at = 0; at < 48000 * 4; at += 480)
    {
        for (int j = 0; j < 480; j++)
            for (int ch = 0; ch < channels; ch++)
                pcm[j * channels + ch] = (float)(0.15 * Math.Sin(2 * Math.PI * frequency * (at + j) / 48000));
        encoder.Encode(pcm, 480, (long)at * 10_000_000 / 48000);
    }
    return new Mp4Trim.AacClip(48000, channels, encoder.AudioSpecificConfig.ToArray(), 192000, frames, 0);
}

static List<float> Decode(Mp4Trim.AacClip clip)
{
    var pcm = new List<float>();
    using var decoder = new AacDecoder(clip.SampleRate, clip.Channels, clip.Config);
    for (int i = 0; i < clip.Frames.Count; i++)
        decoder.Decode(clip.Frames[i], (long)i * 1024 * 10_000_000 / 48000, pcm);
    return pcm;
}

static double Amplitude(List<float> pcm, double hz, double start, double seconds)
{
    double re = 0, im = 0;
    int first = (int)(start * 48000), count = (int)(seconds * 48000);
    for (int i = 0; i < count; i++)
    {
        re += pcm[(first + i) * 2] * Math.Cos(2 * Math.PI * hz * i / 48000);
        im += pcm[(first + i) * 2] * Math.Sin(2 * Math.PI * hz * i / 48000);
    }
    return 2 * Math.Sqrt(re * re + im * im) / count;
}

MediaFactory.MFStartup();
try
{
    var game = Tone(440, 2);
    var mic = Tone(880, 1) with { StartSeconds = 0.5 };
    var mixed = AudioMixdown.Mix([game, mic]);
    var pcm = Decode(mixed);
    Require(Amplitude(pcm, 440, 1, 1) > 0.1, "Game audio disappeared");
    Require(Amplitude(pcm, 880, 1, 1) > 0.1, "Microphone audio disappeared");
    Require(Amplitude(pcm, 880, 0.1, 0.2) < 0.005, "Microphone lost its start delay");
    int expectedSamples = Math.Max(Decode(game).Count / game.Channels,
        24000 + Decode(mic).Count / mic.Channels);
    Require(mixed.Frames.Count == (expectedSamples + 1023) / 1024, "Mixed duration changed");

    using (var canceled = new CancellationTokenSource())
    {
        canceled.Cancel();
        bool observed = false;
        try { AudioMixdown.Mix([game, mic], canceled.Token); }
        catch (OperationCanceledException) { observed = true; }
        Require(observed, "Mixing ignored cancellation");
    }

    // Reuse compressed frame references so this measures the mixer, not fixture allocation.
    var longGame = game with { Frames = Enumerable.Range(0, 45).SelectMany(_ => game.Frames).ToArray() };
    var longMic = mic with { Frames = Enumerable.Range(0, 45).SelectMany(_ => mic.Frames).ToArray() };
    long baseline = GC.GetTotalMemory(forceFullCollection: true), peak = baseline;
    using var monitor = new Timer(_ =>
    {
        long current = GC.GetTotalMemory(false), seen;
        do { seen = Interlocked.Read(ref peak); if (current <= seen) break; }
        while (Interlocked.CompareExchange(ref peak, current, seen) != seen);
    }, null, 0, 10);
    var longMix = AudioMixdown.Mix([longGame, longMic]);
    Require(longMix.Frames.Count > 8000, "Long preview was truncated");
    Require(peak - baseline < 128L << 20, "Mixing retained full-length PCM");
    GC.KeepAlive(longMix);
    Console.WriteLine($"PASS: both tracks, mono/stereo, offset, duration, cancellation; 3-minute mix peak +{(peak - baseline) / (1024 * 1024)} MB");
}
finally { MediaFactory.MFShutdown(); }
