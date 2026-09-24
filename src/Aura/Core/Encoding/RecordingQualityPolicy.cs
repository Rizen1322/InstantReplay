using Aura.Core.Settings;

namespace Aura.Core.Encoding;

internal enum RecordingQualityTier { Light, Normal, High, Maximum }

/// <summary>
/// Рекомендованный битрейт для выбора качества в один клик. Таблица задана для HEVC
/// при 60 кадрах; поправки на частоту кадров и кодек держат сопоставимое качество.
///
/// ПОЧЕМУ ЦИФРЫ ВЫШЕ, ЧЕМ ДЛЯ СТРИМА. Рекомендации YouTube и Twitch (12 Мбит/с на
/// 1080p60) рассчитаны на ПЕРЕДАЧУ, где зритель смотрит сжатое ещё раз. Здесь клип
/// — исходник: его режут, пересжимают для Discord и Telegram, и каждое пересжатие
/// добавляет потерь к уже имеющимся. Вдобавок аппаратный MFT в режиме низкой
/// задержки работает без просмотра вперёд и без адаптивного квантования, то есть
/// тратит биты хуже, чем NVENC у ShadowPlay (там 1080p60 «Высокое» — 50 Мбит/с
/// H.264). Прежние 12 Мбит/с HEVC на 1080p60 давали заметные блоки на траве,
/// дыме и резком повороте камеры. Новые значения примерно вдвое ниже ShadowPlay
/// за счёт HEVC и VBR с потолком ×2 и при этом держат картинку на сложных сценах.
///
/// Память повтора при этом растёт умеренно: 1080p60 на 20 Мбит/с за три минуты —
/// около 560 МБ.
/// </summary>
internal static class RecordingQualityPolicy
{
    public const int MinimumMbps = 4;
    public const int MaximumMbps = 150;

    public static int BitrateMbps(
        RecordingQualityTier tier,
        int height,
        int fps,
        VideoCodec codec)
    {
        int baseAt60 = height switch
        {
            <= 720  => tier switch { RecordingQualityTier.Light => 5,  RecordingQualityTier.Normal => 8,  RecordingQualityTier.High => 12, _ => 18 },
            <= 1080 => tier switch { RecordingQualityTier.Light => 12, RecordingQualityTier.Normal => 20, RecordingQualityTier.High => 30, _ => 45 },
            <= 1440 => tier switch { RecordingQualityTier.Light => 20, RecordingQualityTier.Normal => 32, RecordingQualityTier.High => 45, _ => 65 },
            _       => tier switch { RecordingQualityTier.Light => 40, RecordingQualityTier.Normal => 60, RecordingQualityTier.High => 85, _ => 120 }
        };

        // Битрейт растёт медленнее частоты кадров: соседние кадры ближе друг к другу,
        // и предсказание между ними дешевле.
        double byFps = fps switch { <= 30 => 0.65, <= 60 => 1.0, <= 120 => 1.4, _ => 1.6 };
        // H.264 при том же качестве требует примерно в полтора раза больше, AV1 — меньше.
        double byCodec = codec switch { VideoCodec.H264 => 1.45, VideoCodec.AV1 => 0.8, _ => 1.0 };

        return Math.Clamp(
            (int)Math.Round(baseAt60 * byFps * byCodec),
            MinimumMbps,
            MaximumMbps);
    }
}
