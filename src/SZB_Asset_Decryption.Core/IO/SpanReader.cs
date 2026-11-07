using System.Buffers.Binary;
using System.Text;

namespace SzbAssetDecryption.Core.IO;

/// <summary>
/// Reads big-endian primitives from a byte span with a mutable cursor.
/// UnityFS and CRI USM are both big-endian; AKPK is little-endian.
/// </summary>
public ref struct SpanReader
{
    private readonly ReadOnlySpan<byte> _data;

    public int Position { get; private set; }

    public SpanReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        Position = 0;
    }

    public readonly int Length => _data.Length;
    public readonly int Remaining => _data.Length - Position;
    public readonly ReadOnlySpan<byte> Span => _data;

    private void Need(int count)
    {
        if (Position + count > _data.Length)
            throw new InvalidDataException(
                $"读取越界：需要 {count} 字节，位置 {Position}，总长 {_data.Length}");
    }

    public byte U8()
    {
        Need(1);
        return _data[Position++];
    }

    public ushort U16BE()
    {
        Need(2);
        var v = BinaryPrimitives.ReadUInt16BigEndian(_data[Position..]);
        Position += 2;
        return v;
    }

    public uint U32BE()
    {
        Need(4);
        var v = BinaryPrimitives.ReadUInt32BigEndian(_data[Position..]);
        Position += 4;
        return v;
    }

    public int I32BE()
    {
        Need(4);
        var v = BinaryPrimitives.ReadInt32BigEndian(_data[Position..]);
        Position += 4;
        return v;
    }

    public long I64BE()
    {
        Need(8);
        var v = BinaryPrimitives.ReadInt64BigEndian(_data[Position..]);
        Position += 8;
        return v;
    }

    public uint U32LE()
    {
        Need(4);
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]);
        Position += 4;
        return v;
    }

    public ReadOnlySpan<byte> Bytes(int count)
    {
        Need(count);
        var s = _data.Slice(Position, count);
        Position += count;
        return s;
    }

    public void Skip(int count)
    {
        Need(count);
        Position += count;
    }

    /// <summary>NUL-terminated UTF-8 string.</summary>
    public string CString()
    {
        int start = Position;
        int i = start;
        while (i < _data.Length && _data[i] != 0) i++;
        var s = Encoding.UTF8.GetString(_data[start..i]);
        Position = Math.Min(i + 1, _data.Length);
        return s;
    }

    /// <summary>Round the cursor up to the next multiple of <paramref name="alignment"/>.</summary>
    public void Align(int alignment)
    {
        int rem = Position % alignment;
        if (rem != 0) Skip(alignment - rem);
    }

}
