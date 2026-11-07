using SzbAssetDecryption.Core.Crypto;
using SzbAssetDecryption.Core.IO;

namespace SzbAssetDecryption.Core.UnityFs;

/// <summary>One entry of the block table.</summary>
public sealed class BlockInfo
{
    public uint UncompressedSize { get; init; }
    public uint CompressedSize { get; init; }
    public ushort Flags { get; init; }

    /// <summary>Compression method for this block (low 6 bits of <see cref="Flags"/>).</summary>
    public int CompressionType => Flags & 0x3F;

    public override string ToString() =>
        $"{UncompressedSize}/{CompressedSize} (type {CompressionType})";
}

/// <summary>One entry of the directory table.</summary>
public sealed class NodeInfo
{
    /// <summary>Offset relative to the data section, not to the file.</summary>
    public long Offset { get; init; }
    public long Size { get; init; }
    public uint Flags { get; init; }
    public string Path { get; init; } = string.Empty;

    public override string ToString() => $"{Path} @{Offset}+{Size}";
}

/// <summary>
/// A parsed UnityFS bundle.
///
/// <para>
/// Parsing deliberately does NOT require the key: the header and (usually) the block table
/// are plaintext, which is exactly why stock tools report a valid bundle before failing on
/// the data. A key is only needed for the ~2 300 bundles whose block table straddles 0x100.
/// </para>
/// </summary>
public sealed class UnityFsBundle
{
    // 完整的 flags 位表，和 docs/01_UNITYFS_FORMAT.md 一一对应。
    // 本游戏的所有包都是 0x243（Compression=3 | BlocksInfoAtEnd | AlignBlocksInfo），
    // 0x40 与 0x100 在这里不会被命中，留着是为了这张表是完整的——
    // 少了它们，下一个人看到 0x243 拆不出含义。
    public const uint ArchiveCompressionMask = 0x3F;
    public const uint ArchiveBlocksAndDirCombined = 0x40;
    public const uint ArchiveBlocksInfoAtEnd = 0x80;
    public const uint ArchiveOldWebPlugin = 0x100;
    public const uint ArchiveAlignBlocksInfo = 0x200;

    public string Magic { get; private set; } = string.Empty;
    public int Version { get; private set; }
    public string UnityVersion { get; private set; } = string.Empty;
    public string UnityRevision { get; private set; } = string.Empty;
    public long DeclaredFileSize { get; private set; }
    public uint CompressedBlocksInfoSize { get; private set; }
    public uint UncompressedBlocksInfoSize { get; private set; }
    public uint Flags { get; private set; }

    public long BlocksInfoOffset { get; private set; }
    public long DataStart { get; private set; }

    public List<BlockInfo> Blocks { get; } = new();
    public List<NodeInfo> Nodes { get; } = new();

    /// <summary>True when the block table itself had to be decrypted before LZ4.</summary>
    public bool BlocksInfoWasEncrypted { get; private set; }

    /// <summary>
    /// True when <c>DataStart + Σ(compressed sizes) == FileSize</c>.
    /// This is the single most useful sanity check: it validates header length, alignment,
    /// block table decompression and byte counts all at once.
    /// </summary>
    public bool LayoutValid { get; private set; }

    public long TotalCompressedData => Blocks.Sum(b => (long)b.CompressedSize);
    public long TotalUncompressedData => Blocks.Sum(b => (long)b.UncompressedSize);

    public static bool LooksLikeBundle(Stream stream)
    {
        if (stream.Length < 16) return false;
        long pos = stream.Position;
        try
        {
            stream.Position = 0;
            Span<byte> b = stackalloc byte[8];
            int read = stream.Read(b);
            return read == 8 && b.SequenceEqual("UnityFS\0"u8);
        }
        finally { stream.Position = pos; }
    }

