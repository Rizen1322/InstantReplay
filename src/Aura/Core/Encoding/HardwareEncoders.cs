using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using Aura.Core.Logging;
using Aura.Core.Settings;

namespace Aura.Core.Encoding;

/// <summary>
/// Поиск аппаратного энкодера среди MFT, зарегистрированных в системе.
///
/// MFTEnumEx с флагом Hardware сам находит вендорский энкодер: на NVIDIA это NVENC,
/// на AMD — AMF/VCN, на Intel — QuickSync. Поэтому «запись через видеокарту» работает
/// на любом GPU без отдельных SDK.
///
/// Живёт отдельно от <see cref="VideoEncoder"/> намеренно: здесь нет ни состояния, ни
/// потоков, а спрашивают отсюда не только конвейер, но и интерфейс — «Захват» рисует
/// список доступных кодеков, «Приложение» при старте уводит с кодека, который система
/// не сможет упаковать.
/// </summary>
public static class HardwareEncoders
{
    private const uint MftEnumFlagHardware = 0x00000004;      // MFT_ENUM_FLAG_HARDWARE
    private const uint MftEnumFlagSortAndFilter = 0x00000040; // MFT_ENUM_FLAG_SORTANDFILTER

    /// <summary>
    /// MFVideoFormat_AV1 = {31305641-0000-0010-8000-00AA00389B71}.
    /// Data1 — это FourCC 'AV01' как DWORD little-endian: 'A'|'V'&lt;&lt;8|'0'&lt;&lt;16|'1'&lt;&lt;24.
    /// (Раньше здесь было 0x41313041 — то есть "A01A"; такого подтипа не существует,
    /// поэтому энкодер AV1 не находился даже на RTX 40, где он есть.)
    /// </summary>
    private static readonly Guid VideoFormatAV1 = new("31305641-0000-0010-8000-00AA00389B71");

    public static Guid SubtypeFor(VideoCodec codec) => codec switch
    {
        VideoCodec.HEVC => VideoFormatGuids.Hevc,
        VideoCodec.AV1  => VideoFormatAV1,
        _               => VideoFormatGuids.H264
    };

    /// <summary>
    /// Умеет ли Windows положить этот кодек в MP4.
    ///
    /// Кодировать AV1 видеокарта может и на Windows 10 (RTX 40+ это умеет), но
    /// MP4-мультиплексор системы про AV1 не знает: SinkWriter падает с
    /// MF_E_SINK_HEADERS_NOT_FOUND уже на финализации файла — то есть запись идёт,
    /// а сохранить её невозможно. Поддержка появилась только в Windows 11.
    /// </summary>
    public static bool CanSaveToMp4(VideoCodec codec) =>
        codec != VideoCodec.AV1 || Environment.OSVersion.Version.Build >= 22000;

    public static string VendorOf(string mftName) =>
        mftName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
        : mftName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? "AMD"
        : mftName.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
          mftName.Contains("Quick", StringComparison.OrdinalIgnoreCase) ? "Intel"
        : "GPU";

    public static string VendorTag(string mftName) => VendorOf(mftName) switch
    {
        "NVIDIA" => "nvenc", "AMD" => "amf", "Intel" => "qsv", _ => "hw"
    };

    /// <summary>
    /// Какие кодеки реально доступны аппаратно (по энумерации MFT) и чей энкодер
    /// будет выбран. Для инфопанели UI, конвейер не трогает.
    /// </summary>
    public static (string Vendor, List<VideoCodec> Codecs) ProbeSupport()
    {
        var codecs = new List<VideoCodec>();
        var names = new List<string>();
        foreach (VideoCodec codec in Enum.GetValues<VideoCodec>())
        {
            var activates = Enumerate(SubtypeFor(codec));
            try
            {
                if (activates.Length == 0) continue;
                codecs.Add(codec);
                foreach (var a in activates)
                    try { names.Add(a.GetString(TransformAttributeKeys.MftFriendlyNameAttribute)); } catch { }
            }
            finally { foreach (var a in activates) a.Dispose(); }
        }
        // Предпочтение как в реальном выборе: NVIDIA > Intel > AMD (у AMD активация чаще ломается)
        string vendor =
            names.Any(n => VendorOf(n) == "NVIDIA") ? "NVIDIA"
            : names.Any(n => VendorOf(n) == "Intel") ? "Intel"
            : names.Any(n => VendorOf(n) == "AMD") ? "AMD"
            : "—";
        return (vendor, codecs);
    }

