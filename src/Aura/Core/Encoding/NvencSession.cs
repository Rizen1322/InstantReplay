using System.Runtime.InteropServices;
using Aura.Core.Logging;
using Aura.Core.Settings;

namespace Aura.Core.Encoding;

/// <summary>
/// Прямое кодирование через NVENC (прослойка Aura.Media64.dll, см.
/// src/native/Aura.Media).
///
/// ЗАЧЕМ МИМО MEDIA FOUNDATION. MFT от NVIDIA открывает лишь часть возможностей
/// NVENC: в логах он годами отвечал «не поддерживаю» на B-кадры, а адаптивного
/// квантования и просмотра вперёд в ICodecAPI нет вовсе. Именно это и отличает
/// картинку ShadowPlay: при том же битрейте NVENC с просмотром вперёд, адаптивным
/// квантованием (пространственным и временным) и B-кадрами тратит биты туда, где их
/// видно, — на движение и детали, а не на ровное небо. Разница видна на траве,
/// дыме и резких поворотах камеры.
///
/// Всё, чего конкретная видеокарта не умеет, прослойка выключает сама (по
/// NvEncGetEncodeCaps), а если сессия не открылась вовсе — энкодер уходит на MFT.
/// </summary>
internal sealed partial class NvencSession : IDisposable
{
    private const string Dll = "Aura.Media64.dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct Config
    {
        public int Codec, Width, Height, Fps;
        public int Bitrate, MaxBitrate, VbvBuffer;
        public int TenBit, GopLength, Preset, Lookahead, BFrames;
        public int SpatialAq, TemporalAq, AqStrength, Multipass, BufferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Applied
    {
        public int BFrames, Lookahead, TemporalAq, SpatialAq, Multipass, BufferCount, BFrameRef, TenBit;
    }

    [LibraryImport(Dll, EntryPoint = "aura_nvenc_create")]
    private static unsafe partial int Create(IntPtr device, in Config config, out Applied applied,
                                             out IntPtr session, byte* error, int errorCapacity);

    [LibraryImport(Dll, EntryPoint = "aura_nvenc_free_slots")]
    private static partial int FreeSlots(IntPtr session);

    [LibraryImport(Dll, EntryPoint = "aura_nvenc_encode")]
    private static partial int EncodeNative(IntPtr session, IntPtr texture, long pts, int forceIdr);

    [LibraryImport(Dll, EntryPoint = "aura_nvenc_end")]
    private static partial void End(IntPtr session);

    [LibraryImport(Dll, EntryPoint = "aura_nvenc_get")]
    private static partial int Get(IntPtr session, uint timeoutMs, out IntPtr data, out int size,
                                   out long pts, out int pictureType);

    [LibraryImport(Dll, EntryPoint = "aura_nvenc_sequence_header")]
    private static unsafe partial int SequenceHeaderNative(IntPtr session, byte* buffer, int capacity, out int size);

    [LibraryImport(Dll, EntryPoint = "aura_nvenc_destroy")]
    private static partial void Destroy(IntPtr session);

    [LibraryImport(Dll, EntryPoint = "aura_nvenc_trace")]
    private static unsafe partial int TraceNative(IntPtr session, byte* buffer, int capacity);

    /// <summary>NV_ENC_PIC_TYPE: P, B, I, IDR…</summary>
    public const int PictureIdr = 3;
    public const int PictureI = 2;
    /// <summary>Не тип NVENC: выход пропущен, данных нет.</summary>
    public const int SkippedPicture = -1;

    private IntPtr _session;
    private byte[] _output = new byte[1 << 20];

    public Applied Settings { get; }

    private NvencSession(IntPtr session, Applied applied)
    {
        _session = session;
        Settings = applied;
    }

    /// <summary>Есть ли смысл пробовать NVENC: видеокарта NVIDIA и прослойка на месте.</summary>
    /// <remarks>
    /// AURA_NO_NVENC=1 (или AURA_DIRECT_NVENC=0) — сразу MFT. Если прямой NVENC всё же
    /// встанет, сторож движка переключит программу на MFT до её перезапуска
    /// (<see cref="VideoEncoder.DirectNvencDisabled"/>).
    /// </remarks>
    public static bool Available(string adapterDescription) =>
        adapterDescription.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) &&
        Environment.GetEnvironmentVariable("AURA_DIRECT_NVENC") != "0" &&
        Environment.GetEnvironmentVariable("AURA_NO_NVENC") != "1" &&
        File.Exists(Path.Combine(AppContext.BaseDirectory, Dll));

