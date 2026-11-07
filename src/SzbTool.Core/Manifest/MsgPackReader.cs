using System.Buffers.Binary;
using System.Text;

namespace SzbTool.Core.Manifest;

/// <summary>
/// A msgpack extension value. MasterMemory uses code 99 to wrap LZ4-compressed tables,
/// so the code has to survive parsing — a plain <c>byte[]</c> would lose it.
/// </summary>
public sealed record MsgPackExt(sbyte Code, byte[] Data);

/// <summary>
/// A small, dependency-free MessagePack reader.
///
/// <para>
/// Only the types that actually appear in an <c>assetbundle.manifest</c> are supported.
/// Pulling in the full MessagePack library would mean registering the <c>Ext</c> types
/// MasterMemory uses, configuring a resolver chain and a security mode — more setup code
/// than this reader is long. See docs/08_TOOLING.md.
/// </para>
///
/// <para>
/// Values are returned as: <c>null</c>, <c>bool</c>, <c>long</c>, <c>double</c>,
/// <c>string</c>, <c>byte[]</c>, <c>List&lt;object?&gt;</c>,
/// <c>Dictionary&lt;object?, object?&gt;</c>.
/// </para>
/// </summary>
public sealed class MsgPackReader
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly int _origin;
    private int _pos;

    public MsgPackReader(ReadOnlyMemory<byte> data, int start = 0)
    {
        _data = data;
        _origin = start;
        _pos = start;
    }

    public int Position => _pos;
    public int Consumed => _pos - _origin;
    public int Remaining => _data.Length - _pos;

    private ReadOnlySpan<byte> Span => _data.Span;

    private byte Next()
    {
        if (_pos >= _data.Length) throw new EndOfStreamException("msgpack 数据提前结束");
        return Span[_pos++];
    }

    private ReadOnlySpan<byte> Take(int n)
    {
        if (_pos + n > _data.Length) throw new EndOfStreamException("msgpack 数据提前结束");
        var s = Span.Slice(_pos, n);
        _pos += n;
        return s;
    }

    /// <summary>Read one complete value.</summary>
    public object? Read()
    {
        byte code = Next();

        // positive fixint
        if (code <= 0x7F) return (long)code;
        // negative fixint
        if (code >= 0xE0) return (long)(sbyte)code;

        // fixmap
        if (code is >= 0x80 and <= 0x8F) return ReadMap(code & 0x0F);
        // fixarray
        if (code is >= 0x90 and <= 0x9F) return ReadArray(code & 0x0F);
        // fixstr
        if (code is >= 0xA0 and <= 0xBF) return ReadStr(code & 0x1F);

        return code switch
        {
            0xC0 => null,
            0xC2 => false,
            0xC3 => true,
            0xC4 => ReadBin(Next()),
            0xC5 => ReadBin(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
            0xC6 => ReadBin((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))),
            0xC7 => ReadExt(Next()),
            0xC8 => ReadExt(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
            0xC9 => ReadExt((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))),
            0xCA => (double)BinaryPrimitives.ReadSingleBigEndian(Take(4)),
            0xCB => BinaryPrimitives.ReadDoubleBigEndian(Take(8)),
            0xCC => (long)Next(),
            0xCD => (long)BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
            0xCE => (long)BinaryPrimitives.ReadUInt32BigEndian(Take(4)),
            0xCF => (long)BinaryPrimitives.ReadUInt64BigEndian(Take(8)),
            0xD0 => (long)(sbyte)Next(),
            0xD1 => (long)BinaryPrimitives.ReadInt16BigEndian(Take(2)),
            0xD2 => (long)BinaryPrimitives.ReadInt32BigEndian(Take(4)),
            0xD3 => BinaryPrimitives.ReadInt64BigEndian(Take(8)),
            0xD4 => ReadExt(1),
            0xD5 => ReadExt(2),
            0xD6 => ReadExt(4),
            0xD7 => ReadExt(8),
            0xD8 => ReadExt(16),
            0xD9 => ReadStr(Next()),
            0xDA => ReadStr(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
            0xDB => ReadStr((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))),
            0xDC => ReadArray(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
            0xDD => ReadArray((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))),
            0xDE => ReadMap(BinaryPrimitives.ReadUInt16BigEndian(Take(2))),
            0xDF => ReadMap((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))),
            _ => throw new NotSupportedException($"不支持的 msgpack 类型字节 0x{code:X2}"),
        };
    }

    private string ReadStr(int len) => Encoding.UTF8.GetString(Take(len));

    private byte[] ReadBin(int len) => Take(len).ToArray();

    /// <summary>
    /// Extension values keep their type code. MasterMemory stores large tables as
    /// <c>Ext(99, int32 uncompressedSize + LZ4 block)</c>.
    /// </summary>
    private MsgPackExt ReadExt(int len)
    {
        sbyte code = (sbyte)Next();
        return new MsgPackExt(code, Take(len).ToArray());
    }

    private List<object?> ReadArray(int count)
    {
        var list = new List<object?>(count);
        for (int i = 0; i < count; i++) list.Add(Read());
        return list;
    }

    private Dictionary<object, object?> ReadMap(int count)
    {
        var map = new Dictionary<object, object?>(count);
        for (int i = 0; i < count; i++)
        {
            var k = Read() ?? NullKey;
            var v = Read();
            map[k] = v;
        }
        return map;
    }

    private static readonly object NullKey = "<null>";

    /// <summary>Skip a value without materialising it.</summary>
    public void Skip()
    {
        byte code = Next();
        if (code <= 0x7F || code >= 0xE0) return;
        if (code is >= 0x80 and <= 0x8F) { SkipMap(code & 0x0F); return; }
        if (code is >= 0x90 and <= 0x9F) { SkipArray(code & 0x0F); return; }
        if (code is >= 0xA0 and <= 0xBF) { Take(code & 0x1F); return; }

        switch (code)
        {
            case 0xC0 or 0xC2 or 0xC3: return;
            case 0xC4: Take(Next()); return;
            case 0xC5: Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2))); return;
            case 0xC6: Take((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))); return;
            case 0xC7: { int n = Next(); Next(); Take(n); return; }
            case 0xC8: { int n = BinaryPrimitives.ReadUInt16BigEndian(Take(2)); Next(); Take(n); return; }
            case 0xC9: { int n = (int)BinaryPrimitives.ReadUInt32BigEndian(Take(4)); Next(); Take(n); return; }
            case 0xCA: Take(4); return;
            case 0xCB: Take(8); return;
            case 0xCC or 0xD0: Take(1); return;
            case 0xCD or 0xD1: Take(2); return;
            case 0xCE or 0xD2: Take(4); return;
            case 0xCF or 0xD3: Take(8); return;
            case 0xD4: Next(); Take(1); return;
            case 0xD5: Next(); Take(2); return;
            case 0xD6: Next(); Take(4); return;
            case 0xD7: Next(); Take(8); return;
            case 0xD8: Next(); Take(16); return;
            case 0xD9: Take(Next()); return;
            case 0xDA: Take(BinaryPrimitives.ReadUInt16BigEndian(Take(2))); return;
            case 0xDB: Take((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))); return;
            case 0xDC: SkipArray(BinaryPrimitives.ReadUInt16BigEndian(Take(2))); return;
            case 0xDD: SkipArray((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))); return;
            case 0xDE: SkipMap(BinaryPrimitives.ReadUInt16BigEndian(Take(2))); return;
            case 0xDF: SkipMap((int)BinaryPrimitives.ReadUInt32BigEndian(Take(4))); return;
            default: throw new NotSupportedException($"不支持的 msgpack 类型字节 0x{code:X2}");
        }
    }

    private void SkipArray(int n) { for (int i = 0; i < n; i++) Skip(); }
    private void SkipMap(int n) { for (int i = 0; i < n * 2; i++) Skip(); }
}