    /// <summary>
    /// MFT_ENUM_ADAPTER_LUID: отбор MFT, принадлежащих конкретной видеокарте.
    /// Значение проверено на живой системе с RTX 3070 и встроенной AMD: с LUID
    /// NVIDIA MFTEnum2 отдаёт только «NVIDIA HEVC Encoder MFT», с LUID AMD — только
    /// «AMDh265Encoder».
    /// </summary>
    private static readonly Guid MftEnumAdapterLuid = new("1d39518c-e220-4da8-a07f-ba172552d6b1");

    /// <summary>
    /// Поиск аппаратного MFT-энкодера для нужного выходного subtype.
    ///
    /// <paramref name="adapterLuid"/> — видеокарта, на которой живёт устройство
    /// захвата. ЗАЧЕМ. На ноутбуке с двумя видеокартами MFTEnumEx отдаёт энкодеры
    /// ВСЕХ адаптеров вперемешку, а мы брали первый, который активировался. Если
    /// монитор подключён к встроенной графике, устройство захвата создаётся на ней,
    /// а энкодер мог оказаться от дискретной: кадр-текстура с одного адаптера
    /// подаётся в MFT другого, и это либо ошибка ProcessInput, либо скрытое
    /// копирование через системную память на каждый кадр. Энкодер обязан быть с
    /// той же видеокарты, что и кадры.
    /// </summary>
    public static (IMFTransform Transform, string Name)? Find(Guid outputSubtype, long? adapterLuid = null)
    {
        if (adapterLuid is long luid)
        {
            IMFActivate[] onAdapter;
            try { onAdapter = Enumerate(outputSubtype, luid); }
            catch (Exception ex)
            {
                Log.Info("Encoder", $"Отбор энкодеров по видеокарте недоступен ({ex.Message}) — беру общий список");
                onAdapter = [];
            }
            if (onAdapter.Length > 0)
            {
                var found = Activate(onAdapter);
                if (found is not null) return found;
            }
            else foreach (var a in onAdapter) a.Dispose();

            Log.Warn("Encoder", "На видеокарте захвата нет подходящего аппаратного энкодера — " +
                                "беру энкодер другой видеокарты, кадры пойдут между адаптерами");
        }

        return Activate(Enumerate(outputSubtype));
    }

    private static (IMFTransform Transform, string Name)? Activate(IMFActivate[] activates)
    {
        try
        {
            // На гибридных системах (iGPU + dGPU) активация MFT одного из вендоров
            // может падать (например, AMD AMF с E_OUTOFMEMORY) — пробуем всех по очереди.
            foreach (var activate in activates)
            {
                string name = "?";
                try { name = activate.GetString(TransformAttributeKeys.MftFriendlyNameAttribute); } catch { }
                try
                {
                    var transform = activate.ActivateObject<IMFTransform>();
                    Log.Info("Encoder", $"Выбран MFT: {name}");
                    return (transform, name);
                }
                catch (Exception ex)
                {
                    // Информация, а не предупреждение. Здесь перебираются ВСЕ
                    // кодировщики, о которых знает система, и чужие на этой машине
                    // не активируются по определению: на видеокарте NVIDIA каждый
                    // запуск честно отвечал E_OUTOFMEMORY на MFT от AMD. Настоящий
                    // отказ виден по отсутствию строки «Выбран MFT» следом.
                    Log.Info("Encoder",
                             $"MFT '{name}' не подошёл: {ex.Message.ReplaceLineEndings(" ").Trim()}");
                }
            }
            return null;
        }
        finally
        {
            foreach (var a in activates) a.Dispose();
        }
    }

    private static IMFActivate[] Enumerate(Guid outputSubtype, long? adapterLuid = null)
    {
        var outInfo = new RegisterTypeInfo { GuidMajorType = MediaTypeGuids.Video, GuidSubtype = outputSubtype };
        IntPtr pActivates;
        uint count;
        if (adapterLuid is long luid)
        {
            using var attrs = MediaFactory.MFCreateAttributes(1);
            attrs.SetBlob(MftEnumAdapterLuid, BitConverter.GetBytes(luid));
            MediaFactory.MFTEnum2(
                TransformCategoryGuids.VideoEncoder,
                MftEnumFlagHardware | MftEnumFlagSortAndFilter,
                null, outInfo, attrs, out pActivates, out count);
        }
        else
        {
            MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoEncoder,
                MftEnumFlagHardware | MftEnumFlagSortAndFilter,
                null, outInfo, out pActivates, out count);
        }

        var activates = new IMFActivate[count];
        for (int i = 0; i < count; i++)
            activates[i] = new IMFActivate(Marshal.ReadIntPtr(pActivates, i * IntPtr.Size));
        Marshal.FreeCoTaskMem(pActivates);
        return activates;
    }
}