    /// <summary>
    /// Настройки качества под разрешение и частоту. Чем больше пикселей в секунду,
    /// тем легче пресет: NVENC обязан успевать за 60+ кадрами, пока игра грузит
    /// видеокарту. Проверено на RTX 3070: 1440p60 на P5 с просмотром вперёд
    /// держит темп с запасом, 4K60 — на P4 без двух проходов.
    /// </summary>
    public static Config ConfigFor(VideoCodec codec, int width, int height, int fps, long bitrateBps, bool tenBit)
    {
        double pixelRate = (double)width * height * fps;           // пикселей в секунду
        bool heavy = pixelRate > 2560.0 * 1440 * 60 * 1.05;        // больше 1440p60
        bool veryHeavy = pixelRate > 3840.0 * 2160 * 60 * 1.05;    // больше 4K60

        return WithOverrides(new Config
        {
            Codec = codec switch { VideoCodec.HEVC => 1, VideoCodec.AV1 => 2, _ => 0 },
            Width = width,
            Height = height,
            Fps = fps,
            Bitrate = (int)Math.Min(bitrateBps, int.MaxValue),
            MaxBitrate = (int)Math.Min(bitrateBps * 2, int.MaxValue),
            VbvBuffer = (int)Math.Min(bitrateBps, int.MaxValue),     // секунда, как у ShadowPlay
            TenBit = tenBit ? 1 : 0,
            GopLength = fps * 2,
            Preset = veryHeavy ? 3 : heavy ? 4 : 5,
            // Просмотр вперёд пока выключен. Зависания, которые на него списывали,
            // оказались повторной блокировкой выхода в прослойке (см. aura_nvenc_get);
            // после её исправления просмотр вперёд 16 с B-кадрами прошёл 10 минут без
            // единого эпизода. Включим по умолчанию после проверки в настоящей игре;
            // до тех пор — AURA_NVENC_LOOKAHEAD. Временной AQ без него не работает.
            Lookahead = 0,
            // Без B-кадров и многопроходности: так NVIDIA советует кодировать в
            // реальном времени, и так меньше кадров живёт внутри энкодера. Включаются
            // переменными AURA_NVENC_BFRAMES / AURA_NVENC_MULTIPASS для проверок.
            BFrames = 0,
            SpatialAq = 1,
            TemporalAq = 0,
            AqStrength = 8,
            Multipass = 0,
            BufferCount = 0,                                         // прослойка посчитает сама
        });
    }

    /// <summary>Переопределения для проверочных прогонов: AURA_NVENC_LOOKAHEAD, _BFRAMES, _MULTIPASS.</summary>
    private static Config WithOverrides(Config c)
    {
        static int? Env(string name) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : null;
        if (Env("AURA_NVENC_LOOKAHEAD") is int lookahead) { c.Lookahead = lookahead; if (lookahead == 0) c.TemporalAq = 0; }
        if (Env("AURA_NVENC_BFRAMES") is int bFrames) c.BFrames = bFrames;
        if (Env("AURA_NVENC_MULTIPASS") is int multipass) c.Multipass = multipass;
        return c;
    }

