using System.Buffers.Binary;

namespace Aura.Core.Saving.Mp4;

/// <summary>
/// Сборка ISO BMFF (MP4) в памяти: числа big-endian и вложенные боксы с размером,
/// который проставляется при закрытии.
/// </summary>
public sealed class BoxWriter
{
    private byte[] _buffer = new byte[4096];
    private readonly Stack<int> _open = new();

    public int Length { get; private set; }

    private Span<byte> Grow(int count)
    {
        if (Length + count > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + count));
        var span = _buffer.AsSpan(Length, count);
        Length += count;
        return span;
    }

    public void U8(byte v) => Grow(1)[0] = v;
    public void U16(ushort v) => BinaryPrimitives.WriteUInt16BigEndian(Grow(2), v);
    public void U24(uint v) { var s = Grow(3); s[0] = (byte)(v >> 16); s[1] = (byte)(v >> 8); s[2] = (byte)v; }
    public void U32(uint v) => BinaryPrimitives.WriteUInt32BigEndian(Grow(4), v);
    public void I32(int v) => BinaryPrimitives.WriteInt32BigEndian(Grow(4), v);
    public void U64(ulong v) => BinaryPrimitives.WriteUInt64BigEndian(Grow(8), v);
    public void Bytes(ReadOnlySpan<byte> v) => v.CopyTo(Grow(v.Length));
    public void Zeros(int count) => Grow(count).Clear();

    public void FourCc(string code)
    {
        var s = Grow(4);
        for (int i = 0; i < 4; i++) s[i] = (byte)code[i];
    }

    /// <summary>Открыть бокс; размер проставит <see cref="End"/>.</summary>
    public void Begin(string type)
    {
        _open.Push(Length);
        U32(0);
        FourCc(type);
    }

    /// <summary>Открыть «полный» бокс: версия и флаги.</summary>
    public void BeginFull(string type, byte version, uint flags)
    {
        Begin(type);
        U8(version);
        U24(flags);
    }

    public void End()
    {
        int start = _open.Pop();
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(start, 4), (uint)(Length - start));
    }

    /// <summary>Строка с завершающим нулём (имя обработчика в hdlr).</summary>
    public void CString(string text)
    {
        Bytes(System.Text.Encoding.UTF8.GetBytes(text));
        U8(0);
    }

    /// <summary>Перезаписать 32-битное число по смещению (заплатка после сборки).</summary>
    public void PatchU32(int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(_buffer.AsSpan(offset, 4), value);

    public ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Length);

    public byte[] ToArray() => _buffer.AsSpan(0, Length).ToArray();

    /// <summary>Единичная матрица преобразования для mvhd/tkhd.</summary>
    public void UnityMatrix()
    {
        U32(0x00010000); U32(0); U32(0);
        U32(0); U32(0x00010000); U32(0);
        U32(0); U32(0); U32(0x40000000);
    }
}
