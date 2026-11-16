namespace Aura.Core.Engine;

internal enum PipelineStopIntent
{
    UserStop,
    CaptureRestart,
    /// <summary>
    /// Терминальная блокировка: захват не восстановился в этом процессе.
    /// Конвейер сносится, но накопленный RAM-повтор сохраняется для команды
    /// «Сохранить повтор» — поэтому это НЕ UserStop.
    /// </summary>
    CaptureBlocked
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
        // Блокировка не перезапускает конвейер, но и не освобождает то, что уже
        // накоплено: иначе человек теряет ровно тот клип, ради которого этот
        // этап и делался. Автосохранение на диск по-прежнему запрещено.
        bool blocked = intent == PipelineStopIntent.CaptureBlocked;
        return new(
            KeepReplayBuffer: (restartingCapture && formatCompatible) || blocked,
            KeepAudioBuffer: restartingCapture || blocked,
            SaveReplay: false,
            ResumeContinuousRecording: restartingCapture && continuousRecordingActive);
    }
}
