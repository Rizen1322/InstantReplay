using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using Aura.Core.Logging;

namespace Aura.Core.Encoding;

/// <summary>
/// Настройка энкодера через ICodecAPI: битрейт, GOP, режим низкой задержки,
/// ключевой кадр по требованию, пресет качество/скорость.
///
/// ДВА ПУТИ ВЫЗОВА, и это не дублирование:
///
/// • <see cref="Set"/> — через обёртку .NET. Годится только для потока, на котором
///   объект создан, зато принимает любой тип значения.
/// • <see cref="SetDirect"/> — прямо по таблице методов, без обёртки, поэтому
///   работает с ЛЮБОГО потока. Нужен для параметров на лету (ключевой кадр, пресет):
///   их запрашивают рабочие потоки энкодера, а обёртка .NET возвращается по
///   идентичности объекта и с чужого потока отвечает E_NOINTERFACE. В логах это
///   годами висело как «Ключевой кадр по требованию недоступен» — то есть GOP в две
///   секунды, на который опирается нарезка буфера, по факту не запрашивался.
/// </summary>
internal sealed class CodecApi
{
    /// <summary>Своя ссылка на интерфейс: живёт столько же, сколько энкодер.</summary>
    private IntPtr _ptr;

    /// <summary>Обёртка .NET поверх той же ссылки — только для потока создания.</summary>
    private readonly ICodecAPI _managed;

    private CodecApi(IntPtr ptr, ICodecAPI managed)
    {
        _ptr = ptr;
        _managed = managed;
    }

    /// <summary>Запросить интерфейс у трансформа; null — энкодер его не отдаёт.</summary>
    public static CodecApi? For(IMFTransform transform)
    {
        try
        {
            Guid iid = typeof(ICodecAPI).GUID;
            if (Marshal.QueryInterface(transform.NativePointer, in iid, out IntPtr ptr) < 0 || ptr == IntPtr.Zero)
                return null;
            return new CodecApi(ptr, (ICodecAPI)Marshal.GetObjectForIUnknown(ptr));
        }
        catch (Exception ex)
        {
            Log.Warn("Encoder", $"ICodecAPI недоступен: {ex.Message}");
            return null;
        }
    }

    public bool IsSupported(Guid api)
    {
        try { return _managed.IsSupported(ref api) == 0; }
        catch { return false; }
    }

    /// <summary>
    /// Задать значение через обёртку .NET.
    ///
    /// <paramref name="optional"/> — ключ, который часть энкодеров не поддерживает
    /// штатно (и отвечает E_INVALIDARG). Такие пишем в лог как INF: два вечных WRN
    /// в каждом запуске мешают искать в нём настоящие проблемы.
    /// </summary>
    public void Set(Guid api, object value, bool optional = false)
    {
        try { _managed.SetValue(ref api, ref value); }
        catch (Exception ex)
        {
            if (optional) Log.Info("Encoder", $"ICodecAPI {api} не поддерживается энкодером");
            else Log.Warn("Encoder", $"ICodecAPI {api}: {ex.Message}");
        }
    }

