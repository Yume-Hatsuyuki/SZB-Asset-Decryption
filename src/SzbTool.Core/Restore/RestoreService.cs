using System.Collections.Concurrent;
using SzbTool.Core.Crypto;
using SzbTool.Core.IO;
using SzbTool.Core.Manifest;
using SzbTool.Core.UnityFs;

namespace SzbTool.Core.Restore;

public sealed class RestoreOptions
{
    public required string DatRoot { get; init; }
    public required string OutputRoot { get; init; }
    public required AssetManifest Manifest { get; init; }

    /// <summary>Shared base key. Empty means "not fetched yet" — <see cref="Run"/> refuses to start.</summary>
    public byte[] BaseKey { get; init; } = [];

    /// <summary>Worker count. IO-bound, so a little above core count helps.</summary>
    public int Parallelism { get; init; } = Math.Max(2, Environment.ProcessorCount);

    /// <summary>Re-verify every block after decrypting. Slower but catches bad keys.</summary>
    public bool VerifyBlocks { get; init; }

    /// <summary>Keep bundles whose key is missing, copied verbatim, instead of skipping them.</summary>
    public bool CopyWhenKeyMissing { get; init; }

    public CancellationToken Cancellation { get; init; } = CancellationToken.None;
}

public sealed class RestoreProgress
{
    public int Total { get; init; }
    public int Done { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long BytesWritten { get; set; }
    public string CurrentFile { get; set; } = string.Empty;

    public double Fraction => Total == 0 ? 0 : Math.Min(1.0, (double)Done / Total);
}

public sealed class RestoreResult
{
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public long BytesWritten { get; set; }
    public List<(string File, string Error)> Errors { get; } = new();
    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Restores UnityFX bundles by removing the XOR layer, writing a tree of plain <c>.ab</c>
/// files named after the real asset paths so AssetRipper can consume them directly.
///
/// <para>
/// Only the XOR layer is removed. Compressed blocks stay compressed — the output is a valid
/// UnityFS bundle in its own right, not an unpacked asset tree.
/// </para>
/// </summary>
public sealed class RestoreService
{
    private readonly RestoreOptions _opt;

    public RestoreService(RestoreOptions options) => _opt = options;

    public IReadOnlyList<string> EnumerateBundles()
    {
        var list = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(_opt.DatRoot))
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                string name = Path.GetFileName(f);
                var entry = _opt.Manifest.Find(name);

                // Cheap pre-filter: a bundle must have a manifest row with Category == All,
                // or be recognisable by magic when the manifest is incomplete.
                if (entry is not null && entry.Category != "All") continue;

                if (entry is null)
                {
                    if (FileClassifier.Classify(f) != DatFileKind.UnityBundle) continue;
                }

                list.Add(f);
            }
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    public RestoreResult Run(IProgress<RestoreProgress>? progress = null,
                             IReadOnlyList<string>? subset = null)
    {
        // Refuse to start rather than write 20 GiB of garbage: without a base key every
        // byte from 0x100 on stays encrypted, but the container still opens fine, so the
        // failure would only show up much later when someone loads the output.
        if (_opt.BaseKey.Length == 0)
            throw new InvalidOperationException(
                "基础密钥为空，无法解密。请先在「设置」里获取密钥，或用 --base 指定。");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var files = subset ?? EnumerateBundles();

        var result = new RestoreResult { Total = files.Count };
        var state = new RestoreProgress { Total = files.Count };
        var errors = new ConcurrentBag<(string, string)>();
        var usedPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(_opt.OutputRoot);

        int done = 0, failed = 0, skipped = 0;
        long bytes = 0;

        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = _opt.Parallelism,
            CancellationToken = _opt.Cancellation,
        };

