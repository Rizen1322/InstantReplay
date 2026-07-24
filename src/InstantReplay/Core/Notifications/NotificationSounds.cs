using System.Runtime.InteropServices;
using InstantReplay.Core.Logging;
using InstantReplay.Core.Settings;

namespace InstantReplay.Core.Notifications;

/// <summary>
/// Звук сохранения. Встроенные варианты синтезируются в WAV прямо в памяти
/// (никаких ресурсов/файлов): Classic — двухтоновый «дзынь» вверх,
/// Soft — короткий мягкий тон с плавным затуханием. Custom — свой WAV-файл.
/// Буфер держим в static-поле: SND_ASYNC играет из нашей памяти.
/// </summary>
public static class NotificationSounds
{
    private const int Rate = 44100;

    private static readonly Lazy<byte[]> Classic = new(() => Synth(
        [(660, 0.00, 0.10, 0.55), (880, 0.09, 0.22, 0.5)]));
    private static readonly Lazy<byte[]> Soft = new(() => Synth(
        [(523, 0.00, 0.22, 0.35), (784, 0.02, 0.20, 0.18)]));

    private static byte[]? _playing; // держим ссылку, пока играет SND_ASYNC|SND_MEMORY

    public static void Play(SaveSound sound, string? customPath)
    {
        try
        {
            switch (sound)
            {
                case SaveSound.None: return;
                case SaveSound.Custom when !string.IsNullOrWhiteSpace(customPath) && File.Exists(customPath):
                    PlaySoundW(customPath, IntPtr.Zero, SND_FILENAME | SND_ASYNC | SND_NODEFAULT);
                    return;
                case SaveSound.Classic: PlayMemory(Classic.Value); return;
                default: PlayMemory(Soft.Value); return;
            }
        }
        catch (Exception ex) { Log.Warn("Sound", ex.Message); }
    }

    private static void PlayMemory(byte[] wav)
    {
        _playing = wav;
        PlaySoundBytes(_playing, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
    }

    /// <summary>Синтез WAV: набор синусов (частота, старт сек, длительность сек, громкость).</summary>
    private static byte[] Synth((double freq, double start, double dur, double vol)[] notes)
    {
        double total = notes.Max(n => n.start + n.dur) + 0.05;
        int samples = (int)(total * Rate);
        var mix = new double[samples];
        foreach (var (freq, start, dur, vol) in notes)
        {
            int s0 = (int)(start * Rate), len = (int)(dur * Rate);
            for (int i = 0; i < len && s0 + i < samples; i++)
            {
                double t = i / (double)Rate;
                // Огибающая: быстрая атака 8 мс, экспоненциальный спад
                double env = Math.Min(1.0, t / 0.008) * Math.Exp(-3.5 * t / dur);
                mix[s0 + i] += Math.Sin(2 * Math.PI * freq * t) * vol * env;
            }
        }

        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
            pcm[i] = (short)(Math.Clamp(mix[i], -1, 1) * short.MaxValue);

        // WAV-заголовок (PCM 16-bit mono)
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataLen = samples * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (short s in pcm) w.Write(s);
        return ms.ToArray();
    }

    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004, SND_FILENAME = 0x00020000;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern bool PlaySoundW(string pszSound, IntPtr hmod, uint fdwSound);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    private static extern bool PlaySoundBytes(byte[] pszSound, IntPtr hmod, uint fdwSound);
}
