using Aura.Core.Engine;
using Xunit;

namespace InstantReplay.Tests;

public sealed class PipelineRestartPolicyTests
{
    [Theory]
    [InlineData((int)PipelineStopIntent.UserStop, false, false)]
    [InlineData((int)PipelineStopIntent.UserStop, true, true)]
    [InlineData((int)PipelineStopIntent.CaptureRestart, false, false)]
    [InlineData((int)PipelineStopIntent.CaptureRestart, false, true)]
    [InlineData((int)PipelineStopIntent.CaptureRestart, true, false)]
    [InlineData((int)PipelineStopIntent.CaptureRestart, true, true)]
    public void LifecyclePolicyNeverWritesReplayAutomatically(
        int intent, bool recording, bool compatible) =>
        Assert.False(PipelineRestartPolicy.For(
            (PipelineStopIntent)intent, recording, compatible).SaveReplay);

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

    [Theory]
    [InlineData("provider-selection")]
    [InlineData("hook-injection-failure")]
    [InlineData("focus-switch")]
    [InlineData("capture-recovery")]
    public void Automatic_capture_transitions_never_persist_video(string transition)
    {
        var persistence = new PersistenceSpy();
        PipelineRestartActions actions = PipelineRestartPolicy.For(
            PipelineStopIntent.CaptureRestart,
            continuousRecordingActive: false,
            formatCompatible: true);

        persistence.Apply(transition, actions);

        Assert.Equal(0, persistence.SaveCalls);
        Assert.Equal(0, persistence.MuxCalls);
        Assert.Equal(0, persistence.FileNameCalls);
        Assert.Equal(0, persistence.StorageCalls);
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

    private sealed class PersistenceSpy
    {
        public int SaveCalls { get; private set; }
        public int MuxCalls { get; private set; }
        public int FileNameCalls { get; private set; }
        public int StorageCalls { get; private set; }

        public void Apply(string transition, PipelineRestartActions actions)
        {
            Assert.False(string.IsNullOrWhiteSpace(transition));
            if (!actions.SaveReplay) return;
            SaveCalls++;
            MuxCalls++;
            FileNameCalls++;
            StorageCalls++;
        }
    }
}
