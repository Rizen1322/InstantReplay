using System.Runtime.InteropServices;
using Aura.Core.Logging;
using Aura.Core.Settings;

namespace Aura.Core.Notifications;

/// <summary>
/// Звук сохранения. Встроенные варианты синтезируются в WAV прямо в памяти
/// (никаких ресурсов и файлов), Custom — свой WAV-файл. Буфер держим в
/// static-поле: SND_ASYNC играет из нашей памяти.
///
/// Как сделано, чтобы звук был приятным поверх игры:
/// - только гармоничные обертоны и консонансы (кварта, квинта). Негармоничный
///   «колокол» и эхо короткими задержками звучали металлически;
/// - мягкая атака 6-10 мс, без щелчка, и плавный экспоненциальный спад;
/// - ширина стерео от лёгкой расстройки каналов, а не от отражений;
/// - срез верхов на 7 кГц и пик около −15 дБ: звук слышно, но он не режет.
/// </summary>
public static class NotificationSounds
{
    private const int Rate = 48000;

    /// <summary>
    /// Нота: частота, начало (с), спад (с), громкость, атака (с), глубина FM и
    /// отношение модулятора. FM даёт мягкий «электропиано» призвук в начале ноты,
    /// который за 60 мс сходит на чистый тон.
    /// </summary>
    private readonly record struct Note(double Freq, double Start, double Decay, double Volume,
                                        double Attack = 0.008, double FmIndex = 0, double FmRatio = 1,
                                        double Octave = 0.12);

    // Мягкий: две чистые ноты вверх на кварту, соль и до. Короткий и круглый.
    private static readonly Lazy<byte[]> SoftWav = new(() => Render(
    [
        new Note(783.99, 0.000, 0.10, 0.8, Attack: 0.006, Octave: 0.08),
        new Note(1046.5, 0.085, 0.22, 0.9, Attack: 0.006, Octave: 0.08),
    ]));

    // Колокольчик: электропиано, ми и си (квинта), первая чуть тише.
    private static readonly Lazy<byte[]> ClassicWav = new(() => Render(
    [
        new Note(659.25, 0.000, 0.28, 0.7, Attack: 0.004, FmIndex: 1.6, FmRatio: 1),
        new Note(987.77, 0.100, 0.45, 0.85, Attack: 0.004, FmIndex: 1.4, FmRatio: 1),
    ]));

    // Стекло: одна высокая нота с тёплой нижней октавой и долгим тихим хвостом.
    private static readonly Lazy<byte[]> GlassWav = new(() => Render(
    [
        new Note(1318.5, 0.000, 0.55, 0.75, Attack: 0.004, FmIndex: 0.5, FmRatio: 2, Octave: 0.0),
        new Note(659.25, 0.000, 0.35, 0.35, Attack: 0.010, Octave: 0.0),
        new Note(1975.5, 0.045, 0.40, 0.25, Attack: 0.004, Octave: 0.0),
    ]));

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
                case SaveSound.Classic: PlayMemory(ClassicWav.Value); return;
                case SaveSound.Glass: PlayMemory(GlassWav.Value); return;
                default: PlayMemory(SoftWav.Value); return;
            }
        }
        catch (Exception ex) { Log.Warn("Sound", ex.Message); }
    }

    private static void PlayMemory(byte[] wav)
    {
        _playing = wav;
        PlaySoundBytes(_playing, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
    }

    /// <summary>Готовый WAV для тестов и проверки на слух.</summary>
    internal static byte[] Wav(SaveSound sound) => sound switch
    {
        SaveSound.Classic => ClassicWav.Value,
        SaveSound.Glass => GlassWav.Value,
        _ => SoftWav.Value
    };

    private static byte[] Render(Note[] notes)
    {
        double tail = notes.Max(n => n.Start + n.Decay * 6);
        int samples = (int)((tail + 0.05) * Rate);
        var left = new double[samples];
        var right = new double[samples];

        foreach (var note in notes)
        {
            int s0 = (int)(note.Start * Rate);
            int len = Math.Min(samples - s0, (int)(note.Decay * 6 * Rate));
            // Каналы расстроены на доли герца: звук шире, но без эха
            for (int ch = 0; ch < 2; ch++)
            {
                var buffer = ch == 0 ? left : right;
                double freq = note.Freq * (ch == 0 ? 0.9993 : 1.0007);
                double phase = 0, modPhase = 0, octPhase = 0;
                for (int i = 0; i < len; i++)
                {
                    double t = i / (double)Rate;
                    double attack = t < note.Attack ? 0.5 - 0.5 * Math.Cos(Math.PI * t / note.Attack) : 1;
                    double env = attack * Math.Exp(-t / note.Decay);
                    double index = note.FmIndex * Math.Exp(-t / 0.06);
                    modPhase += 2 * Math.PI * freq * note.FmRatio / Rate;
                    phase += 2 * Math.PI * freq / Rate;
                    octPhase += 2 * Math.PI * freq * 2 / Rate;
                    double v = Math.Sin(phase + index * Math.Sin(modPhase))
                             + note.Octave * Math.Sin(octPhase) * Math.Exp(-t / (note.Decay * 0.5));
                    buffer[s0 + i] += v * note.Volume * env;
                }
            }
        }

        // Срез верхов: двойной однополюсный фильтр на 7 кГц
        double a = Math.Exp(-2 * Math.PI * 7000.0 / Rate);
        double peak = 1e-9;
        foreach (var buffer in new[] { left, right })
        {
            double y1 = 0, y2 = 0;
            for (int i = 0; i < samples; i++)
            {
                y1 = (1 - a) * buffer[i] + a * y1;
                y2 = (1 - a) * y1 + a * y2;
                buffer[i] = y2;
                peak = Math.Max(peak, Math.Abs(y2));
            }
        }

        // Пик около −15 дБ и плавный конец последних 30 мс
        double norm = 0.18 / peak;
        int fade = (int)(0.03 * Rate);
        using var stream = new MemoryStream(44 + samples * 4);
        using var w = new BinaryWriter(stream);
        int dataLen = samples * 4;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        for (int i = 0; i < samples; i++)
        {
            double f = i > samples - fade ? (samples - i) / (double)fade : 1;
            w.Write((short)(Math.Clamp(left[i] * norm * f, -1, 1) * short.MaxValue));
            w.Write((short)(Math.Clamp(right[i] * norm * f, -1, 1) * short.MaxValue));
        }
        return stream.ToArray();
    }

    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004, SND_FILENAME = 0x00020000;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode)]
    private static extern bool PlaySoundW(string pszSound, IntPtr hmod, uint fdwSound);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    private static extern bool PlaySoundBytes(byte[] pszSound, IntPtr hmod, uint fdwSound);
}
