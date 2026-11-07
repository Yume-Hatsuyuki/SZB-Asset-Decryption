namespace SzbTool.Core.External;

/// <summary>
/// Finds the external command-line decoders.
///
/// <para>
/// Search order matters: a copy that ships next to the app must win over whatever happens
/// to be on PATH, otherwise a stale system FFmpeg silently takes over and produces
/// different output.
/// </para>
/// </summary>
public static class ToolLocator
{
    public static string? Ffmpeg { get; private set; }
    public static string? Vgmstream { get; private set; }

    public static bool FfmpegAvailable => Ffmpeg is not null;
    public static bool VgmstreamAvailable => Vgmstream is not null;

    /// <summary>Extra directories to probe, in priority order.</summary>
    private static IEnumerable<string> SearchRoots()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "Dependencies");
        yield return Path.Combine(baseDir, "deps");
        yield return Path.Combine(baseDir, "tools", "bin", "ffmpeg", "bin");
        yield return Path.Combine(baseDir, "tools", "bin", "vgmstream");
        yield return Path.Combine(baseDir, "tools", "bin");
        yield return baseDir;

        // repository layout: src/SzbTool.App/bin/... -> repo root
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            yield return Path.Combine(dir.FullName, "tools", "bin", "ffmpeg", "bin");
            yield return Path.Combine(dir.FullName, "tools", "bin", "vgmstream");
            yield return Path.Combine(dir.FullName, "tools", "bin");
            dir = dir.Parent;
        }
    }

    public static void Refresh()
    {
        Ffmpeg = Probe("ffmpeg.exe");
        Vgmstream = Probe("vgmstream-cli.exe");
    }

    private static string? Probe(string fileName)
    {
        foreach (var root in SearchRoots())
        {
            try
            {
                var p = Path.Combine(root, fileName);
                if (File.Exists(p)) return p;
            }
            catch { /* invalid path characters, skip */ }
        }

        // fall back to PATH
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    /// <summary>Human-readable summary for the UI.</summary>
    public static string Describe()
        => $"ffmpeg: {(Ffmpeg ?? "缺失")}\nvgmstream: {(Vgmstream ?? "缺失")}";
}
