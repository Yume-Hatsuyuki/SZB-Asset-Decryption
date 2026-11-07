using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using SzbAssetDecryption.Core.External;
using SzbAssetDecryption.Core.Manifest;
using SzbAssetDecryption.Core.Media;
using SzbAssetDecryption.Core.Video;

namespace SzbAssetDecryption.Core.Restore;

public sealed class VideoOptions
{
    public required string DatRoot { get; init; }
    public required string OutputRoot { get; init; }
    public required AssetManifest Manifest { get; init; }

    public VideoContainer Container { get; init; } = VideoContainer.Mp4H264;

    /// <summary>When several quality tiers exist for the same movie, keep only the best one.</summary>
    public bool DeduplicateQualityTiers { get; init; } = true;

    /// <summary>Delete the intermediate .ivf / .m1v / .adx files.</summary>
    public bool KeepIntermediates { get; init; }

    public int Parallelism { get; init; } = Math.Max(1, Environment.ProcessorCount / 3);
    public CancellationToken Cancellation { get; init; } = CancellationToken.None;
}

public sealed class VideoProgress
{
    public int Total { get; init; }
    public int Done { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
}

public sealed class VideoResult
{
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int SkippedDuplicates { get; set; }
    public TimeSpan Elapsed { get; set; }
    public List<(string File, string Error)> Errors { get; } = new();
}

/// <summary>
/// Demuxes CRI USM files and hands the elementary streams to ffmpeg.
///
/// <para>
/// Nothing here is encrypted; the work is purely container surgery:
/// VP9 needs its per-frame IVF headers rebuilt (block payloads carry trailing padding),
/// MPEG-1 is a plain concatenation, and the ADX track is a plain concatenation too.
/// </para>
/// </summary>
public sealed class VideoService
{
    private static readonly Regex TierPattern =
        new(@"^movie/(High|Middle|Low)/", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly VideoOptions _opt;

    public VideoService(VideoOptions options) => _opt = options;

    public IReadOnlyList<string> EnumerateVideos(out int skippedDuplicates)
    {
        skippedDuplicates = 0;

        var all = new List<(string Path, string Name, string Tier, string Key)>();

        foreach (var dir in Directory.EnumerateDirectories(_opt.DatRoot))
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                string hash = Path.GetFileName(f);
                var entry = _opt.Manifest.Find(hash);

                if (entry is not null)
                {
                    if (entry.Category != "Movie") continue;
                }
                else if (FileClassifier.Classify(f) != DatFileKind.CridVideo)
                {
                    continue;
                }

                string name = entry?.Name ?? $"_unnamed/{hash}.usm";
                var m = TierPattern.Match(name);
                string tier = m.Success ? m.Groups[1].Value : "High";
                string key = m.Success ? name[m.Length..] : name;

                all.Add((f, name, tier, key));
            }
        }

        if (!_opt.DeduplicateQualityTiers)
        {
            var flat = all.Select(x => x.Path).ToList();
            flat.Sort(StringComparer.Ordinal);
            return flat;
        }

        int rank(string tier) => tier.ToLowerInvariant() switch
        {
            "high" => 0,
            "middle" => 1,
            "low" => 2,
            _ => 3,
        };

        var best = all
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(x => rank(x.Tier)).First())
            .ToList();

        skippedDuplicates = all.Count - best.Count;

        var result = best.Select(x => x.Path).ToList();
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    public VideoResult Run(IProgress<VideoProgress>? progress = null,
                           IReadOnlyList<string>? subset = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var files = subset ?? EnumerateVideos(out _);
        int skipped = subset is null ? 0 : 0;

        var result = new VideoResult { Total = files.Count, SkippedDuplicates = skipped };
        var errors = new ConcurrentBag<(string, string)>();

        string tempRoot = Path.Combine(Path.GetTempPath(), "szb-asset-decryption-video-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);

        int done = 0, ok = 0, fail = 0;

        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = _opt.Parallelism,
            CancellationToken = _opt.Cancellation,
        };

        try
        {
            Parallel.ForEach(files, po, path =>
            {
                string hash = Path.GetFileName(path);
                var entry = _opt.Manifest.Find(hash);
                string name = entry?.Name ?? $"_unnamed/{hash}.usm";

                string relNoExt = Path.ChangeExtension(
                    OutputPathBuilder.Normalise(name, string.Empty), null);
                string outDir = Path.Combine(_opt.OutputRoot, Path.GetDirectoryName(relNoExt) ?? "");
                string stem = Path.GetFileName(relNoExt);

                Directory.CreateDirectory(outDir);

                string tempDir = Path.Combine(tempRoot, hash);
                Directory.CreateDirectory(tempDir);

                try
                {
                    var r = ConvertOne(path, tempDir, outDir, stem);
                    if (r.Ok) Interlocked.Increment(ref ok);
                    else
                    {
                        Interlocked.Increment(ref fail);
                        errors.Add((name, r.Error ?? "unknown"));
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref fail);
                    errors.Add((name, $"{ex.GetType().Name}: {ex.Message}"));
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }

                int d = Interlocked.Increment(ref done);
                progress?.Report(new VideoProgress
                {
                    Total = files.Count,
                    Done = d,
                    Succeeded = ok,
                    Failed = fail,
                    CurrentFile = name,
                });
            });
        }
        catch (OperationCanceledException) { }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }

        result.Succeeded = ok;
        result.Failed = fail;
        result.Errors.AddRange(errors.OrderBy(e => e.Item1).Take(500));
        result.Elapsed = sw.Elapsed;
        return result;
    }

    private (bool Ok, string? Error) ConvertOne(string src, string tempDir, string outDir, string stem)
    {
        UsmFile usm;
        try
        {
            usm = UsmFile.Open(src);
        }
        catch (Exception ex)
        {
            return (false, $"解析 USM 失败: {ex.Message}");
        }

        if (usm.Codec == UsmVideoCodec.Unknown)
            return (false, "未能识别视频编码");

        string ext = usm.Codec == UsmVideoCodec.Vp9InIvf ? ".ivf" : ".m1v";
        string videoPath = Path.Combine(tempDir, "video" + ext);
        string audioPath = Path.Combine(tempDir, "audio.adx");

        using (var input = File.OpenRead(src))
        using (var vo = File.Create(videoPath))
        {
            Stream? ao = usm.HasAudio ? File.Create(audioPath) : null;
            try
            {
                usm.Demux(input, vo, ao);
            }
            finally
            {
                ao?.Dispose();
            }
        }

        if (new FileInfo(videoPath).Length == 0)
            return (false, "未提取到视频帧");

        string outExt = _opt.Container switch
        {
            VideoContainer.MkvCopy => ".mkv",
            _ => ".mp4",
        };

        string output = Path.Combine(outDir, stem + outExt);

        string? forceFormat = usm.Codec switch
        {
            UsmVideoCodec.Mpeg1 => "mpegvideo",
            UsmVideoCodec.H264 => "h264",
            _ => null,                       // IVF carries its own header
        };

        string? audio = File.Exists(audioPath) && new FileInfo(audioPath).Length > 0
            ? audioPath : null;

        var r = MediaConverter.VideoToContainer(
            videoPath, output, _opt.Container,
            usm.FrameRate > 0 ? usm.FrameRate : 30,
            audio, forceFormat);

        if (!r.Success && !File.Exists(output))
            return (false, $"ffmpeg 失败: {FirstLine(r.StdErr)}");

        return (true, null);
    }

    private static string FirstLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
}
