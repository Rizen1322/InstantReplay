namespace Aura.Core.Engine;

internal enum PipelineStopIntent
{
    UserStop,
    CaptureRestart
}

internal readonly record struct PipelineRestartActions(
    bool KeepReplayBuffer,
    bool KeepAudioBuffer,
    bool SaveReplay,
    bool ResumeContinuousRecording);

internal static class PipelineRestartPolicy
{
    public static PipelineRestartActions For(
        PipelineStopIntent intent,
        bool continuousRecordingActive,
        bool formatCompatible)
    {
        bool restartingCapture = intent == PipelineStopIntent.CaptureRestart;
        return new(
            KeepReplayBuffer: restartingCapture && formatCompatible,
            KeepAudioBuffer: restartingCapture,
            SaveReplay: false,
            ResumeContinuousRecording: restartingCapture && continuousRecordingActive);
    }
}
