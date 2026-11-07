using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SzbTool.Core.Crypto;

/// <summary>
/// The streaming XOR used on Shadowverse: Worlds Beyond AssetBundles.
///
/// <code>
/// keystream[o] = base[(o mod P) &gt;&gt; 3] ^ key[(o mod P) &amp; 7]
/// P = 8 * base.Length
///
/// cipher[o] = plain[o]                , o &lt;  0x100
/// cipher[o] = plain[o] ^ keystream[o] , o &gt;= 0x100
/// </code>
///
/// <para>
/// <c>o</c> is the ABSOLUTE file offset, not an offset into a buffer and not an offset
/// relative to the data section. Getting this wrong produces a cipher that "almost" works,
/// which is far harder to debug than one that fails outright.
/// </para>
/// </summary>
public static class XorKeystream
{
    /// <summary>Number of leading plaintext bytes. Everything from here on is XORed.</summary>
    public const int HeaderSkip = 0x100;

    // NOTE: there is deliberately no key constant in this file.
    // The base key is data, not code: it changes between game versions and is fetched at
    // run time by SzbTool.Core.Keys.BaseKeySource. See docs/02_ENCRYPTION.md.

    /// <summary>Build one full period of the keystream. Empty key → empty period.</summary>
    public static byte[] BuildPeriod(ReadOnlySpan<byte> baseKey, long key)
    {
        int period = baseKey.Length * 8;
        var pat = new byte[period];
        for (int i = 0; i < baseKey.Length; i++)
        {
            byte b = baseKey[i];
            int off = i * 8;
            for (int j = 0; j < 8; j++)
                pat[off + j] = (byte)(b ^ (byte)(key >> (8 * j)));
        }
        return pat;
    }

    /// <summary>Single keystream byte at an absolute offset. Returns 0 when no key is loaded.</summary>
    public static byte ByteAt(ReadOnlySpan<byte> baseKey, long key, long absoluteOffset)
    {
        int period = baseKey.Length * 8;
        if (period == 0) return 0;
        int i = (int)(absoluteOffset % period);
        if (i < 0) i += period;
        return (byte)(baseKey[i >> 3] ^ (byte)(key >> (8 * (i & 7))));
    }

    /// <summary>
    /// XOR <paramref name="buffer"/> in place. <paramref name="absoluteOffset"/> is the
    /// file offset of <c>buffer[0]</c>. Bytes below <see cref="HeaderSkip"/> are left alone.
    /// </summary>
    public static void Apply(Span<byte> buffer, long absoluteOffset,
                             ReadOnlySpan<byte> baseKey, long key)
    {
        int period = baseKey.Length * 8;
        if (period == 0 || buffer.IsEmpty) return;

        byte[] pat = BuildPeriod(baseKey, key);

        int idx = 0;

        // Everything before 0x100 is plaintext — skip it.
        if (absoluteOffset < HeaderSkip)
        {
            long skip = HeaderSkip - absoluteOffset;
            if (skip >= buffer.Length) return;
            idx = (int)skip;
        }

        // Byte-wise until the ABSOLUTE offset is 8-aligned. Aligning the buffer index alone is
        // not enough: the ulong path indexes the period mask by phase/8, which is only correct
        // when phase itself is a multiple of 8.
        while (idx < buffer.Length && ((absoluteOffset + idx) & 7) != 0)
        {
            int p = (int)((absoluteOffset + idx) % period);
            buffer[idx] ^= pat[p];
            idx++;
        }

        // Fast path: XOR eight bytes at a time with a pre-built ulong mask.
        if (period % 8 == 0 && buffer.Length - idx >= 8)
        {
            int phase = (int)((absoluteOffset + idx) % period);
            int nWords = period / 8;
            Span<ulong> mask = nWords <= 64 ? stackalloc ulong[nWords] : new ulong[nWords];
            for (int i = 0; i < nWords; i++)
                mask[i] = BinaryPrimitives.ReadUInt64LittleEndian(pat.AsSpan(i * 8, 8));

            int mi = phase / 8;
            var words = MemoryMarshal.Cast<byte, ulong>(buffer[idx..]);
            ref ulong w0 = ref MemoryMarshal.GetReference(words);
            for (int i = 0; i < words.Length; i++)
            {
                Unsafe.Add(ref w0, i) ^= mask[mi];
                if (++mi == nWords) mi = 0;
            }
            idx += words.Length * 8;
        }

        while (idx < buffer.Length)
        {
            int p = (int)((absoluteOffset + idx) % period);
            buffer[idx] ^= pat[p];
            idx++;
        }
    }

    /// <summary>Parse "hex:0011223344" or a base64 string into a key. Returns null if unparseable.</summary>
    public static byte[]? TryParseBaseKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();

        if (text.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
            return Convert.FromHexString(text[4..].Replace(" ", "").Replace("-", ""));

        if (text.Length % 2 == 0 && text.All(Uri.IsHexDigit) && text.Length <= 64)
        {
            try { return Convert.FromHexString(text); } catch { /* fall through */ }
        }

        try { return Convert.FromBase64String(text); } catch { return null; }
    }
}
