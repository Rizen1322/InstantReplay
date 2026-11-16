using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace Aura.Core.Capture.Host;

/// <summary>
/// Доступ к управляющему блоку общей памяти между Aura и воркером захвата.
/// Блок держится на процесс целиком: ни одного выделения на кадр здесь нет.
///
/// Чтение/запись идут через указатель; между записью метаданных и публикацией
/// ставится барьер памяти, а будит потребителя именованное событие — его
/// срабатывание само по себе даёт нужную синхронизацию.
/// </summary>
internal sealed unsafe class CaptureHostControl : IDisposable
{
    private readonly MemoryMappedViewAccessor _view;
    private byte* _base;
    private bool _hasPointer;

    public CaptureHostControl(MemoryMappedViewAccessor view)
    {
        _view = view;
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        _base = pointer + view.PointerOffset;
        _hasPointer = true;
    }

    public byte* Pointer => _base;

    public int ReadInt(int offset) => *(int*)(_base + offset);
    public long ReadLong(int offset) => *(long*)(_base + offset);
    public void WriteInt(int offset, int value)
    {
        *(int*)(_base + offset) = value;
        Interlocked.MemoryBarrier();
    }

    public void WriteLong(int offset, long value)
    {
        *(long*)(_base + offset) = value;
        Interlocked.MemoryBarrier();
    }

    public void WriteLongNoBarrier(int offset, long value) => *(long*)(_base + offset) = value;

    public void WriteUtf16(int offset, int capacityBytes, string value)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(value);
        int length = Math.Min(bytes.Length, capacityBytes - sizeof(char));
        new Span<byte>(bytes, 0, length).CopyTo(new Span<byte>(_base + offset, length));
        *(char*)(_base + offset + length) = '\0';
        Interlocked.MemoryBarrier();
    }

    public string ReadUtf16(int offset, int capacityBytes)
    {
        int max = capacityBytes / sizeof(char);
        var chars = new char[max];
        int length = 0;
        while (length < max - 1)
        {
            char c = *(char*)(_base + offset + length * sizeof(char));
            if (c == '\0') break;
            chars[length++] = c;
        }
        return new string(chars, 0, length);
    }

    public int ReadSlotInt(int slot, int fieldOffset) =>
        ReadInt(CaptureHostProtocol.SlotMetadataOffset(slot) + fieldOffset);

    public long ReadSlotLong(int slot, int fieldOffset) =>
        ReadLong(CaptureHostProtocol.SlotMetadataOffset(slot) + fieldOffset);

    public void WriteSlotInt(int slot, int fieldOffset, int value) =>
        WriteInt(CaptureHostProtocol.SlotMetadataOffset(slot) + fieldOffset, value);

    public void WriteSlotLong(int slot, int fieldOffset, long value) =>
        WriteLong(CaptureHostProtocol.SlotMetadataOffset(slot) + fieldOffset, value);

    public void WriteShape(int slot, ReadOnlySpan<byte> pixels)
    {
        pixels.CopyTo(new Span<byte>(_base + CaptureHostProtocol.SlotShapeOffset(slot), pixels.Length));
        Interlocked.MemoryBarrier();
    }

    public void ReadShape(int slot, int length, byte[] destination)
    {
        if (length <= 0) return;
        new Span<byte>(_base + CaptureHostProtocol.SlotShapeOffset(slot), length).CopyTo(destination);
    }

    public void Dispose()
    {
        if (_hasPointer)
        {
            try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
            _hasPointer = false;
            _base = null;
        }
        _view.Dispose();
    }
}

/// <summary>Имена объектов ядра для одной сессии: общая память и четыре события.</summary>
internal static class CaptureHostSessionNames
{
    public static string CreateSessionName() =>
        $"Local\\AuraCapture-{Environment.ProcessId}-{Guid.NewGuid():N}";

    public static string Mapping(string session) => session + "-map";
    public static string Ready(string session) => session + "-ready";
    public static string Attach(string session) => session + "-attach";
    public static string Frame(string session) => session + "-frame";
    public static string Stop(string session) => session + "-stop";
}

internal static class CaptureHostNative
{
    public const uint DuplicateSameAccess = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle,
        IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess, bool inheritHandle,
        uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();
}
