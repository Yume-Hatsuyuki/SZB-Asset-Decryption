using System.Collections.Concurrent;
using SzbTool.Core.Audio;
using SzbTool.Core.External;
using SzbTool.Core.Manifest;
using SzbTool.Core.Media;

namespace SzbTool.Core.Restore;

public sealed class AudioOptions
{
    public required string DatRoot { get; init; }
    public required string OutputRoot { get; init; }
    public required AssetManifest Manifest { get; init; }

    /// <summary>Decode each extracted .wem into .wav with vgmstream.</summary>
    public bool DecodeToWav { get; init; } = true;

    /// <summary>Keep the intermediate .wem files.</summary>
    public bool KeepWem { get; init; }

    /// <summary>Honour loop points (two loops plus fade) instead of playing once.</summary>
    public bool HonourLoop { get; init; }

    public int Parallelism { get; init; } = Math.Max(2, Environment.ProcessorCount / 2);
    public CancellationToken Cancellation { get; init; } = CancellationToken.None;
}

public sealed class AudioProgress
{
    public int PackagesTotal { get; init; }
    public int PackagesDone { get; set; }
    public int WemsExtracted { get; set; }
    public int WavConverted { get; set; }
    public int WavFailed { get; set; }
    public string CurrentPackage { get; set; } = string.Empty;
}

public sealed class AudioResult
{
    public int Packages { get; set; }
    public int Wems { get; set; }
    public int Wavs { get; set; }
    public int Failures { get; set; }
    public TimeSpan Elapsed { get; set; }
    public List<(string File, string Error)> Errors { get; } = new();
}

/// <summary>
/// Unpacks Wwise <c>.pck</c> containers into individual <c>.wem</c> files and optionally
/// decodes them to <c>.wav</c> with vgmstream.
///
/// <para>None of this is encrypted — see docs/06_AUDIO.md.</para>
/// </summary>
public sealed class AudioService
{
    private readonly AudioOptions _opt;

    public AudioService(AudioOptions options) => _opt = options;

    public IReadOnlyList<string> EnumeratePackages()
    {
        var list = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(_opt.DatRoot))
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                string name = Path.GetFileName(f);
                var entry = _opt.Manifest.Find(name);

                if (entry is not null)
                {
                    if (entry.Category != "Sound") continue;
                    if (!entry.Name.EndsWith(".pck", StringComparison.OrdinalIgnoreCase)) continue;
                }
                else if (FileClassifier.Classify(f) != DatFileKind.WwisePackage)
                {
                    continue;
                }

                list.Add(f);
            }
        }
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    public AudioResult Run(IProgress<AudioProgress>? progress = null,
                           IReadOnlyList<string>? subset = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var packages = subset ?? EnumeratePackages();
        var result = new AudioResult { Packages = packages.Count };
        var errors = new ConcurrentBag<(string, string)>();

        int done = 0, wems = 0, wavs = 0, fails = 0;

        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = _opt.Parallelism,
            CancellationToken = _opt.Cancellation,
        };

        try
        {
            Parallel.ForEach(packages, po, path =>
            {
                string name = Path.GetFileName(path);
                var entry = _opt.Manifest.Find(name);
                string relDir = OutputPathBuilder.Normalise(
                    entry?.Name ?? $"_unnamed/{name}", string.Empty);

                string outDir = Path.Combine(_opt.OutputRoot, relDir);
                Directory.CreateDirectory(outDir);

                AkpkPackage pkg;
                try
                {
                    pkg = AkpkPackage.Open(path);
                }
                catch (Exception ex)
                {
                    fails++;
                    errors.Add((name, $"打开失败: {ex.Message}"));
                    Interlocked.Increment(ref done);
                    return;
                }

                using var source = File.OpenRead(path);
                int localWems = 0, localWavs = 0;

                foreach (var e in pkg.Entries)
                {
                    if (_opt.Cancellation.IsCancellationRequested) break;

                    string stem = e.Id.ToString();
                    string wemPath = Path.Combine(outDir, stem + ".wem");

                    try
                    {
                        using (var outFs = File.Create(wemPath))
                            AkpkPackage.Extract(source, e, outFs);
                        localWems++;

                        if (_opt.DecodeToWav)
                        {
                            if (!ToolLocator.VgmstreamAvailable) continue;

                            string wavPath = Path.Combine(outDir, stem + ".wav");
                            var r = MediaConverter.WemToWav(wemPath, wavPath, _opt.HonourLoop);
                            if (r.Success && File.Exists(wavPath))
                            {
                                localWavs++;
                                if (!_opt.KeepWem) TryDelete(wemPath);
                            }
                            else
                            {
                                Interlocked.Increment(ref fails);
                                errors.Add((name, $"{stem}.wem 解码失败: {FirstLine(r.StdErr)}"));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref fails);
                        errors.Add((name, $"{stem}: {ex.Message}"));
                    }
                }

                Interlocked.Add(ref wems, localWems);
                Interlocked.Add(ref wavs, localWavs);
                int d = Interlocked.Increment(ref done);

                progress?.Report(new AudioProgress
                {
                    PackagesTotal = packages.Count,
                    PackagesDone = d,
                    WemsExtracted = wems,
                    WavConverted = wavs,
                    WavFailed = fails,
                    CurrentPackage = name,
                });
            });
        }
        catch (OperationCanceledException) { }

        result.Wems = wems;
        result.Wavs = wavs;
        result.Failures = fails;
        result.Errors.AddRange(errors.Take(500));
        result.Elapsed = sw.Elapsed;
        return result;
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return line ?? string.Empty;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
