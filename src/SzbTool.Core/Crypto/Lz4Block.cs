namespace SzbTool.Core.Crypto;

/// <summary>
/// Minimal LZ4 block-format decoder.
///
/// <para>
/// A block is a sequence of runs. Each run is:
/// <c>[token][extra length bytes][literals][u16 offset][extra match length bytes][match]</c>.
/// The token's high nibble is the literal count, low nibble is match length minus four;
/// a nibble of 15 means "read more bytes, adding each, until one is below 255".
/// </para>
///
/// <para>
/// Only the decoder is implemented — we never need to compress, because the restored
/// bundles keep their original compressed blocks and simply stop being encrypted.
/// </para>
/// </summary>
public static class Lz4Block
{
    /// <summary>
    /// Decompress a raw LZ4 block. Returns the payload; its length may be shorter than
    /// <paramref name="expectedSize"/> when the input is not a valid block — callers
    /// should treat a length mismatch as "wrong key" rather than "bad file".
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> source, int expectedSize)
    {
        if (expectedSize < 0) throw new ArgumentOutOfRangeException(nameof(expectedSize));
        var dst = new byte[expectedSize];

        int si = 0, di = 0;
        int n = source.Length;

        while (si < n)
        {
            byte token = source[si++];

            // ---- literals ----
            int literalLength = token >> 4;
            if (literalLength == 15)
            {
                byte b;
                do
                {
                    if (si >= n) return dst[..di];
                    b = source[si++];
                    literalLength += b;
                }
                while (b == 255);
            }

            if (literalLength > 0)
            {
                if (si + literalLength > n || di + literalLength > dst.Length)
                    return dst[..di];
                source.Slice(si, literalLength).CopyTo(dst.AsSpan(di));
                si += literalLength;
                di += literalLength;
            }

            // Last run has no match part.
            if (si >= n) break;
            if (si + 2 > n) return dst[..di];

            // ---- match ----
            int offset = source[si] | (source[si + 1] << 8);
            si += 2;

            if (offset == 0 || offset > di)
                return dst[..di];          // invalid back-reference

            int matchLength = token & 0x0F;
            if (matchLength == 15)
            {
                byte b;
                do
                {
                    if (si >= n) return dst[..di];
                    b = source[si++];
                    matchLength += b;
                }
                while (b == 255);
            }
            matchLength += 4;

            if (di + matchLength > dst.Length)
                return dst[..di];

            // Overlapping copies are legal in LZ4 and common (run-length encoding).
            int src = di - offset;
            for (int k = 0; k < matchLength; k++)
                dst[di + k] = dst[src + k];
            di += matchLength;
        }

        return di == expectedSize ? dst : dst[..di];
    }
}
