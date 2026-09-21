using Vortice.DXGI;
using Aura.Core.Logging;

namespace Aura.Core.Capture;

/// <summary>
/// В каком цветовом пространстве работает монитор прямо сейчас.
///
/// ЗАЧЕМ. Весь тракт захвата у нас восьмибитный: и Desktop Duplication, и WGC
/// запрашивают B8G8R8A8_UNorm. Пока рабочий стол в обычном SDR, это ровно то, что
/// нужно. Но если в Windows включён HDR, рабочий стол композитится в
/// R16G16B16A16_Float с кривой PQ, и система отдаёт нам его СВЁРНУТЫМ в восемь бит
/// без тональной компрессии. Запись выходит выцветшей и тёмной, причём одинаково на
/// любых настройках битрейта и кодека — угадать причину по виду невозможно.
///
/// Полноценный HDR-тракт (захват в FP16, тонмаппинг либо запись HDR10) — отдельная
/// работа. До неё единственное честное поведение: заметить HDR и сказать об этом
/// прямо, чтобы человек не искал причину в настройках качества.
/// </summary>
internal static class MonitorColorSpace
{
    /// <summary>Что показывает монитор и во что это превратится в записи.</summary>
    internal readonly record struct State(bool Hdr, string Description);

    /// <summary>
    /// Состояние монитора по его индексу в перечислении DXGI — том же, в котором
    /// монитор выбирается в настройках (см. <see cref="GpuInfo.ForMonitor"/>).
    /// </summary>
    public static State? Detect(int monitorIndex)
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            int index = 0;
            for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1 adapter).Success; a++)
                using (adapter)
                    for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput output).Success; o++)
                        using (output)
                        {
                            if (index++ != monitorIndex) continue;

                            // Описание с цветовым пространством живёт на IDXGIOutput6
                            // (Windows 10 1703). На более старых сборках HDR рабочего
                            // стола нет вовсе, поэтому отсутствие интерфейса — это SDR.
                            using IDXGIOutput6 output6 = output.QueryInterface<IDXGIOutput6>();
                            var description = output6.Description1;
                            bool hdr = IsHdr(description.ColorSpace);
                            return new State(
                                hdr,
                                $"{description.ColorSpace}, {description.BitsPerColor} бит на канал, " +
                                $"яркость до {description.MaxLuminance:F0} кд/м²");
                        }
        }
        catch (Exception ex)
        {
            Log.Info("Capture", $"Цветовое пространство монитора #{monitorIndex} не читается: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// HDR — это пространства с кривой PQ (G2084) на первичных цветах BT.2020.
    /// Расширенный sRGB с линейной кривой (scRGB, G10) сюда же: рабочий стол в нём
    /// тоже выходит за пределы SDR, и свёртка в восемь бит так же ломает картинку.
    /// </summary>
    private static bool IsHdr(ColorSpaceType colorSpace) => colorSpace
        is ColorSpaceType.RgbFullG2084NoneP2020
        or ColorSpaceType.RgbStudioG2084NoneP2020
        or ColorSpaceType.YcbcrStudioG2084LeftP2020
        or ColorSpaceType.YcbcrStudioG2084TopLeftP2020
        or ColorSpaceType.RgbFullG10NoneP709;
}