        try
        {
            Parallel.ForEach(files, po, path =>
            {
                string name = Path.GetFileName(path);
                var entry = _opt.Manifest.Find(name);
                string rel = OutputPathBuilder.ForBundle(entry, name, usedPaths);
                string dst = Path.Combine(_opt.OutputRoot, rel);

                var (ok, err, written) = RestoreOne(path, dst, entry);
                int d = Interlocked.Increment(ref done);
                bytes += written;

                if (!ok)
                {
                    if (err == SkipReason) Interlocked.Increment(ref skipped);
                    else
                    {
                        Interlocked.Increment(ref failed);
                        errors.Add((name, err ?? "unknown"));
                    }
                }

                if (d % 64 == 0 || d == files.Count)
                {
                    state.Done = d;
                    state.BytesWritten = bytes;
                    state.CurrentFile = name;
                    progress?.Report(new RestoreProgress
                    {
                        Total = files.Count,
                        Done = d,
                        Failed = failed,
                        Skipped = skipped,
                        BytesWritten = bytes,
                        CurrentFile = name,
                    });
                }
            });
        }
        catch (OperationCanceledException) { /* user cancelled */ }

        result.Succeeded = done - failed - skipped;
        result.Failed = failed;
        result.Skipped = skipped;
        result.BytesWritten = bytes;
        result.Errors.AddRange(errors.OrderBy(e => e.Item1));
        result.Elapsed = sw.Elapsed;
        return result;
    }

    private const string SkipReason = "__skip__";

    private (bool Ok, string? Error, long Bytes) RestoreOne(string src, string dst, ManifestEntry? entry)
    {
        try
        {
            using var fs = File.OpenRead(src);

            if (!UnityFsBundle.LooksLikeBundle(fs))
                return (false, SkipReason, 0);

            long key = entry?.Key ?? 0;

            if (key == 0)
            {
                if (!_opt.CopyWhenKeyMissing) return (false, SkipReason, 0);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
                return (true, null, new FileInfo(src).Length);
            }

            UnityFsBundle bundle;
            try
            {
                bundle = UnityFsBundle.Parse(fs, key, _opt.BaseKey);
            }
            catch (Exception ex)
            {
                return (false, $"解析失败: {ex.Message}", 0);
            }

            if (!bundle.LayoutValid)
            {
                return (false,
                    $"布局校验失败: data_start {bundle.DataStart} + 数据 {bundle.TotalCompressedData} " +
                    $"≠ 文件长度 {fs.Length}", 0);
            }

            if (_opt.VerifyBlocks)
            {
                var (ok, failedAt, error) = bundle.VerifyBlocks(fs, key, _opt.BaseKey);
                if (!ok) return (false, $"块校验失败({failedAt}): {error}", 0);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            // Stream the file through the cipher. Only [0x100, EOF) changes.
            fs.Position = 0;
            using var output = File.Create(dst);
            var buffer = new byte[1 << 20];
            long pos = 0, written = 0;
            int n;
            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                XorKeystream.Apply(buffer.AsSpan(0, n), pos, _opt.BaseKey, key);
                output.Write(buffer, 0, n);
                pos += n;
                written += n;
            }

            return (true, null, written);
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {ex.Message}", 0);
        }
    }
}

/// <summary>Builds output paths from manifest names, keeping the tree readable and safe on Windows.</summary>
public static class OutputPathBuilder
{
    private static readonly char[] Invalid =
        Path.GetInvalidPathChars().Concat(['<', '>', ':', '"', '|', '?', '*']).Distinct().ToArray();

    public static string ForBundle(ManifestEntry? entry, string hash,
                                   ConcurrentDictionary<string, byte>? used = null)
    {
        string basePath = entry?.Name ?? $"_unnamed/{hash}";
        return Normalise(basePath, ".ab", used);
    }

    public static string Normalise(string assetPath, string extension,
                                   ConcurrentDictionary<string, byte>? used = null)
    {
        var parts = assetPath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Sanitise)
            .ToArray();

        if (parts.Length == 0) parts = ["_unnamed"];

        string rel = Path.Combine(parts) + extension;

        if (used is not null && !used.TryAdd(rel, 0))
        {
            // Same asset name from a different category — disambiguate rather than overwrite.
            string dir = Path.GetDirectoryName(rel) ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(rel);
            int i = 2;
            do
            {
                rel = Path.Combine(dir, $"{stem}_{i}{extension}");
                i++;
            }
            while (!used.TryAdd(rel, 0));
        }

        return rel;
    }

    private static string Sanitise(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment)) return "_";
        var chars = segment.Select(c => Invalid.Contains(c) ? '_' : c).ToArray();
        var s = new string(chars).Trim().TrimEnd('.');
        if (s.Length == 0) return "_";
        if (s.Length > 180) s = s[..180];
        return s;
    }
}