    public static unsafe NvencSession? TryCreate(IntPtr device, Config config, out string error)
    {
        var buffer = new byte[512];
        int ok;
        IntPtr session;
        Applied applied;
        try
        {
            fixed (byte* p = buffer)
                ok = Create(device, config, out applied, out session, p, buffer.Length);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = $"прослойка NVENC не загрузилась: {ex.Message}";
            return null;
        }

        if (ok == 0)
        {
            error = System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Max(0, Array.IndexOf(buffer, (byte)0)));
            return null;
        }
        error = "";
        return new NvencSession(session, applied);
    }

    public int FreeSlotCount => _session == IntPtr.Zero ? 0 : FreeSlots(_session);

    /// <summary>Сколько кадров отправлено и ещё не забрано.</summary>
    public int PendingCount => _session == IntPtr.Zero ? 0 : Settings.BufferCount - FreeSlots(_session);

    /// <summary>Состояние сессии и последние вызовы NVENC (из трассировки прослойки).</summary>
    public unsafe string Trace()
    {
        var session = _session;
        if (session == IntPtr.Zero) return "сессия закрыта";
        var buffer = new byte[8192];
        int n;
        fixed (byte* p = buffer) n = TraceNative(session, p, buffer.Length);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Clamp(n, 0, buffer.Length)).TrimEnd();
    }

    /// <summary>1 — принят, 0 — нет свободного буфера, иначе исключение.</summary>
    /// <summary>
    /// Отправить кадр. 1 — принят (в том числе NV_ENC_ERR_NEED_MORE_INPUT: это не
    /// ошибка, выход придёт позже), 0 — нет свободного выходного буфера, иначе минус
    /// код NVENCSTATUS (-1000 — текстуру не удалось зарегистрировать).
    /// </summary>
    public int Encode(IntPtr texture, long pts, bool forceIdr) =>
        EncodeNative(_session, texture, pts, forceIdr ? 1 : 0);

    // NVENCSTATUS из nvEncodeAPI.h (порядковые значения перечисления)
    public const int StatusLockBusy = 13;
    public const int StatusNeedMoreInput = 17;
    public const int StatusEncoderBusy = 18;

    /// <summary>
    /// Временное состояние: этот кадр пропускаем, сессия цела. Всё остальное —
    /// неверный вызов, нехватка памяти, пропавшее устройство, общий сбой — считается
    /// поломкой сессии: пропускать кадры дальше бессмысленно, нужен MFT.
    /// </summary>
    public static bool IsTransient(int status) => status is StatusEncoderBusy or StatusLockBusy;

    public static string StatusName(int status) => status switch
    {
        0 => "SUCCESS", 1 => "NO_ENCODE_DEVICE", 2 => "UNSUPPORTED_DEVICE", 3 => "INVALID_ENCODERDEVICE",
        4 => "INVALID_DEVICE", 5 => "DEVICE_NOT_EXIST", 6 => "INVALID_PTR", 7 => "INVALID_EVENT",
        8 => "INVALID_PARAM", 9 => "INVALID_CALL", 10 => "OUT_OF_MEMORY", 11 => "ENCODER_NOT_INITIALIZED",
        12 => "UNSUPPORTED_PARAM", 13 => "LOCK_BUSY", 14 => "NOT_ENOUGH_BUFFER", 15 => "INVALID_VERSION",
        16 => "MAP_FAILED", 17 => "NEED_MORE_INPUT", 18 => "ENCODER_BUSY", 19 => "EVENT_NOT_REGISTERD",
        20 => "GENERIC", 21 => "INCOMPATIBLE_CLIENT_KEY", 22 => "UNIMPLEMENTED", 23 => "RESOURCE_REGISTER_FAILED",
        24 => "RESOURCE_NOT_REGISTERED", 25 => "RESOURCE_NOT_MAPPED", 26 => "NEED_MORE_OUTPUT",
        1000 => "регистрация текстуры не удалась",
        _ => status.ToString()
    };

    /// <summary>
    /// Последний пропущенный выход был поломкой сессии, а не временным состоянием
    /// (см. <see cref="IsTransient"/>). Читается сразу после TryGet в том же потоке.
    /// </summary>
    public bool LastSkipFatal { get; private set; }

    /// <summary>Причина последнего пропуска — для лога.</summary>
    public string LastSkipReason { get; private set; } = "";

    public void EndOfStream()
    {
        if (_session != IntPtr.Zero) End(_session);
    }

    /// <summary>
    /// Следующий выход. null — ещё не готов (или отправленных кадров нет). Данные
    /// действительны до следующего вызова. Прослойка блокирует каждый выход ровно
    /// один раз и копирует его в свой буфер: повторная блокировка того же выхода
    /// вешала драйвер на крупных ключевых кадрах (см. aura_nvenc_get).
    /// </summary>
    public (ArraySegment<byte> Data, long Pts, int PictureType)? TryGet(uint timeoutMs)
    {
        int result = Get(_session, timeoutMs, out IntPtr data, out int size, out long pts, out int type);
        if (result == 1)
        {
            if (_output.Length < size) _output = new byte[Math.Max(size, _output.Length * 2)];
            Marshal.Copy(data, _output, 0, size);
            return (new ArraySegment<byte>(_output, 0, size), pts, type);
        }
        if (result == 0 || result == -2) return null;
        // Выход пропущен (нет памяти под кадр или ошибка блокировки). Прослойка уже
        // сняла отображение входа и перешла к следующему; вызывающему нужно снять
        // метку времени этого кадра и решить, жива ли сессия.
        if (result == -1)
        {
            LastSkipFatal = true;
            LastSkipReason = "кадр не поместился в память";
        }
        else
        {
            int status = -(result + 3);
            LastSkipFatal = !IsTransient(status);
            LastSkipReason = $"nvEncLockBitstream → {StatusName(status)}";
        }
        return (default, pts, SkippedPicture);
    }

    public unsafe byte[]? SequenceHeader()
    {
        var buffer = new byte[4096];
        fixed (byte* p = buffer)
            if (SequenceHeaderNative(_session, p, buffer.Length, out int size) == 1 && size > 0)
                return buffer[..size];
        return null;
    }

    public void Dispose()
    {
        var session = Interlocked.Exchange(ref _session, IntPtr.Zero);
        if (session != IntPtr.Zero) Destroy(session);
    }

    public string Describe() =>
        $"B-кадров {Settings.BFrames}{(Settings.BFrameRef == 1 ? " (опорные)" : "")}, " +
        $"просмотр вперёд {Settings.Lookahead}, AQ {(Settings.SpatialAq == 1 ? "пространственный" : "нет")}" +
        $"{(Settings.TemporalAq == 1 ? " + временной" : "")}, " +
        $"проходов {(Settings.Multipass == 0 ? "1" : Settings.Multipass == 1 ? "2 (¼ разрешения)" : "2")}, " +
        $"буферов {Settings.BufferCount}{(Settings.TenBit == 1 ? ", 10 бит" : "")}";
}
