using Aura.Core.Encoding;
using Aura.Core.Settings;
using Xunit;

namespace InstantReplay.Tests;

public sealed class RateControlPolicyTests
{
    [Fact]
    public void Quality_on_hevc_1440p60_at_30_mbps_is_the_calibrated_cq_level()
    {
        // Калибровка: бой CS2 2560×1440@60, HEVC без B-кадров — CQ 25 даёт 31,7 Мбит/с
        var choice = RateControlPolicy.For(BitrateMode.Quality, VideoCodec.HEVC, 2560, 1440, 60, 30_000_000, extras: false);
        Assert.True(choice.ConstantQuality);
        Assert.InRange(choice.TargetQuality, 25.0, 26.0);
        Assert.Equal(60_000_000, choice.MaxBitrate);   // потолок два заданных: укладывается в запас кольца
    }

    [Fact]
    public void Double_bitrate_means_about_five_levels_better_quality()
    {
        var at30 = RateControlPolicy.For(BitrateMode.Quality, VideoCodec.HEVC, 2560, 1440, 60, 30_000_000, false);
        var at60 = RateControlPolicy.For(BitrateMode.Quality, VideoCodec.HEVC, 2560, 1440, 60, 60_000_000, false);
        Assert.InRange(at30.TargetQuality - at60.TargetQuality, 4.8, 5.6);
    }

    [Fact]
    public void Same_bits_per_pixel_gives_same_level_at_other_resolution()
    {
        var big = RateControlPolicy.For(BitrateMode.Quality, VideoCodec.HEVC, 2560, 1440, 60, 30_000_000, false);
        var small = RateControlPolicy.For(BitrateMode.Quality, VideoCodec.HEVC, 1920, 1080, 60, 16_875_000, false);
        Assert.Equal(big.TargetQuality, small.TargetQuality, 1);
    }

    [Fact]
    public void Economy_and_av1_are_cbr_at_the_set_bitrate()
    {
        var economy = RateControlPolicy.For(BitrateMode.Economy, VideoCodec.HEVC, 2560, 1440, 60, 30_000_000, false);
        Assert.False(economy.ConstantQuality);
        Assert.Equal(30_000_000, economy.MaxBitrate);

        // Для AV1 калибровки нет: всегда CBR
        Assert.False(RateControlPolicy.For(BitrateMode.Quality, VideoCodec.AV1, 2560, 1440, 60, 30_000_000, false).ConstantQuality);
    }

    [Fact]
    public void Level_is_clamped_for_extreme_bitrates()
    {
        Assert.Equal(RateControlPolicy.MinQuality,
            RateControlPolicy.For(BitrateMode.Quality, VideoCodec.HEVC, 1280, 720, 30, 150_000_000, true).TargetQuality);
        Assert.Equal(RateControlPolicy.MaxQuality,
            RateControlPolicy.For(BitrateMode.Quality, VideoCodec.HEVC, 3840, 2160, 144, 4_000_000, false).TargetQuality);
    }
}
