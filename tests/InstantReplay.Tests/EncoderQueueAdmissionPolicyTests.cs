using Aura.Core.Encoding;
using Xunit;

namespace InstantReplay.Tests;

public sealed class EncoderQueueAdmissionPolicyTests
{
    [Fact]
    public void Duplicate_appends_only_while_queue_has_low_pressure()
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
            EncoderQueueAdmission.RejectDuplicate,
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
    public void Encoder_behind_suppresses_duplicate_even_when_queue_is_empty()
    {
        Assert.Equal(
            EncoderQueueAdmission.RejectDuplicate,
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
}
