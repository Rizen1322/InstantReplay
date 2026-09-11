using Aura.Core.Engine;
using Xunit;

namespace InstantReplay.Tests;

public sealed class PipelineRestartPolicyTests
{
    [Fact]
    public void CaptureRestartKeepsCompatibleRamBuffersAndNeverSavesReplay()
    {
        var actions = PipelineRestartPolicy.For(
            PipelineStopIntent.CaptureRestart,
            continuousRecordingActive: false,
            formatCompatible: true);

        Assert.True(actions.KeepReplayBuffer);
        Assert.True(actions.KeepAudioBuffer);
        Assert.False(actions.SaveReplay);
        Assert.False(actions.ResumeContinuousRecording);
    }

    [Fact]
    public void CaptureRestartResumesOnlyExplicitContinuousRecording()
    {
        var actions = PipelineRestartPolicy.For(
            PipelineStopIntent.CaptureRestart,
            continuousRecordingActive: true,
            formatCompatible: true);

        Assert.True(actions.ResumeContinuousRecording);
        Assert.False(actions.SaveReplay);
    }

    [Fact]
    public void IncompatibleVideoFormatClearsRamVideoWithoutSavingIt()
    {
        var actions = PipelineRestartPolicy.For(
            PipelineStopIntent.CaptureRestart,
            continuousRecordingActive: false,
            formatCompatible: false);

        Assert.False(actions.KeepReplayBuffer);
        Assert.True(actions.KeepAudioBuffer);
        Assert.False(actions.SaveReplay);
    }

    [Fact]
    public void UserStopReleasesRamWithoutSavingReplay()
    {
        var actions = PipelineRestartPolicy.For(
            PipelineStopIntent.UserStop,
            continuousRecordingActive: false,
            formatCompatible: true);

        Assert.False(actions.KeepReplayBuffer);
        Assert.False(actions.KeepAudioBuffer);
        Assert.False(actions.SaveReplay);
    }
}
