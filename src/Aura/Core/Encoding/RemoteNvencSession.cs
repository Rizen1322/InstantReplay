using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Aura.Core.Logging;

namespace Aura.Core.Encoding;

/// <summary>Сессия NVENC: в процессе Aura (<see cref="NvencSession"/>) или в хосте (<see cref="RemoteNvencSession"/>).</summary>
internal interface INvencSession : IDisposable
{
    NvencSession.Applied Settings { get; }
    int PendingCount { get; }
    (ArraySegment<byte> Data, long Pts, int PictureType)? TryGet(uint timeoutMs);
    int Reconfigure(int multipass, bool spatialAq);
    byte[]? SequenceHeader();
    string Trace();
    void EndOfStream();
    string Describe();
    /// <summary>Управление битрейтом, как его применил драйвер (для лога).</summary>
    string RcInfo();
    bool LastSkipFatal { get; }
    string LastSkipReason { get; }
    bool ReleaseFailed { get; }
}

/// <summary>
/// Сессия NVENC в отдельном процессе Aura.EncoderHost.exe (src/native/Aura.Media/encoder_host.c).
///
/// ЗАЧЕМ. Зависший внутри драйвера вызов NVENC в процессе Aura не прервать: поток
/// стоит в драйвере, и его объекты нельзя освобождать. Раньше такой конвейер
/// бросался до конца процесса вместе с видеопамятью и потоком. Хост можно убить:
/// Windows сама освобождает всё, что он держал на видеокарте, а Aura поднимает
/// новый. Общие текстуры кадров создаёт Aura, поэтому они переживают смерть хоста.
///
/// ОБМЕН. Общая память и два события: Aura пишет запрос, будит хост и ждёт ответ с
/// таймаутом. Запрос всегда один (замок <see cref="_call"/>). Хост не ответил за
/// таймаут — он убивается, а сессия считается мёртвой: вызывающий пересобирает
/// конвейер. Раскладка блока повторяет encoder_host.c; менять только вместе.
/// </summary>
internal sealed unsafe class RemoteNvencSession : INvencSession
{
    // ---- раскладка управляющего блока (encoder_host.c) ----
    private const int HostMagic = 0x31484E56;
    private const int OffMagic = 0, OffType = 4, OffResult = 8, OffDataLength = 12;
    private const int OffArgs = 16, OffOut = 144, OffConfig = 272, OffApplied = 448, OffHandles = 512, OffError = 1024;
    private const int OffData = 65536;
    private const long MappingBytes = OffData + 32L * 1024 * 1024;   // самый большой ключевой кадр с запасом
    private const int MaxSlots = 64;

    private enum Request { Open = 1, Attach, Encode, Get, FreeSlots, Reconfigure, SequenceHeader, Trace, EndOfStream, Destroy, RcInfo }

    /// <summary>Ответ ENCODE: общий слот не отдан за 200 мс.</summary>
    public const int EncodeSlotTimeout = -2000;
    /// <summary>Ответ ENCODE: ключ общей текстуры брошен (WAIT_ABANDONED).</summary>
    public const int EncodeSlotAbandoned = -2001;
    /// <summary>Хост не ответил или умер: сессия мертва.</summary>
    public const int HostDead = -3000;

    private const int CallTimeoutMs = 10_000;
    private const int OpenTimeoutMs = 15_000;

    private readonly MemoryMappedFile _mapping;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _base;
    private readonly EventWaitHandle _request, _response;
    private readonly Process _process;
    private readonly IntPtr _job;
    private readonly object _call = new();
    private readonly WaitHandle[] _waits;
    private volatile bool _dead;
    private volatile string _currentCall = "";
    private long _callStarted;
    private long _sent, _got;
    private byte[] _output = new byte[1 << 20];

    public NvencSession.Applied Settings { get; private set; }
    public bool LastSkipFatal { get; private set; }
    public string LastSkipReason { get; private set; } = "";
    public bool ReleaseFailed { get; private set; }

    /// <summary>Хост мёртв: не ответил или убит. Сессию нужно пересоздать.</summary>
    public bool Dead => _dead;

    /// <summary>Почему хост мёртв — для лога.</summary>
    public string DeathReason { get; private set; } = "";

    /// <summary>
    /// Хост убила сама Aura по своей причине (сторож, бросание конвейера, закрытие),
    /// а не он умер или завис сам. Такая смерть уже учтена или отказом не считается.
    /// </summary>
    public bool ExpectedDeath { get; private set; }

    public int ProcessId { get; }

