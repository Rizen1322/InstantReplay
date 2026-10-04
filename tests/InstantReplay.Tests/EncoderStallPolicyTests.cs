using Aura.Core.Engine;
using Xunit;

namespace InstantReplay.Tests;

public sealed class EncoderStallPolicyTests
{
    [Fact]
    public void Sudden_silence_is_a_deadlock_after_four_seconds()
    {
        Assert.Equal(4, EncoderStallPolicy.ThresholdSeconds(0, starving: false));
    }

    [Fact]
    public void Starved_encoder_gets_more_time_and_repeat_needs_full_silence()
    {
        Assert.Equal(15, EncoderStallPolicy.ThresholdSeconds(0, starving: true));
        Assert.Equal(20, EncoderStallPolicy.ThresholdSeconds(1, starving: true));
        Assert.Equal(20, EncoderStallPolicy.ThresholdSeconds(1, starving: false));
    }

    [Fact]
    public void Busy_gpu_during_silence_is_starvation()
    {
        Assert.True(EncoderStallPolicy.IsStarved(0, allGraphicsPercent: 95));
        Assert.True(EncoderStallPolicy.IsStarved(3, -1));
        // Одна медленная секунда бывает и в начале настоящего зависания
        Assert.False(EncoderStallPolicy.IsStarved(1, -1));
        Assert.False(EncoderStallPolicy.IsStarved(0, 30));
    }

    [Fact]
    public void Slow_output_means_starvation_but_zero_does_not()
    {
        // Как у CS2 под HAGS: 6–15 кадров в секунду вместо 60
        Assert.True(EncoderStallPolicy.IsSlow(10, 1.0, 60));
        Assert.False(EncoderStallPolicy.IsSlow(58, 1.0, 60));
        Assert.False(EncoderStallPolicy.IsSlow(0, 1.0, 60));
        Assert.False(EncoderStallPolicy.IsSlow(10, 0.3, 60));
    }
}
