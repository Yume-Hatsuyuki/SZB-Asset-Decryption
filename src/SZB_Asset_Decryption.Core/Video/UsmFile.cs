using System.Buffers.Binary;
using System.Text;

namespace SzbAssetDecryption.Core.Video;

public enum UsmVideoCodec
{
    Unknown,
    Vp9InIvf,
    Mpeg1,
    H264,
}

public enum UsmPayloadKind
{
    Metadata,
    Marker,
    VideoStart,
    VideoFrame,
    Audio,
    Other,
}

/// <summary>One chunk of a CRI USM file.</summary>
public sealed class UsmBlock
{
    public string Tag { get; init; } = string.Empty;
    public long Offset { get; init; }

    /// <summary>Value of the size field. Total block length is this plus 8.</summary>
    public uint Size { get; init; }

    public long TotalLength => Size + 8L;
    public long PayloadOffset => Offset + 0x20;
    public long PayloadLength => Size - 0x18L;

    public override string ToString() => $"{Tag} @0x{Offset:X} size={Size}";
}

/// <summary>
/// CRI USM (Sofdec) reader.
///
/// <para>
/// Fixed 0x800-byte header, then a chain of blocks:
/// <c>[4-byte tag][u32 BE size][24 more header bytes][payload]</c> where
/// <c>total = size + 8</c> and <c>payloadLength = size - 0x18</c>.
/// </para>
///
/// <para>
/// This title stores the metadata blobs inside <c>@SFV</c> blocks, so the tag alone is not
/// enough — the payload must be classified by its first bytes. See docs/07_VIDEO.md.
/// </para>
/// </summary>
public sealed class UsmFile
{
    public const int HeaderSize = 0x800;

    public List<UsmBlock> Blocks { get; } = new();
    public long FileSize { get; private set; }
    public string Path { get; private set; } = string.Empty;
    public uint FrameRate { get; private set; }

    public UsmVideoCodec Codec { get; private set; } = UsmVideoCodec.Unknown;
    public bool HasAudio { get; private set; }
    public int VideoFrameCount { get; private set; }
    public int AudioBlockCount { get; private set; }

    /// <summary>Width and height when they could be read from the DKIF or MPEG-1 header.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>True when the block chain ends exactly at the end of the file.</summary>
    public bool ChainClosed { get; private set; }

    public static UsmFile Open(string path)
    {
        var usm = new UsmFile { Path = path };

        using var fs = File.OpenRead(path);
        usm.FileSize = fs.Length;

        Span<byte> header = stackalloc byte[32];
        fs.ReadExactly(header);

        if (!header[..4].SequenceEqual("CRID"u8))
            throw new InvalidDataException("不是 CRID/USM 文件");

        usm.FrameRate = BinaryPrimitives.ReadUInt32BigEndian(header[0x14..]);

        long pos = HeaderSize;
        Span<byte> bh = stackalloc byte[8];

        while (pos + 8 <= fs.Length)
        {
            fs.Position = pos;
            if (fs.Read(bh) < 8) break;

            if (bh[0] != (byte)'@')
                break;                                  // drifted out of the chain

            string tag = Encoding.ASCII.GetString(bh[..4].ToArray());
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bh[4..]);

            if (size < 0x18 || pos + size + 8L > fs.Length)
                break;

            usm.Blocks.Add(new UsmBlock { Tag = tag, Offset = pos, Size = size });
            pos += size + 8L;

            if (tag == "@END") break;
        }