    /// <summary>
    /// Вызов ICodecAPI::SetValue напрямую через таблицу методов, без обёртки .NET —
    /// поэтому работает с любого потока. Девятый слот таблицы: три метода IUnknown
    /// плюс шесть объявленных выше SetValue (см. интерфейс <see cref="ICodecAPI"/>).
    /// </summary>
    public unsafe bool SetDirect(Guid api, uint value, out string error)
    {
        error = "";
        IntPtr ptr = Volatile.Read(ref _ptr);
        if (ptr == IntPtr.Zero) { error = "интерфейс недоступен"; return false; }

        try
        {
            // VARIANT: тип в первых двух байтах, значение с восьмого (x64)
            byte* variant = stackalloc byte[24];
            new Span<byte>(variant, 24).Clear();
            *(ushort*)variant = 19;              // VT_UI4
            *(uint*)(variant + 8) = value;

            var vtable = *(void***)ptr;
            var setValue = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, void*, int>)vtable[9];
            int hr = setValue(ptr, &api, variant);
            if (hr >= 0) return true;

            error = $"HRESULT 0x{hr:X8}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Прочитать настройки битрейта ОБРАТНО из энкодера.
    ///
    /// SetValue может вернуть успех и при этом ничего не изменить (так у NVIDIA
    /// проходит неподдерживаемый ключ), а режим управления битрейтом — как раз то
    /// место, где мы уже ошиблись: там годами стоял режим Quality под комментарием
    /// «CBR», и заданный битрейт игнорировался. Теперь в логе видно фактическое
    /// состояние, а не наши намерения.
    /// </summary>
    public void LogRateControl()
    {
        try
        {
            string mode = Read(CodecApiGuids.AVEncCommonRateControlMode) is uint m
                ? m switch
                {
                    0 => "CBR", 1 => "VBR с потолком", 2 => "VBR без ограничений",
                    3 => "по качеству (битрейт игнорируется!)", 4 => "VBR низкой задержки",
                    _ => $"режим {m}"
                }
                : "не читается";
            string mean = Read(CodecApiGuids.AVEncCommonMeanBitRate) is uint b
                ? $"{b / 1_000_000.0:F0} Мбит/с" : "не читается";
            string buffer = Read(CodecApiGuids.AVEncCommonBufferSize) is uint bs
                ? $"{bs / 1_000_000.0:F0} Мбит" : "по умолчанию";
            Log.Info("Encoder", $"Битрейт по факту: {mode}, {mean}, буфер {buffer}");
        }
        catch (Exception ex)
        {
            Log.Info("Encoder", $"Настройки битрейта не читаются обратно: {ex.Message}");
        }
    }

    /// <summary>
    /// Что из ICodecAPI этот энкодер вообще умеет. Пишем один раз при старте:
    /// набор у NVIDIA, AMD и Intel разный, и без этой строки любая настройка —
    /// гадание (так мы уже потеряли время на B-кадрах, которых у NVENC нет).
    /// </summary>
    public void LogSupport()
    {
        var yes = new List<string>();
        var no = new List<string>();
        foreach (var (name, guid) in CodecApiGuids.Probe)
            (IsSupported(guid) ? yes : no).Add(name);

        Log.Info("Encoder", $"ICodecAPI поддерживает: {string.Join(", ", yes)}");
        if (no.Count > 0) Log.Info("Encoder", $"ICodecAPI НЕ поддерживает: {string.Join(", ", no)}");
    }

    private object? Read(Guid guid)
    {
        try { _managed.GetValue(ref guid, out object value); return value; }
        catch { return null; }
    }

    /// <summary>Отпустить ссылку. Звать только когда трансформ уже никем не используется.</summary>
    public void Release()
    {
        IntPtr ptr = Interlocked.Exchange(ref _ptr, IntPtr.Zero);
        if (ptr != IntPtr.Zero) Marshal.Release(ptr);
    }

    /// <summary>
    /// Забыть ссылку, НЕ освобождая её.
    ///
    /// Нужно, когда поток событий не завершился и продолжает пользоваться трансформом:
    /// освободить интерфейс под ним — это краш при выключении. Утечка одной ссылки
    /// безопаснее.
    /// </summary>
    public void Abandon() => Interlocked.Exchange(ref _ptr, IntPtr.Zero);
}

/// <summary>
/// Ручной COM-интероп ICodecAPI: в Vortice.MediaFoundation 3.x обёртки нет.
/// Порядок методов строго по vtable (strmif.h), объявлены только нужные + предшествующие.
/// </summary>
[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(ref Guid api);
    [PreserveSig] int IsModifiable(ref Guid api);
    [PreserveSig] int GetParameterRange(ref Guid api,
        [MarshalAs(UnmanagedType.Struct)] out object valueMin,
        [MarshalAs(UnmanagedType.Struct)] out object valueMax,
        [MarshalAs(UnmanagedType.Struct)] out object steppingDelta);
    [PreserveSig] int GetParameterValues(ref Guid api, out IntPtr values, out uint valuesCount);
    [PreserveSig] int GetDefaultValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
    void GetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
    void SetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
}

/// <summary>
/// GUID'ы ICodecAPI, которых может не быть в обёртке.
/// Значения сверены с codecapi.h из Windows SDK (10.0.19041.0).
/// </summary>
internal static class CodecApiGuids
{
    public static readonly Guid AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid AVEncCommonMeanBitRate     = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid AVEncCommonBufferSize      = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid AVEncMPVGOPSize            = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid AVEncCommonLowLatency      = new("9d3ecd55-89e8-490a-970a-0c9548d5a56e");
    public static readonly Guid AVLowLatencyMode           = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid AVEncCommonMaxBitRate      = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid AVEncCommonQuality         = new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");
    public static readonly Guid AVEncVideoEncodeQP         = new("2cb5696b-23fb-4ce1-a0f9-ef5b90fd55ca");
    public static readonly Guid AVEncVideoMinQP            = new("0ee22c6a-a37c-4568-b5f1-9d4c2b3ab886");
    public static readonly Guid AVEncVideoMaxQP            = new("3daf6f66-a6a7-45e0-a8e5-f2743f46a3a2");
    public static readonly Guid AVEncVideoForceKeyFrame    = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid AVEncNumWorkerThreads      = new("b0c8bf60-16f7-4951-a30b-1db1609293d6");
    public static readonly Guid AVEncAdaptiveMode          = new("4419b185-da1f-4f53-bc76-097d0c1efb1e");
    public static readonly Guid AVEncCommonQualityVsSpeed  = new("98332df8-03cd-476b-89fa-3f9e442dec9f");

    /// <summary>
    /// Человекочитаемые имена для лога поддержки ключей.
    /// ВНИМАНИЕ: объявление обязано идти ПОСЛЕ всех Guid-полей выше. Статические
    /// инициализаторы выполняются в порядке объявления, и поле, объявленное ниже,
    /// попадёт сюда как Guid.Empty — опрос тогда честно ответит «не поддерживается»
    /// про пустой GUID. Один раз уже наступили: так «пропала» поддержка QualityVsSpeed.
    /// </summary>
    public static readonly (string Name, Guid Guid)[] Probe =
    [
        ("RateControlMode", AVEncCommonRateControlMode),
        ("MeanBitRate", AVEncCommonMeanBitRate),
        ("MaxBitRate", AVEncCommonMaxBitRate),
        ("BufferSize", AVEncCommonBufferSize),
        ("Quality", AVEncCommonQuality),
        ("QualityVsSpeed", AVEncCommonQualityVsSpeed),
        ("LowLatency", AVEncCommonLowLatency),
        ("LowLatencyMode", AVLowLatencyMode),
        ("GOPSize", AVEncMPVGOPSize),
        ("BPictureCount", AVEncMPVDefaultBPictureCount),
        ("EncodeQP", AVEncVideoEncodeQP),
        ("MinQP", AVEncVideoMinQP),
        ("MaxQP", AVEncVideoMaxQP),
        ("ForceKeyFrame", AVEncVideoForceKeyFrame),
        ("WorkerThreads", AVEncNumWorkerThreads),
        ("AdaptiveMode", AVEncAdaptiveMode),
    ];
}