    private RemoteNvencSession(MemoryMappedFile mapping, MemoryMappedViewAccessor view, EventWaitHandle request,
                               EventWaitHandle response, Process process, IntPtr job)
    {
        _mapping = mapping;
        _view = view;
        _request = request;
        _response = response;
        _process = process;
        _job = job;
        ProcessId = process.Id;
        _waits = [response, new ProcessWaitHandle(process)];
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _base = pointer + view.PointerOffset;
    }

    public static string HostPath => Path.Combine(AppContext.BaseDirectory, "Aura.EncoderHost.exe");

    /// <summary>
    /// Запустить хост и открыть в нём сессию. null — не вышло, <paramref name="error"/> скажет почему;
    /// тогда вызывающий кодирует в своём процессе, как раньше.
    /// </summary>
    public static RemoteNvencSession? TryStart(long adapterLuid, int gpuPriorityClass, NvencSession.Config config,
                                               out string error)
    {
        error = "";
        if (!File.Exists(HostPath)) { error = "нет Aura.EncoderHost.exe"; return null; }

        string name = $"Local\\AuraNvenc-{Environment.ProcessId}-{Guid.NewGuid():N}";
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? view = null;
        EventWaitHandle? request = null, response = null;
        Process? process = null;
        IntPtr job = IntPtr.Zero;
        RemoteNvencSession? session = null;
        try
        {
            mapping = MemoryMappedFile.CreateNew(name + "-map", MappingBytes);
            view = mapping.CreateViewAccessor();
            request = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-req");
            response = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-resp");

            var psi = new ProcessStartInfo(HostPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            psi.ArgumentList.Add("--host");
            psi.ArgumentList.Add(name);
            psi.ArgumentList.Add("--parent");
            psi.ArgumentList.Add(Environment.ProcessId.ToString());
            process = Process.Start(psi) ?? throw new InvalidOperationException("процесс не запустился");
            // Хост умирает вместе с Aura, даже если та упала: задание с KILL_ON_JOB_CLOSE
            job = JobObject.CreateKillOnClose(process);

            session = new RemoteNvencSession(mapping, view, request, response, process, job);
            if (!response.WaitOne(5000) || session.ReadInt(OffMagic) != HostMagic)
                throw new InvalidOperationException(process.HasExited
                    ? $"хост завершился с кодом {process.ExitCode}"
                    : "хост не ответил за 5 секунд");

            // Открыть сессию: устройство на той же видеокарте, приоритет очереди как у Aura
            session.WriteLong(OffArgs, adapterLuid);
            session.WriteLong(OffArgs + 8, gpuPriorityClass);
            var cfg = config;
            Buffer.MemoryCopy(&cfg, session._base + OffConfig, sizeof(NvencSession.Config), sizeof(NvencSession.Config));
            int ok = session.Call(Request.Open, OpenTimeoutMs);
            if (ok != 1) throw new InvalidOperationException(session.ReadError() is { Length: > 0 } e ? e : $"код {ok}");
            NvencSession.Applied applied;
            Buffer.MemoryCopy(session._base + OffApplied, &applied, sizeof(NvencSession.Applied), sizeof(NvencSession.Applied));
            session.Settings = applied;
            return session;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            if (session is not null) session.Kill(error);
            else
            {
                try { process?.Kill(); } catch { }
                JobObject.Close(job);
                view?.Dispose();
                mapping?.Dispose();
                request?.Dispose();
                response?.Dispose();
            }
            session?.Dispose();
            return null;
        }
    }

    /// <summary>Передать хосту общие текстуры пула (legacy shared handles). false — хост их не открыл.</summary>
    public bool Attach(IReadOnlyList<IntPtr> handles, out string error)
    {
        lock (_call) return AttachLocked(handles, out error);
    }

    private bool AttachLocked(IReadOnlyList<IntPtr> handles, out string error)
    {
        error = "";
        if (handles.Count is 0 or > MaxSlots) { error = "неверное число слотов"; return false; }
        WriteLong(OffArgs, handles.Count);
        for (int i = 0; i < handles.Count; i++) WriteLong(OffHandles + i * 8, handles[i].ToInt64());
        int ok = Call(Request.Attach, CallTimeoutMs);
        if (ok == 1) return true;
        error = ReadError() is { Length: > 0 } e ? e : $"код {ok}";
        return false;
    }

    /// <summary>
    /// Отправить кадр из общего слота <paramref name="slot"/>. <paramref name="retry"/> —
    /// повтор после ENCODER_BUSY: копия уже во входе хоста, слот не трогается.
    /// Коды как у <see cref="NvencSession.Encode"/>, плюс EncodeSlot* и <see cref="HostDead"/>.
    /// </summary>
    public int EncodeSlot(int slot, long pts, bool forceIdr, bool retry)
    {
        lock (_call) return EncodeSlotLocked(slot, pts, forceIdr, retry);
    }

    private int EncodeSlotLocked(int slot, long pts, bool forceIdr, bool retry)
    {
        WriteLong(OffArgs, slot);
        WriteLong(OffArgs + 8, pts);
        WriteLong(OffArgs + 16, forceIdr ? 1 : 0);
        WriteLong(OffArgs + 24, retry ? 1 : 0);
        int result = Call(Request.Encode, CallTimeoutMs);
        if (result > 0) Interlocked.Increment(ref _sent);
        return result;
    }

    public int PendingCount => (int)(Interlocked.Read(ref _sent) - Interlocked.Read(ref _got));

    public (ArraySegment<byte> Data, long Pts, int PictureType)? TryGet(uint timeoutMs)
    {
        lock (_call) return TryGetLocked(timeoutMs);
    }

    private (ArraySegment<byte> Data, long Pts, int PictureType)? TryGetLocked(uint timeoutMs)
    {
        WriteLong(OffArgs, timeoutMs);
        int result = Call(Request.Get, (int)Math.Min(int.MaxValue, timeoutMs + CallTimeoutMs));
        if (result == HostDead)
        {
            LastSkipFatal = true;
            LastSkipReason = DeathReason;
            return null;
        }
        if (result == 0 || result == -2) return null;
        Interlocked.Increment(ref _got);
        long pts = ReadLong(OffOut);
        int type = (int)ReadLong(OffOut + 8);
        if (result == 2)
        {
            // Кадр цел, но NVENC не отпустил буфер выхода или вход (см. nvenc_shim.c)
            ReleaseFailed = true;
            result = 1;
        }
        if (result == 1)
        {
            int size = ReadInt(OffDataLength);
            if (_output.Length < size) _output = new byte[Math.Max(size, _output.Length * 2)];
            Marshal.Copy((IntPtr)(_base + OffData), _output, 0, size);
            return (new ArraySegment<byte>(_output, 0, size), pts, type);
        }
        if (result == -1)
        {
            LastSkipFatal = true;
            LastSkipReason = "кадр не поместился в память";
        }
        else
        {
            int status = -(result + 3);
            LastSkipFatal = !NvencSession.IsTransient(status);
            LastSkipReason = $"nvEncLockBitstream → {NvencSession.StatusName(status)}";
        }
        return (default, pts, NvencSession.SkippedPicture);
    }

    public int Reconfigure(int multipass, bool spatialAq)
    {
        lock (_call)
        {
            WriteLong(OffArgs, multipass);
            WriteLong(OffArgs + 8, spatialAq ? 1 : 0);
            return CallLocked(Request.Reconfigure, CallTimeoutMs);
        }
    }

    public byte[]? SequenceHeader()
    {
        lock (_call)
        {
            if (CallLocked(Request.SequenceHeader, CallTimeoutMs) != 1) return null;
            int size = ReadInt(OffDataLength);
            var header = new byte[size];
            Marshal.Copy((IntPtr)(_base + OffData), header, 0, size);
            return header;
        }
    }

    public void EndOfStream() => Call(Request.EndOfStream, CallTimeoutMs);

    public string RcInfo()
    {
        lock (_call)
        {
            int n = CallLocked(Request.RcInfo, CallTimeoutMs);
            if (n <= 0) return "";
            var bytes = new byte[Math.Min(n, ReadInt(OffDataLength))];
            Marshal.Copy((IntPtr)(_base + OffData), bytes, 0, bytes.Length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
    }

    /// <summary>
    /// Журнал вызовов NVENC из хоста. Если хост занят зависшим вызовом, сам журнал
    /// не получить — тогда хотя бы какой вызов и сколько он идёт.
    /// </summary>
    public string Trace()
    {
        if (_dead) return $"хост NVENC мёртв: {DeathReason}";
        if (!Monitor.TryEnter(_call, 200))
            return $"хост NVENC (PID {ProcessId}) занят вызовом {_currentCall} уже " +
                   $"{Stopwatch.GetElapsedTime(Interlocked.Read(ref _callStarted)).TotalMilliseconds:F0} мс";
        try
        {
            int n = CallLocked(Request.Trace, 2000);
            if (n <= 0) return "журнал пуст";
            var bytes = new byte[Math.Min(n, ReadInt(OffDataLength))];
            Marshal.Copy((IntPtr)(_base + OffData), bytes, 0, bytes.Length);
            return $"хост PID {ProcessId}\n" + System.Text.Encoding.UTF8.GetString(bytes).TrimEnd();
        }
        finally { Monitor.Exit(_call); }
    }

    public string Describe()
    {
        var s = Settings;
        return $"B-кадров {s.BFrames}{(s.BFrameRef > 0 ? " (опорные)" : "")}, просмотр вперёд {s.Lookahead}, " +
               $"AQ {(s.SpatialAq > 0 ? "пространственный" : "выкл")}{(s.TemporalAq > 0 ? "+временной" : "")}, " +
               $"проходов {(s.Multipass > 0 ? 2 : 1)}{(s.Multipass == 1 ? " (¼ разрешения)" : "")}, " +
               $"буферов {s.BufferCount}, {(s.TenBit == 1 ? "10 бит" : "8 бит")}; в отдельном процессе (PID {ProcessId})";
    }

    // ---------------- обмен ----------------

    /// <summary>
    /// Весь обмен — запись аргументов, вызов и чтение результата и данных — идёт
    /// под одним замком <see cref="_call"/>: общий блок и область данных одни на
    /// все запросы. Раньше аргументы писались до замка, а кадр читался после, и
    /// Trace со сторожа между ними мог переписать область данных посреди кадра.
    /// </summary>
    private int Call(Request type, int timeoutMs)
    {
        lock (_call) return CallLocked(type, timeoutMs);
    }

    private int CallLocked(Request type, int timeoutMs)
    {
        if (_dead) return HostDead;
        _currentCall = type.ToString();
        Interlocked.Exchange(ref _callStarted, Stopwatch.GetTimestamp());
        WriteInt(OffType, (int)type);
        _request.Set();
        int index = WaitHandle.WaitAny(_waits, timeoutMs);
        _currentCall = "";
        if (index == 0) return ReadInt(OffResult);
        Kill(index == 1
            ? $"хост NVENC завершился посреди вызова {type} (код {SafeExitCode()})"
            : $"хост NVENC не ответил на {type} за {timeoutMs / 1000.0:F0} с");
        return HostDead;
    }

    /// <summary>Убить хост: зависший драйвер в нём не мешает Aura, а память освободит Windows.</summary>
    public void Kill(string reason, bool expected = false)
    {
        if (_dead) return;
        _dead = true;
        DeathReason = reason;
        ExpectedDeath = expected;
        Log.Warn("Encoder", $"{reason} — хост NVENC (PID {ProcessId}) убит, его видеопамять освободит система");
        try { if (!_process.HasExited) _process.Kill(); } catch { }
    }

    private string SafeExitCode()
    {
        try { return _process.HasExited ? _process.ExitCode.ToString() : "?"; } catch { return "?"; }
    }

    private int ReadInt(int offset) => Volatile.Read(ref *(int*)(_base + offset));
    private long ReadLong(int offset) => Volatile.Read(ref *(long*)(_base + offset));
    private void WriteInt(int offset, int value) => Volatile.Write(ref *(int*)(_base + offset), value);
    private void WriteLong(int offset, long value) => Volatile.Write(ref *(long*)(_base + offset), value);

    private string ReadError()
    {
        var span = new ReadOnlySpan<byte>(_base + OffError, 1024);
        int end = span.IndexOf((byte)0);
        return System.Text.Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    public void Dispose()
    {
        if (!_dead)
        {
            // Штатно: хост сам закрывает сессию и устройство и выходит
            lock (_call) CallLocked(Request.Destroy, 3000);
            try
            {
                if (!_process.WaitForExit(2000)) Kill("хост NVENC не вышел после закрытия");
            }
            catch { }
        }
        try { _process.WaitForExit(2000); } catch { }
        _process.Dispose();
        JobObject.Close(_job);
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mapping.Dispose();
        _request.Dispose();
        _response.Dispose();
    }

    /// <summary>Ожидание завершения процесса как WaitHandle: хост умер — ответа не будет.</summary>
    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(Process process)
        {
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process.Handle, ownsHandle: false);
        }
    }
}

/// <summary>Задание Windows, которое убивает свои процессы, когда закрыт его дескриптор (или умерла Aura).</summary>
internal static class JobObject
{
    public static IntPtr CreateKillOnClose(Process process)
    {
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new ExtendedLimitInformation();
        info.Basic.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()) ||
            !AssignProcessToJobObject(job, process.Handle))
        {
            CloseHandle(job);
            return IntPtr.Zero;
        }
        return job;
    }

    public static void Close(IntPtr job)
    {
        if (job != IntPtr.Zero) CloseHandle(job);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
