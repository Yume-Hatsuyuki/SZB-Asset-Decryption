using System.Collections.Concurrent;
using SzbTool.Core.Crypto;
using SzbTool.Core.Manifest;
using SzbTool.Core.UnityFs;

namespace SzbTool.Core.Restore;

public sealed class KindStat
{
    public int Count { get; set; }
    public long Bytes { get; set; }
}

public sealed class CensusReport
{
    public int Total { get; set; }
    public long TotalBytes { get; set; }
    public Dictionary<DatFileKind, KindStat> ByKind { get; } = new();

    /// <summary>Histogram of the low 6 bits of ArchiveFlags among bundles.</summary>
    public Dictionary<int, int> BlocksInfoCompression { get; } = new();

    /// <summary>Histogram of data_start values among bundles (only the most common ones survive).</summary>
    public Dictionary<long, int> DataStartHistogram { get; } = new();

    public int BundlesParsed { get; set; }
    public int BundlesLayoutOk { get; set; }
    public int BundlesStraddlingBlocksInfo { get; set; }
    public int BundlesWithoutManifestKey { get; set; }
    public int ManifestSizeMismatch { get; set; }
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Walks the whole <c>Persistent/dat</c> tree and produces a census.
/// Cheap enough to run before a restore, and it surfaces problems early: a bundle count that
/// does not match the manifest, or a layout check that fails, means something is wrong with
/// the version assumption.
/// </summary>
public sealed class CensusService
{
    private readonly string _datRoot;
    private readonly AssetManifest _manifest;
    private readonly byte[] _baseKey;

    public CensusService(string datRoot, AssetManifest manifest, byte[]? baseKey = null)
    {
        _datRoot = datRoot;
        _manifest = manifest;
        _baseKey = baseKey ?? [];
    }

    public CensusReport Run(IProgress<(int Done, int Total)>? progress = null,
                            CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var report = new CensusReport();

        var files = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(_datRoot))
            files.AddRange(Directory.EnumerateFiles(dir));

        report.Total = files.Count;

        var byKind = new ConcurrentDictionary<DatFileKind, KindStat>();
        var comp = new ConcurrentDictionary<int, int>();
        var dsHist = new ConcurrentDictionary<long, int>();
        int parsed = 0, layoutOk = 0, straddle = 0, noKey = 0, sizeMismatch = 0;
        long totalBytes = 0;
        int done = 0;

        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount),
            CancellationToken = ct,
        };

        try
        {
            Parallel.ForEach(files, po, path =>
            {
                long len;
                try { len = new FileInfo(path).Length; }
                catch { return; }

                Interlocked.Add(ref totalBytes, len);

                var kind = FileClassifier.Classify(path);
                byKind.AddOrUpdate(kind,
                    _ => new KindStat { Count = 1, Bytes = len },
                    (_, s) => { lock (s) { s.Count++; s.Bytes += len; } return s; });

                string hash = Path.GetFileName(path);
                var entry = _manifest.Find(hash);

                if (entry is null) Interlocked.Increment(ref noKey);
                else if (entry.Size > 0 && entry.Size != len) Interlocked.Increment(ref sizeMismatch);

                if (kind == DatFileKind.UnityBundle)
                {
                    try
                    {
                        using var fs = File.OpenRead(path);
                        var b = UnityFsBundle.Parse(fs, entry?.Key ?? 0, _baseKey);
                        Interlocked.Increment(ref parsed);

                        comp.AddOrUpdate((int)(b.Flags & 0x3F), 1, (_, v) => v + 1);

                        // Only keep a compact histogram: full values below 0x400, bucketed above.
                        long key = b.DataStart < 0x400 ? b.DataStart : (b.DataStart / 0x400) * 0x400;
                        dsHist.AddOrUpdate(key, 1, (_, v) => v + 1);

                        if (b.LayoutValid) Interlocked.Increment(ref layoutOk);
                        if (b.BlocksInfoWasEncrypted) Interlocked.Increment(ref straddle);
                    }
                    catch { /* counted as unparsed */ }
                }

                int d = Interlocked.Increment(ref done);
                if (d % 500 == 0) progress?.Report((d, files.Count));
            });
        }
        catch (OperationCanceledException) { }

        foreach (var kv in byKind) report.ByKind[kv.Key] = kv.Value;
        foreach (var kv in comp) report.BlocksInfoCompression[kv.Key] = kv.Value;
        foreach (var kv in dsHist) report.DataStartHistogram[kv.Key] = kv.Value;

        report.TotalBytes = totalBytes;
        report.BundlesParsed = parsed;
        report.BundlesLayoutOk = layoutOk;
        report.BundlesStraddlingBlocksInfo = straddle;
        report.BundlesWithoutManifestKey = noKey;
        report.ManifestSizeMismatch = sizeMismatch;
        report.Elapsed = sw.Elapsed;
        return report;
    }
}
