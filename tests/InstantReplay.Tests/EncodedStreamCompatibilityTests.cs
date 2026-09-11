using Aura.Core.Encoding;
using Xunit;

namespace InstantReplay.Tests;

public sealed class EncodedStreamCompatibilityTests
{
    [Fact]
    public void IdenticalSequenceHeadersAreCompatible()
    {
        byte[] header = [1, 100, 0, 31, 255];

        Assert.True(EncodedStreamCompatibility.SameSequenceHeader(header, [.. header]));
    }

    [Theory]
    [InlineData(null, new byte[] { 1 })]
    [InlineData(new byte[] { 1 }, null)]
    [InlineData(null, null)]
    public void MissingSequenceHeaderIsNotSafeToMix(byte[]? previous, byte[]? current) =>
        Assert.False(EncodedStreamCompatibility.SameSequenceHeader(previous, current));

    [Fact]
    public void DifferentSequenceHeadersAreNotCompatible() =>
        Assert.False(EncodedStreamCompatibility.SameSequenceHeader(
            [1, 100, 0, 31], [1, 100, 0, 40]));
}
