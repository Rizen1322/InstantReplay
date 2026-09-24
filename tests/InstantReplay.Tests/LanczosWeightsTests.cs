using Aura.Core.Capture;
using Xunit;

namespace InstantReplay.Tests;

public class LanczosWeightsTests
{
    [Theory]
    [InlineData(2560, 1920)]   // 1440p → 1080p
    [InlineData(3840, 1920)]   // 4K → 1080p
    [InlineData(1920, 1280)]   // 1080p → 720p
    [InlineData(1440, 1080)]
    public void Every_window_sums_to_one_and_covers_its_output_pixel(int source, int output)
    {
        var (first, weights, taps) = LanczosWeights.Compute(source, output, a: 2);
        double scale = (double)source / output;
        for (int o = 0; o < output; o++)
        {
            double sum = 0;
            for (int k = 0; k < taps; k++) sum += weights[o * taps + k];
            Assert.InRange(sum, 0.9999, 1.0001);

            // Центр выходного пикселя лежит внутри окна
            double center = (o + 0.5) * scale;
            Assert.InRange(center, first[o], first[o] + taps);
        }
    }

    [Fact]
    public void Window_widens_with_downscale_ratio()
    {
        var (_, _, tapsSmall) = LanczosWeights.Compute(2560, 1920, a: 2);
        var (_, _, tapsLarge) = LanczosWeights.Compute(3840, 1280, a: 2);
        Assert.True(tapsLarge > tapsSmall, "при сильном уменьшении окно обязано быть шире — иначе муар");
    }
}