        usm.ChainClosed = pos == fs.Length;
        usm.Classify(fs);
        return usm;
    }

    private void Classify(FileStream fs)
    {
        var probe = new byte[64];

        foreach (var b in Blocks)
        {
            int n = (int)Math.Min(probe.Length, b.PayloadLength);
            if (n <= 0) continue;

            fs.Position = b.PayloadOffset;
            int got = fs.Read(probe, 0, n);

            if (b.Tag == "@SFA")
            {
                if (got >= 2 && probe[0] == 0x80 && probe[1] == 0x00)
                {
                    HasAudio = true;
                    AudioBlockCount++;
                }
                continue;
            }

            if (b.Tag != "@SFV") continue;

            var kind = ClassifyPayload(probe, got);

            if (kind == UsmPayloadKind.VideoStart && Codec == UsmVideoCodec.Unknown)
            {
                if (probe[0] == 'D' && probe[1] == 'K' && probe[2] == 'I' && probe[3] == 'F')
                {
                    Codec = UsmVideoCodec.Vp9InIvf;
                    if (got >= 24)
                    {
                        Width = BinaryPrimitives.ReadUInt16LittleEndian(probe.AsSpan(12));
                        Height = BinaryPrimitives.ReadUInt16LittleEndian(probe.AsSpan(14));

                        uint num = BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(16));
                        uint den = BinaryPrimitives.ReadUInt32LittleEndian(probe.AsSpan(20));
                        if (den > 0 && num / den is > 0 and < 240) FrameRate = num / den;
                    }
                }
                else if (got >= 8 && probe[0] == 0 && probe[1] == 0 && probe[2] == 1
                         && probe[3] == 0xB3)
                {
                    Codec = UsmVideoCodec.Mpeg1;
                    ParseMpeg1Dimensions(probe.AsSpan(4, 4));
                }
                else
                {
                    Codec = UsmVideoCodec.H264;
                }
            }

            switch (kind)
            {
                case UsmPayloadKind.VideoStart:
                case UsmPayloadKind.VideoFrame:
                    VideoFrameCount++;
                    break;

                case UsmPayloadKind.Other when Codec == UsmVideoCodec.Vp9InIvf:
                    // Subsequent IVF records start with a little-endian frame size, so they
                    // look like "other" to the byte classifier. Inside a VP9 stream every
                    // non-metadata @SFV block is a frame.
                    VideoFrameCount++;
                    break;
            }
        }
    }

    private void ParseMpeg1Dimensions(ReadOnlySpan<byte> four)
    {
        // Sequence header body: 12 bits width, 12 bits height, 4 bits aspect, 4 bits fps code.
        // 78 04 38 18  ->  0x78043818  ->  width 0x780 (1920), height 0x438 (1080)
        uint v = BinaryPrimitives.ReadUInt32BigEndian(four);
        Width = (int)(v >> 20);
        Height = (int)((v >> 8) & 0xFFF);
    }

    /// <summary>Classify a video payload by its leading bytes.</summary>
    public static UsmPayloadKind ClassifyPayload(ReadOnlySpan<byte> probe, int length)
    {
        if (length < 4) return UsmPayloadKind.Other;

        if (probe[0] == '@' && probe[1] == 'U' && probe[2] == 'T' && probe[3] == 'F')
            return UsmPayloadKind.Metadata;

        if (probe[0] == '#')
            return UsmPayloadKind.Marker;

        if (probe[0] == 'D' && probe[1] == 'K' && probe[2] == 'I' && probe[3] == 'F')
            return UsmPayloadKind.VideoStart;

        if (probe[0] == 0 && probe[1] == 0 && probe[2] == 1)
            return UsmPayloadKind.VideoStart;      // Annex-B start code

        if (probe[0] == 0 && probe[1] == 0 && probe[2] == 0 && probe[3] == 1)
            return UsmPayloadKind.VideoStart;

        if (probe[0] == 0x80 && probe[1] == 0x00)
            return UsmPayloadKind.Audio;

        return UsmPayloadKind.Other;
    }

    // ------------------------------------------------------------------
    //  Demux
    // ------------------------------------------------------------------

    /// <summary>
    /// Extract the elementary streams. <paramref name="videoOut"/> receives either an IVF file
    /// (VP9) or a bare Annex-B stream (MPEG-1 / H.264); <paramref name="audioOut"/> receives ADX.
    /// Returns the number of video frames written.
    /// </summary>
    public int Demux(Stream source, Stream? videoOut, Stream? audioOut)
    {
        if (Codec == UsmVideoCodec.Vp9InIvf)
            return DemuxVp9(source, videoOut);

        return DemuxAnnexB(source, videoOut, audioOut);
    }

    private int DemuxVp9(Stream source, Stream? videoOut)
    {
        // Locate the first block that starts with the DKIF magic; everything before it is
        // metadata. Its payload begins with the 32-byte IVF file header.
        int start = -1;
        for (int i = 0; i < Blocks.Count; i++)
        {
            if (Blocks[i].Tag != "@SFV") continue;
            if (PeekIsDkif(source, Blocks[i])) { start = i; break; }
        }
        if (start < 0) return 0;

        var first = Blocks[start];
        var header = new byte[32];
        source.Position = first.PayloadOffset;
        source.ReadExactly(header);

        long frameCountPos = -1;
        if (videoOut is not null)
        {
            videoOut.Write(header);
            frameCountPos = 32 - 8;             // frame count lives at offset 24 in DKIF
        }

        var buf = new byte[12];
        int frames = 0;

        for (int i = start; i < Blocks.Count; i++)
        {
            var b = Blocks[i];
            if (b.Tag != "@SFV") continue;

            long payloadOffset = b.PayloadOffset;
            long payloadLength = b.PayloadLength;

            if (i == start)
            {
                payloadOffset += 32;            // skip the IVF header we already wrote
                payloadLength -= 32;
            }
            if (payloadLength < 12) continue;

            source.Position = payloadOffset;
            source.ReadExactly(buf);

            uint frameSize = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            if (frameSize == 0 || 12L + frameSize > payloadLength) continue;

            if (videoOut is not null)
            {
                videoOut.Write(buf, 0, 12);
                CopyBytes(source, videoOut, frameSize);
            }
            frames++;
        }

        if (videoOut is not null && frameCountPos >= 0 && videoOut.CanSeek)
        {
            long end = videoOut.Position;
            videoOut.Position = frameCountPos;
            Span<byte> cnt = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(cnt, (uint)frames);
            videoOut.Write(cnt);
            videoOut.Position = end;
        }

        return frames;
    }

    private int DemuxAnnexB(Stream source, Stream? videoOut, Stream? audioOut)
    {
        int frames = 0;
        var probe = new byte[8];

        foreach (var b in Blocks)
        {
            if (b.PayloadLength <= 0) continue;

            int n = (int)Math.Min(probe.Length, b.PayloadLength);
            source.Position = b.PayloadOffset;
            int got = source.Read(probe, 0, n);

            if (b.Tag == "@SFA")
            {
                if (audioOut is null) continue;
                if (got >= 2 && probe[0] == 0x80 && probe[1] == 0x00)
                {
                    source.Position = b.PayloadOffset;
                    CopyBytes(source, audioOut, b.PayloadLength);
                }
                continue;
            }

            if (b.Tag != "@SFV") continue;

            var kind = ClassifyPayload(probe, got);
            if (kind is not (UsmPayloadKind.VideoStart or UsmPayloadKind.VideoFrame))
                continue;

            if (videoOut is not null)
            {
                source.Position = b.PayloadOffset;
                CopyBytes(source, videoOut, b.PayloadLength);
            }
            frames++;
        }

        return frames;
    }

    private static bool PeekIsDkif(Stream s, UsmBlock b)
    {
        if (b.PayloadLength < 4) return false;
        Span<byte> p = stackalloc byte[4];
        s.Position = b.PayloadOffset;
        return s.Read(p) == 4 && p.SequenceEqual("DKIF"u8);
    }

    private static void CopyBytes(Stream src, Stream dst, long count)
    {
        var buf = new byte[128 * 1024];
        long remaining = count;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buf.Length, remaining);
            int got = src.Read(buf, 0, want);
            if (got <= 0) break;
            dst.Write(buf, 0, got);
            remaining -= got;
        }
    }
}
