using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SzbAssetDecryption.Core.Crypto;
using SzbAssetDecryption.Core.IO;
using SzbAssetDecryption.Core.Manifest;
using SzbAssetDecryption.Core.UnityFs;

namespace SzbAssetDecryption.Core.Keys;

/// <summary>
/// One bundle reduced to the facts needed to solve for the base key.
///
/// <para>
/// What is deliberately <em>absent</em> matters as much as what is present: the compressed
/// block sizes and the blocksInfo size are not part of the identity, because they are
/// exactly what differs between two archives holding the same content. Including them would
/// prevent the very pairs the solver depends on.
/// </para>
/// </summary>
public sealed class BundleProbe
{
    public required string Path { get; init; }
    public required long FileSize { get; init; }
    public required long DataStart { get; init; }

    /// <summary>The per-file key from the manifest.</summary>
    public required long Key { get; init; }

    /// <summary>Hash of everything describing the content, independent of compression.</summary>
    public required string ContentId { get; init; }

    public required int BlockCount { get; init; }
    public required long TotalUncompressed { get; init; }

    /// <summary>Compression type of block 0 (0 stored, 2/3 LZ4).</summary>
    public required int FirstBlockType { get; init; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public override string ToString() =>
        $"{FileName} ds=0x{DataStart:X} key={Key} blocks={BlockCount}";
}

public enum ProbeScanOutcome
{
    Ok,
    /// <summary>Not a UnityFS container (AKPK audio, USM video, the manifest itself…).</summary>
    NotABundle,
    /// <summary>The block table straddles 0x100, so it cannot be read without the answer.</summary>
    Straddling,
    /// <summary>Header present but the block table would not decompress.</summary>
    Broken,
}

public sealed class ProbeScanReport
{
    public int FilesSeen { get; internal set; }
    public int NoKey { get; internal set; }
    public int NotABundle { get; internal set; }
    public int Straddling { get; internal set; }
    public int Broken { get; internal set; }
    public int Usable => Probes.Count;

    public List<BundleProbe> Probes { get; } = new();
}

/// <summary>
/// Walks the game's <c>dat</c> tree and turns every readable bundle into a
/// <see cref="BundleProbe"/>. Read-only; never writes anything.
/// </summary>
public static class BundleProbeCollector
{
    private const int HeaderBuffer = 512;

    public static ProbeScanReport Collect(
        string datRoot,
        AssetManifest manifest,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var report = new ProbeScanReport();
        var bag = new ConcurrentBag<BundleProbe>();

        int seen = 0, noKey = 0, notBundle = 0, straddling = 0, broken = 0;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount),
            CancellationToken = ct,
        };

        Parallel.ForEach(Directory.EnumerateFiles(datRoot, "*", SearchOption.AllDirectories),
                         options, path =>
        {
            long size;
            try { size = new FileInfo(path).Length; }
            catch { return; }

            if (size < XorKeystream.HeaderSkip + 64) return;

            int n = Interlocked.Increment(ref seen);
            if (n % 2000 == 0) progress?.Report(n);

            var entry = manifest.Find(Path.GetFileName(path));
            if (entry is null)
            {
                Interlocked.Increment(ref noKey);
                return;
            }

            var (outcome, probe) = Inspect(path, size, entry.Key);
            switch (outcome)
            {
                case ProbeScanOutcome.Ok:
                    bag.Add(probe!);
                    break;
                case ProbeScanOutcome.NotABundle:
                    Interlocked.Increment(ref notBundle);
                    break;
                case ProbeScanOutcome.Straddling:
                    Interlocked.Increment(ref straddling);
                    break;
                default:
                    Interlocked.Increment(ref broken);
                    break;
            }
        });

        report.FilesSeen = seen;
        report.NoKey = noKey;
        report.NotABundle = notBundle;
        report.Straddling = straddling;
        report.Broken = broken;
        report.Probes.AddRange(bag);
        return report;
    }

    private static (ProbeScanOutcome, BundleProbe?) Inspect(string path, long fileSize, long key)
    {
        try
        {
            using var fs = File.OpenRead(path);

            // Read the header once, by hand, purely to find out whether the block table
            // straddles 0x100. If it does, the tail is ciphertext and we cannot read it
            // without the answer we are trying to compute.
            Span<byte> head = stackalloc byte[HeaderBuffer];
            int n = fs.Read(head);
            if (n < 32 || !head[..8].SequenceEqual("UnityFS\0"u8))
                return (ProbeScanOutcome.NotABundle, null);

            var hr = new SpanReader(head[..n]);
            hr.CString();                       // magic
            hr.U32BE();                         // version
            hr.CString();                       // unityVersion
            hr.CString();                       // unityRevision
            hr.I64BE();                         // declared size
            uint ciSize = hr.U32BE();
            hr.U32BE();                         // uiSize
            uint flags = hr.U32BE();
            if ((flags & UnityFsBundle.ArchiveAlignBlocksInfo) != 0) hr.Align(16);

            long infoOffset = (flags & UnityFsBundle.ArchiveBlocksInfoAtEnd) != 0
                ? fileSize - ciSize
                : hr.Position;

            if (infoOffset < XorKeystream.HeaderSkip &&
                infoOffset + ciSize > XorKeystream.HeaderSkip)
                return (ProbeScanOutcome.Straddling, null);

            fs.Position = 0;
            var bundle = UnityFsBundle.Parse(fs, 0, default);

            if (bundle.Blocks.Count == 0 || bundle.Nodes.Count == 0)
                return (ProbeScanOutcome.Broken, null);

            return (ProbeScanOutcome.Ok, new BundleProbe
            {
                Path = path,
                FileSize = fileSize,
                DataStart = bundle.DataStart,
                Key = key,
                ContentId = ContentIdOf(bundle),
                BlockCount = bundle.Blocks.Count,
                TotalUncompressed = bundle.TotalUncompressedData,
                FirstBlockType = bundle.Blocks[0].CompressionType,
            });
        }
        catch
        {
            return (ProbeScanOutcome.Broken, null);
        }
    }

    /// <summary>
    /// Fingerprint of "what is in this archive", ignoring how it was compressed.
    ///
    /// <para>
    /// Note the fields that are missing on purpose: compressed block sizes, blocksInfo size,
    /// data start, and the file name. Two copies of the same resource repacked with a
    /// different block layout differ in all of those.
    /// </para>
    /// </summary>
    private static string ContentIdOf(UnityFsBundle bundle)
    {
        var sb = new StringBuilder(8192);
        sb.Append(bundle.Version).Append('|')
          .Append(bundle.UnityVersion).Append('|')
          .Append(bundle.UnityRevision).Append('|')
          .Append(bundle.TotalUncompressedData).Append('|');

        foreach (var node in bundle.Nodes)
            sb.Append(node.Offset).Append(':').Append(node.Size).Append(';');

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()), hash);
        return Convert.ToHexString(hash[..8]);
    }
}
