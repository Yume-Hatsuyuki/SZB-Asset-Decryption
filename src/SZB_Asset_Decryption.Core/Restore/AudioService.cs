using System.Collections.Concurrent;
using SzbAssetDecryption.Core.Audio;
using SzbAssetDecryption.Core.External;
using SzbAssetDecryption.Core.Manifest;
using SzbAssetDecryption.Core.Media;

namespace SzbAssetDecryption.Core.Restore;

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

    /// <summary>Waves written under their Wwise event name instead of a numeric id.</summary>
    public int Named { get; set; }

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

        int done = 0, wems = 0, wavs = 0, fails = 0, named = 0;

        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = _opt.Parallelism,
            CancellationToken = _opt.Cancellation,
        };

        WwiseIdMap? idMap = LoadIdMap();

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

                IReadOnlyDictionary<uint, string> mediaNames = ResolveNames(pkg, idMap);
                var usedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using var source = File.OpenRead(path);
                int localWems = 0, localWavs = 0, localNamed = 0;

                foreach (var e in pkg.Entries)
                {
                    if (_opt.Cancellation.IsCancellationRequested) break;

                    string stem = UniqueStem(usedStems, e.Id, mediaNames);
                    if (mediaNames.ContainsKey(e.Id)) localNamed++;
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
                Interlocked.Add(ref named, localNamed);
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
        result.Named = named;
        result.Errors.AddRange(errors.Take(500));
        result.Elapsed = sw.Elapsed;
        return result;
    }

    /// <summary>
    /// Load <c>sound/WwiseIdMapping.bytes</c> through the manifest. Returns null when the file
    /// is absent or does not decrypt, in which case waves keep their numeric media id.
    /// </summary>
    private WwiseIdMap? LoadIdMap()
    {
        var entry = _opt.Manifest.All.FirstOrDefault(e =>
            e.Name.EndsWith("WwiseIdMapping.bytes", StringComparison.OrdinalIgnoreCase));
        if (entry is null || entry.Hash.Length < 2) return null;

        string path = Path.Combine(_opt.DatRoot, entry.Hash[..2], entry.Hash);
        return WwiseIdMap.TryLoad(path);
    }

    private static IReadOnlyDictionary<uint, string> ResolveNames(AkpkPackage pkg, WwiseIdMap? idMap)
    {
        if (idMap is null || pkg.Hirc is null)
            return s_emptyNames;

        var objects = HircGraph.TryParse(pkg.Hirc);
        return objects is null ? s_emptyNames : HircGraph.ResolveMediaNames(objects, idMap);
    }

    private static readonly IReadOnlyDictionary<uint, string> s_emptyNames =
        new Dictionary<uint, string>();

    /// <summary>
    /// Prefer the Wwise event name, fall back to the numeric media id. Names repeat across a
    /// package when one wave is shared by several events, so collisions get a numeric suffix.
    /// </summary>
    private static string UniqueStem(HashSet<string> used, uint id,
                                     IReadOnlyDictionary<uint, string> names)
    {
        string stem = names.TryGetValue(id, out var n) ? Sanitise(n) : id.ToString();

        if (used.Add(stem)) return stem;

        for (int i = 2; ; i++)
        {
            string candidate = $"{stem}_{i}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static string Sanitise(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
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
