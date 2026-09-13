using Aura.Core.Settings;

namespace Aura.Core.Encoding;

internal enum RecordingQualityTier { Light, Normal, High, Maximum }

/// <summary>
/// Recommended video bitrate for one-click quality choices. The table is based on
/// HEVC at 60 fps; frame-rate and codec multipliers preserve comparable visual
/// quality without wasting replay-buffer memory.
/// </summary>
internal static class RecordingQualityPolicy
{
    public const int MinimumMbps = 4;
    public const int MaximumMbps = 80;

    public static int BitrateMbps(
        RecordingQualityTier tier,
        int height,
        int fps,
        VideoCodec codec)
    {
        int baseAt60 = height switch
        {
            <= 720  => tier switch { RecordingQualityTier.Light => 4,  RecordingQualityTier.Normal => 7,  RecordingQualityTier.High => 11, _ => 16 },
            <= 1080 => tier switch { RecordingQualityTier.Light => 8,  RecordingQualityTier.Normal => 12, RecordingQualityTier.High => 18, _ => 26 },
            <= 1440 => tier switch { RecordingQualityTier.Light => 16, RecordingQualityTier.Normal => 25, RecordingQualityTier.High => 36, _ => 50 },
            _       => tier switch { RecordingQualityTier.Light => 30, RecordingQualityTier.Normal => 48, RecordingQualityTier.High => 65, _ => 80 }
        };

        double byFps = fps switch { <= 30 => 0.7, <= 60 => 1.0, <= 120 => 1.35, _ => 1.5 };
        double byCodec = codec switch { VideoCodec.H264 => 1.4, VideoCodec.AV1 => 0.8, _ => 1.0 };

        return Math.Clamp(
            (int)Math.Round(baseAt60 * byFps * byCodec),
            MinimumMbps,
            MaximumMbps);
    }
}
