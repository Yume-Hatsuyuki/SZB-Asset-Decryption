using System.Diagnostics;
using SzbAssetDecryption.Core.Manifest;

namespace SzbAssetDecryption.Core.Keys;

public sealed class BaseKeyDerivationReport
{
    public byte[]? BaseKey { get; internal set; }
    public string? Failure { get; internal set; }

    public string? ManifestPath { get; internal set; }
    public int ManifestEntries { get; internal set; }

    public int FilesSeen { get; internal set; }
    public int Probes { get; internal set; }
    public int PairsUsable { get; internal set; }
    public int Windows { get; internal set; }
    public int Rank { get; internal set; }
    public int Nullity { get; internal set; }
    public int CandidatesTried { get; internal set; }
    public TimeSpan Elapsed { get; internal set; }

    /// <summary>True when this came from a previous derivation rather than a fresh scan.</summary>
    public bool FromCache { get; internal set; }
    public DateTimeOffset? CachedAt { get; internal set; }

    /// <summary>Human-readable account of what happened, in order.</summary>
    public List<string> Steps { get; } = new();

    public bool Success => BaseKey is { Length: > 0 };
    public int Period => BaseKey is null ? 0 : BaseKey.Length * 8;
}

/// <summary>
/// The one entry point the rest of the app should use: given a <c>dat</c> directory,
/// work out the base key.
///
/// <para>
/// Nothing here touches the network, and nothing here reads a stored key. The only inputs
/// are the game's own files and the manifest that ships inside them.
/// </para>
/// </summary>
public static class BaseKeyDerivation
{
    public static BaseKeyDerivationReport Run(
        string datRoot,
        AssetManifest? manifest = null,
        string? manifestPath = null,
        IProgress<string>? log = null,
        IProgress<int>? progress = null,
        string? cachePath = null,
        bool forceRederive = false,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var report = new BaseKeyDerivationReport();

        void Step(string text)
        {
            report.Steps.Add(text);
            log?.Report(text);
        }

        if (!Directory.Exists(datRoot))
        {
            report.Failure = $"数据目录不存在：{datRoot}";
            report.Elapsed = sw.Elapsed;
            return report;
        }

        // ---- 1. reuse the last derivation if nothing has changed -----------
        // Tried before locating the manifest, because the cache records where the manifest
        // was; a hit then costs milliseconds instead of walking a thousand directories.
        if (cachePath is not null && !forceRederive && manifest is null && manifestPath is null)
        {
            if (TryUseCache(datRoot, cachePath, report, Step, sw)) return report;
        }

        // ---- 2. locate the manifest that ships with the install ------------
        if (manifest is null)
        {
            Step("在 dat 里找游戏自带的清单…");
            manifestPath ??= AssetManifest.LocateLocal(datRoot);

            if (manifestPath is null)
            {
                report.Failure =
                    "没找到本地清单。密钥是从清单里的 key 和本地包数据反推出来的，" +
                    "没有清单就没有输入。";
                report.Elapsed = sw.Elapsed;
                return report;
            }

            try
            {
                manifest = AssetManifest.Load(manifestPath);
            }
            catch (Exception ex)
            {
                report.Failure = $"清单解析失败：{ex.Message}";
                report.Elapsed = sw.Elapsed;
                return report;
            }
        }

        report.ManifestPath = manifestPath;
        report.ManifestEntries = manifest.Count;

        // When the caller supplied a manifest we only reach here, so give the cache a
        // second chance with the path we now know.
        if (cachePath is not null && !forceRederive && manifestPath is not null)
        {
            if (TryUseCache(datRoot, cachePath, report, Step, sw)) return report;
        }

        Step($"清单 {manifest.Count:N0} 条记录" +
             (manifestPath is null ? "" : $"（{Path.GetFileName(manifestPath)}）"));

        // ---- 3. reduce every bundle to a probe ----------------------------
        Step("扫描包（只读文件头，不写任何东西）…");
        ProbeScanReport scan;
        try
        {
            scan = BundleProbeCollector.Collect(datRoot, manifest, progress, ct);
        }
        catch (OperationCanceledException)
        {
            report.Failure = "已取消。";
            report.Elapsed = sw.Elapsed;
            return report;
        }
        catch (Exception ex)
        {
            report.Failure = $"扫描失败：{ex.Message}";
            report.Elapsed = sw.Elapsed;
            return report;
        }

        report.FilesSeen = scan.FilesSeen;
        report.Probes = scan.Usable;
        Step($"看过 {scan.FilesSeen:N0} 个文件，可用于求解 {scan.Usable:N0} 个" +
             $"（非 UnityFS {scan.NotABundle:N0}，无 key {scan.NoKey:N0}，" +
             $"块表跨界 {scan.Straddling:N0}，损坏 {scan.Broken:N0}）");

        if (scan.Usable < 4)
        {
            report.Failure = $"可用于求解的包只有 {scan.Usable} 个，不够。";
            report.Elapsed = sw.Elapsed;
            return report;
        }

        // ---- 4. solve ------------------------------------------------------
        Step("解方程…");
        var solved = BaseKeySolver.Solve(scan.Probes, log, ct);

        report.PairsUsable = solved.PairsUsable;
        report.Windows = solved.Windows;
        report.Rank = solved.Rank;
        report.Nullity = solved.Nullity;
        report.CandidatesTried = solved.CandidatesTried;
        report.BaseKey = solved.BaseKey;
        report.Failure = solved.Failure;
        report.Elapsed = sw.Elapsed;

        if (!solved.Success) return report;

        Step($"得到 {solved.BaseKey!.Length} 字节密钥，周期 {report.Period}，" +
             $"共试了 {solved.CandidatesTried} 个候选");

        // ---- 5. remember it, tied to this manifest ------------------------
        if (cachePath is not null && manifestPath is not null)
        {
            var info = new FileInfo(manifestPath);
            BaseKeyCache.Save(cachePath, new BaseKeyCacheEntry
            {
                DatRoot = datRoot,
                ManifestPath = manifestPath,
                ManifestSize = info.Length,
                ManifestWriteTicks = info.LastWriteTimeUtc.Ticks,
                ManifestEntries = manifest.Count,
                BaseKeyHex = Convert.ToHexString(solved.BaseKey!).ToLowerInvariant(),
                Probes = scan.Usable,
                Pairs = solved.PairsUsable,
                Windows = solved.Windows,
                Rank = solved.Rank,
                Nullity = solved.Nullity,
                DerivedAt = DateTimeOffset.Now,
            });
        }

        return report;
    }

    /// <summary>Adopt a previous derivation into the report. Returns false on a cache miss.</summary>
    private static bool TryUseCache(
        string datRoot,
        string cachePath,
        BaseKeyDerivationReport report,
        Action<string> step,
        Stopwatch sw)
    {
        var cached = BaseKeyCache.TryLoad(datRoot, cachePath);
        if (cached is null) return false;

        report.BaseKey = cached.Key;
        report.FromCache = true;
        report.CachedAt = cached.DerivedAt;
        report.ManifestPath = cached.ManifestPath;
        report.ManifestEntries = cached.ManifestEntries;
        report.Probes = cached.Probes;
        report.PairsUsable = cached.Pairs;
        report.Windows = cached.Windows;
        report.Rank = cached.Rank;
        report.Nullity = cached.Nullity;
        report.Elapsed = sw.Elapsed;

        step($"沿用上次的推导结果（{cached.DerivedAt:yyyy-MM-dd HH:mm}，" +
             $"{cached.Probes:N0} 个包 · {cached.Pairs} 对同明文 · 秩 {cached.Rank}）。" +
             "要重算加 --rederive。");
        return true;
    }
}