    /// <summary>
    /// Parse the bundle. Pass <paramref name="key"/> when the block table may straddle
    /// the 0x100 boundary; passing 0 still works for the common case.
    /// </summary>
    public static UnityFsBundle Parse(Stream stream, long key = 0, ReadOnlySpan<byte> baseKey = default)
    {
        long fileSize = stream.Length;

        int headLen = (int)Math.Min(fileSize, 0x10000);
        var head = new byte[headLen];
        stream.Position = 0;
        stream.ReadExactly(head);

        var r = new SpanReader(head);
        var bundle = new UnityFsBundle
        {
            Magic = r.CString(),
            Version = (int)r.U32BE(),
            UnityVersion = r.CString(),
            UnityRevision = r.CString(),
            DeclaredFileSize = r.I64BE(),
            CompressedBlocksInfoSize = r.U32BE(),
            UncompressedBlocksInfoSize = r.U32BE(),
            Flags = r.U32BE(),
        };

        if (bundle.Magic != "UnityFS")
            throw new InvalidDataException($"不是 UnityFS 包（magic = '{bundle.Magic}'）");

        uint ciSize = bundle.CompressedBlocksInfoSize;
        uint uiSize = bundle.UncompressedBlocksInfoSize;

        long infoOffset;
        if ((bundle.Flags & ArchiveBlocksInfoAtEnd) != 0)
        {
            infoOffset = fileSize - ciSize;
        }
        else
        {
            if ((bundle.Flags & ArchiveAlignBlocksInfo) != 0)
                r.Align(16);
            infoOffset = r.Position;
        }

        bundle.BlocksInfoOffset = infoOffset;

        // ---- read the (possibly compressed) block table ----
        byte[] raw;
        if (infoOffset >= 0 && infoOffset + ciSize <= headLen)
        {
            raw = head.AsSpan((int)infoOffset, (int)ciSize).ToArray();
        }
        else
        {
            raw = new byte[ciSize];
            stream.Position = infoOffset;
            stream.ReadExactly(raw);
        }

        // The table can straddle the 0x100 boundary; its tail is then ciphertext.
        if (key != 0 && baseKey.Length > 0 &&
            infoOffset < XorKeystream.HeaderSkip &&
            infoOffset + ciSize > XorKeystream.HeaderSkip)
        {
            XorKeystream.Apply(raw, infoOffset, baseKey, key);
            bundle.BlocksInfoWasEncrypted = true;
        }

        int compType = (int)(bundle.Flags & ArchiveCompressionMask);
        byte[] info = compType switch
        {
            0 => raw,
            2 or 3 => Lz4Block.Decompress(raw, (int)uiSize),
            1 => throw new NotSupportedException("blocksInfo 使用 LZMA 压缩，本工具未实现"),
            _ => throw new NotSupportedException($"未知的 blocksInfo 压缩类型 {compType}"),
        };

        if (info.Length < 20)
            throw new InvalidDataException(
                $"blocksInfo 解压结果只有 {info.Length} 字节（期望 {uiSize}），密钥可能不正确");

        // ---- block table ----
        var ir = new SpanReader(info);
        ir.Skip(16);                        // 16-byte hash, unused
        int blockCount = ir.I32BE();
        if (blockCount < 0 || blockCount > 1_000_000)
            throw new InvalidDataException($"块数量异常：{blockCount}");

        bundle.Blocks.Capacity = blockCount;
        for (int i = 0; i < blockCount; i++)
        {
            bundle.Blocks.Add(new BlockInfo
            {
                UncompressedSize = ir.U32BE(),
                CompressedSize = ir.U32BE(),
                Flags = ir.U16BE(),
            });
        }

        // ---- directory table ----
        int nodeCount = ir.I32BE();
        if (nodeCount < 0 || nodeCount > 1_000_000)
            throw new InvalidDataException($"节点数量异常：{nodeCount}");

        bundle.Nodes.Capacity = nodeCount;
        for (int i = 0; i < nodeCount; i++)
        {
            bundle.Nodes.Add(new NodeInfo
            {
                Offset = ir.I64BE(),
                Size = ir.I64BE(),
                Flags = ir.U32BE(),
                Path = ir.CString(),
            });
        }

        bundle.DataStart = Align16(infoOffset + ciSize);
        bundle.LayoutValid = bundle.DataStart + bundle.TotalCompressedData == fileSize;

        return bundle;
    }

    public static long Align16(long v) => (v + 15) & ~15L;

    /// <summary>Find the file offset where block <paramref name="index"/> begins.</summary>
    public long BlockFileOffset(int index)
    {
        long off = DataStart;
        for (int i = 0; i < index; i++) off += Blocks[i].CompressedSize;
        return off;
    }

    /// <summary>
    /// Extract and decompress the payload of one block. <paramref name="key"/> is required
    /// unless the block happens to sit entirely inside the plaintext prefix.
    /// </summary>
    public byte[] ReadBlock(Stream stream, int index, long key, ReadOnlySpan<byte> baseKey)
    {
        var b = Blocks[index];
        var buf = new byte[b.CompressedSize];
        stream.Position = BlockFileOffset(index);
        stream.ReadExactly(buf);

        if (key != 0 && baseKey.Length > 0)
            XorKeystream.Apply(buf, BlockFileOffset(index), baseKey, key);

        return b.CompressionType switch
        {
            0 => buf,
            2 or 3 => Lz4Block.Decompress(buf, (int)b.UncompressedSize),
            _ => throw new NotSupportedException($"不支持的块压缩类型 {b.CompressionType}"),
        };
    }

    /// <summary>
    /// Verify every block decompresses to exactly its declared size.
    /// Returns (ok, failedCount, firstError).
    /// </summary>
    public (bool Ok, int Failed, string? Error) VerifyBlocks(
        Stream stream, long key, ReadOnlySpan<byte> baseKey)
    {
        for (int i = 0; i < Blocks.Count; i++)
        {
            try
            {
                var data = ReadBlock(stream, i, key, baseKey);
                if (data.Length != Blocks[i].UncompressedSize)
                    return (false, i + 1,
                        $"块 {i} 解压长度 {data.Length}，期望 {Blocks[i].UncompressedSize}");
            }
            catch (Exception ex)
            {
                return (false, i + 1, $"块 {i}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return (true, 0, null);
    }

    /// <summary>Reassemble one node's bytes from the block stream.</summary>
    public byte[] ReadNode(Stream stream, NodeInfo node, long key, ReadOnlySpan<byte> baseKey)
    {
        var output = new byte[node.Size];
        long nodeEnd = node.Offset + node.Size;
        long cursor = 0;

        for (int i = 0; i < Blocks.Count; i++)
        {
            long blockStart = cursor;
            long blockEnd = cursor + Blocks[i].UncompressedSize;
            cursor = blockEnd;

            if (blockEnd <= node.Offset) continue;
            if (blockStart >= nodeEnd) break;

            var data = ReadBlock(stream, i, key, baseKey)
                       ?? throw new InvalidDataException("块解压失败");

            long from = Math.Max(node.Offset, blockStart);
            long to = Math.Min(nodeEnd, blockEnd);
            int count = (int)(to - from);
            if (count <= 0) continue;

            Array.Copy(data, (int)(from - blockStart), output, (int)(from - node.Offset), count);
        }

        return output;
    }
}
