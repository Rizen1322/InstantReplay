using Aura.Core.Encoding;
using Xunit;

namespace InstantReplay.Tests;

public sealed class EncoderQueueAdmissionPolicyTests
{
    [Fact]
    public void Duplicate_appends_while_queue_has_capacity()
    {
        Assert.Equal(
            EncoderQueueAdmission.Append,
            EncoderQueueAdmissionPolicy.Decide(
                incomingDuplicate: true,
                queueDepth: 3,
                maximumDepth: 33,
                encoderBehind: false,
                firstDuplicateIndex: -1));
        Assert.Equal(
            EncoderQueueAdmission.Append,
            EncoderQueueAdmissionPolicy.Decide(true, 8, 33, false, -1));
    }

    [Fact]
    public void Duplicate_never_evicts_from_a_full_queue()
    {
        Assert.Equal(
            EncoderQueueAdmission.RejectDuplicate,
            EncoderQueueAdmissionPolicy.Decide(true, 33, 33, false, 5));
    }

    [Fact]
    public void Encoder_behind_does_not_break_constant_frame_rate_when_queue_has_capacity()
    {
        Assert.Equal(
            EncoderQueueAdmission.Append,
            EncoderQueueAdmissionPolicy.Decide(true, 0, 33, true, -1));
    }

    [Fact]
    public void Real_frame_evicts_oldest_duplicate_before_any_real_frame()
    {
        Assert.Equal(
            EncoderQueueAdmission.EvictDuplicate,
            EncoderQueueAdmissionPolicy.Decide(false, 33, 33, false, 6));
    }

    [Fact]
    public void Full_all_real_queue_evicts_oldest_real_frame()
    {
        Assert.Equal(
            EncoderQueueAdmission.EvictOldestReal,
            EncoderQueueAdmissionPolicy.Decide(false, 33, 33, false, -1));
    }

    [Fact]
    public void Non_full_real_queue_appends_without_eviction()
    {
        Assert.Equal(
            EncoderQueueAdmission.Append,
            EncoderQueueAdmissionPolicy.Decide(false, 32, 33, false, 4));
    }

    [Theory]
    [InlineData(1, 150, false)]
    [InlineData(8, 1100, false)]
    [InlineData(1, 99, true)]
    [InlineData(0, 0, true)]
    public void Stale_queue_does_not_receive_more_gpu_copies_of_duplicate_frames(
        int depth, double ageMs, bool append)
    {
        Assert.Equal(append ? EncoderQueueAdmission.Append : EncoderQueueAdmission.RejectDuplicate,
            EncoderQueueAdmissionPolicy.Decide(true, depth, 20,
            false, -1, oldestAgeMs: ageMs));
    }

    [Fact]
    public void Real_frames_are_still_admitted_when_duplicate_generation_is_suspended()
    {
        Assert.Equal(EncoderQueueAdmission.Append,
            EncoderQueueAdmissionPolicy.Decide(false, 8, 20, true, -1, oldestAgeMs: 1100));
    }
}
