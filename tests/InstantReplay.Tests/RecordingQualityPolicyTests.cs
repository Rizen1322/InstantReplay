using Aura.Core.Encoding;
using Aura.Core.Settings;
using Xunit;

namespace InstantReplay.Tests;

public sealed class RecordingQualityPolicyTests
{
    [Theory]
    [InlineData(1, 720, 30, VideoCodec.HEVC, 5)]
    [InlineData(1, 1080, 60, VideoCodec.HEVC, 12)]
    [InlineData(2, 1080, 60, VideoCodec.HEVC, 18)]
    [InlineData(1, 1440, 60, VideoCodec.HEVC, 25)]
    [InlineData(1, 1440, 60, VideoCodec.H264, 35)]
    [InlineData(2, 1440, 60, VideoCodec.H264, 50)]
    [InlineData(1, 1440, 60, VideoCodec.AV1, 20)]
    [InlineData(3, 2160, 120, VideoCodec.HEVC, 80)]
    [InlineData(0, 720, 30, VideoCodec.AV1, 4)]
    public void Returns_codec_and_frame_rate_aware_bitrate(
        int tier,
        int height,
        int fps,
        VideoCodec codec,
        int expectedMbps)
    {
        Assert.Equal(expectedMbps, RecordingQualityPolicy.BitrateMbps(
            (RecordingQualityTier)tier,
            height,
            fps,
            codec));
    }
}
