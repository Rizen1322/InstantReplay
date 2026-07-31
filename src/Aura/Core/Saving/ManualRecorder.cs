using System.Runtime.InteropServices;
using Vortice.MediaFoundation;
using Aura.Core.Buffering;
using Aura.Core.Logging;
using Aura.Core.Settings;

namespace Aura.Core.Saving;

/// <summary>
/// Обычная запись в файл («Начать запись»): подписывается на те же сжатые кадры
/// и аудиоблоки, что и кольцевой буфер, но пишет их в MP4 на лету через SinkWriter.
/// RAM не расходуется — данные сразу уходят на диск; остановка = Finalize (мгновенно).
/// Первый кадр ждём keyframe, иначе начало файла — «каша».
/// </summary>
public sealed class ManualRecorder : IDisposable
{
    private readonly object _sync = new();
    private IMFSinkWriter? _writer;
    private readonly int _videoStream;
    private readonly List<(int Index, Func<AudioBlock, float[]> Selector)> _audioStreams;
    private long _baseTicks = -1;
    private bool _finished;

    public string FilePath { get; }
    public DateTime StartedAt { get; } = DateTime.Now;

    public ManualRecorder(string filePath, IMFMediaType videoType,
        AudioTrackMode trackMode, bool hasGame, bool hasMic)
    {
        FilePath = filePath;
        _writer = MfMp4Writer.Create(filePath);
        _videoStream = MfMp4Writer.AddPassthroughVideoStream(_writer, videoType);
        _audioStreams = MfMp4Writer.AddAudioStreams(_writer, trackMode, hasGame, hasMic);
        _writer.BeginWriting();
        Log.Info("Recorder", $"Запись в файл начата: {filePath}");
    }

    public void OnFrame(EncodedFrame f)
    {
        lock (_sync)
        {
            if (_writer is null || _finished) return;
            if (_baseTicks < 0)
            {
                if (!f.IsKeyframe) return; // ждём первый keyframe
                _baseTicks = f.PtsTicks;
            }
            try
            {
                using var sample = MfMp4Writer.CreateSample(f.Data, f.Length, f.PtsTicks - _baseTicks, f.DurationTicks);
                if (f.IsKeyframe) sample.Set(SampleAttributeKeys.CleanPoint, 1u);
                _writer.WriteSample(_videoStream, sample);
            }
            catch (Exception ex) { Log.Error("Recorder", ex); }
        }
    }

    public void OnAudio(AudioBlock block)
    {
        lock (_sync)
        {
            if (_writer is null || _finished || _baseTicks < 0) return;
            long pts = block.PtsTicks - _baseTicks;
            if (pts < 0) return;
            try
            {
                foreach (var (index, selector) in _audioStreams)
                {
                    var bytes = MemoryMarshal.AsBytes<float>(selector(block)).ToArray();
                    using var sample = MfMp4Writer.CreateSample(bytes, bytes.Length, pts, 100_000);
                    _writer.WriteSample(index, sample);
                }
            }
            catch (Exception ex) { Log.Error("Recorder", ex); }
        }
    }

    /// <summary>Завершает файл. Возвращает фактическую длительность записи в секундах.</summary>
    public int Finish()
    {
        lock (_sync)
        {
            if (_writer is null || _finished) return 0;
            _finished = true;
            try { _writer.Finalize(); }
            catch (Exception ex) { Log.Error("Recorder", ex); }
            _writer.Dispose();
            _writer = null;
            int seconds = (int)Math.Round((DateTime.Now - StartedAt).TotalSeconds);
            Log.Info("Recorder", $"Запись завершена: {FilePath} ({seconds} сек)");
            return seconds;
        }
    }

    public void Dispose() => Finish();
}
