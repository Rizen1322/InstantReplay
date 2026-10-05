using Aura.Core.Settings;

namespace Aura.Core.Encoding;

/// <summary>
/// Как NVENC тратит битрейт: «Качество» (постоянное качество с потолком) или
/// «Экономия» (CBR).
///
/// ПОЧЕМУ НЕ ОБЫЧНЫЙ VBR. Его средний NVENC считает по всей сессии, и минуты
/// статичного рабочего стола копят запас, который потом тратится на потолке. На
/// одних и тех же кадрах CS2 при 30 Мбит/с: 30,8 без простоя и 60,1 после двух минут
/// рабочего стола. Размер клипа и память повтора зависели от того, сколько до игры
/// был открыт рабочий стол. Замеры — стендом src/native/Aura.Media/rc_probe.c.
///
/// КАЧЕСТВО (по умолчанию). VBR с целевым качеством (CQ) и потолком в два заданных
/// битрейта: накопления нет, простые сцены почти бесплатны, сложным разрешено до
/// потолка. На трёхминутном клипе CS2 1440p60 CQ 25 дал 25,4 Мбит/с в среднем и
/// картинку чуть лучше CBR на 27. Потолок 2× рассчитан на запас кольца повтора
/// (ReplayVideoBuffer.PeakFactor): арена выделяется сразу под потолок, плюс слоты
/// на GOP и сохранение, и дальше не растёт. Длину повтора это держит, пока арена
/// не упёрлась в общий предел памяти (тогда буфер короче, и это видно в настройках).
///
/// В режиме качества заданный битрейт — ориентир для уровня CQ и потолок, а НЕ
/// обещанный средний. Уровень — стартовая оценка, а не калибровка для любого ПК:
/// битрейт NVENC при CQ падает примерно в e^k раз на единицу уровня и при одном
/// уровне пропорционален числу пикселей в секунду. Константы ниже сняты на одном
/// бою CS2 на RTX 3070; трава, дым, шум и другие поколения NVENC дадут и меньше,
/// и больше. Возможности карты (B-кадры, второй проход, пресет) от уровня
/// отделены: их выбирает ConfigFor по возможностям кодировщика, а при нехватке
/// темпа снимает NvencLoadAdapter, не трогая уровень CQ.
///
/// ЭКОНОМИЯ. CBR: ровно заданный битрейт в игре; на статичном экране NVENC не
/// добивает поток до него. Для AV1 калибровки нет (на RTX 3070 его кодировщика нет),
/// поэтому AV1 всегда пишется в CBR.
/// </summary>
internal static class RateControlPolicy
{
    public readonly record struct Choice(bool ConstantQuality, double TargetQuality, long MaxBitrate, long VbvBuffer)
    {
        public override string ToString() => ConstantQuality
            ? $"качество (CQ {TargetQuality:F1}, потолок {MaxBitrate / 1_000_000} Мбит/с)"
            : $"CBR {MaxBitrate / 1_000_000} Мбит/с";
    }

    /// <summary>Калибровка: битов на пиксель при CQ 25 и наклон k (на единицу уровня).</summary>
    private readonly record struct Calibration(double BitsPerPixelAt25, double Slope);

    // Замеры (бой CS2, 60 с, одни и те же кадры), средний битрейт при CQ 22/25/28:
    //   HEVC 1440p60 без B-кадров 47,8/31,7/21,5 (8 и 10 бит одинаково);
    //   HEVC 1080p60 с B-кадрами и вторым проходом 22,5/15,2/10,3;
    //   H.264 1440p60 без B-кадров 48,9/33,0/22,2; H.264 1080p60 с B-кадрами 24,5/16,6/11,4.
    // HEVC без B-кадров и второго прохода (так Aura пишет 1440p60 и выше)
    private static readonly Calibration Hevc = new(0.1433, 0.133);
    // HEVC с двумя B-кадрами и вторым проходом в ¼ разрешения (ниже 1440p60)
    private static readonly Calibration HevcExtras = new(0.1222, 0.131);
    private static readonly Calibration H264 = new(0.1492, 0.132);
    private static readonly Calibration H264Extras = new(0.1334, 0.128);

    public const double MinQuality = 15, MaxQuality = 40;

    public static Choice For(BitrateMode mode, VideoCodec codec, int width, int height, int fps,
                             long bitrateBps, bool extras)
    {
        if (mode == BitrateMode.Economy || codec == VideoCodec.AV1)
            return new Choice(false, 0, bitrateBps, bitrateBps);

        var calibration = codec == VideoCodec.H264
            ? (extras ? H264Extras : H264)
            : (extras ? HevcExtras : Hevc);
        double bitsPerPixel = bitrateBps / Math.Max(1.0, (double)width * height * fps);
        double level = 25 + Math.Log(calibration.BitsPerPixelAt25 / bitsPerPixel) / calibration.Slope;
        return new Choice(true, Math.Round(Math.Clamp(level, MinQuality, MaxQuality), 2),
                          bitrateBps * 2, bitrateBps * 2);
    }
}
