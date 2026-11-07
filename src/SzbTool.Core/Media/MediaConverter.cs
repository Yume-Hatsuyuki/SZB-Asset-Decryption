using SzbTool.Core.External;

namespace SzbTool.Core.Media;

public enum VideoContainer
{
    /// <summary>Re-encode to H.264/AAC in MP4. Widest compatibility, slower, lossy.</summary>
    Mp4H264,

    /// <summary>Remux without re-encoding. Fast and lossless, but VP9-in-MP4 is poorly supported.</summary>
    Mp4Copy,

    /// <summary>Matroska container, copy codecs. Lossless and plays everywhere.</summary>
    MkvCopy,
}

/// <summary>
/// Thin wrappers around the two external decoders. Argument lists are built explicitly so
/// that paths containing spaces or non-ASCII characters never need quoting.
/// </summary>
public static class MediaConverter
{
    /// <summary>Decode a Wwise <c>.wem</c> into a PCM WAV.</summary>
    public static ProcessResult WemToWav(string wemPath, string wavPath, bool honourLoop = false)
    {
        var vgm = ToolLocator.Vgmstream
            ?? throw new InvalidOperationException("未找到 vgmstream-cli.exe，请先下载依赖");

        var args = new List<string>();
        if (!honourLoop) args.Add("-i");        // play the stream once, no loop padding
        args.Add("-o"); args.Add(wavPath);
        args.Add(wemPath);

        return ProcessRunner.Run(vgm, args);
    }

    /// <summary>
    /// Package an IVF (VP9) or a bare MPEG-1/H.264 elementary stream into a playable file.
    /// </summary>
    public static ProcessResult VideoToContainer(
        string videoPath,
        string outputPath,
        VideoContainer container,
        double frameRate,
        string? audioPath = null,
        string? forceInputFormat = null)
    {
        var ffmpeg = ToolLocator.Ffmpeg
            ?? throw new InvalidOperationException("未找到 ffmpeg.exe，请先下载依赖");

        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
        };

        // A bare elementary stream has no timestamps; ffmpeg would otherwise guess 25 fps.
        if (forceInputFormat is not null)
        {
            args.Add("-r");
            args.Add(FormatFps(frameRate));
            args.Add("-f");
            args.Add(forceInputFormat);
        }

        args.Add("-i"); args.Add(videoPath);

        bool hasAudio = audioPath is not null;
        if (hasAudio) { args.Add("-i"); args.Add(audioPath!); }

        if (hasAudio)
        {
            args.Add("-map"); args.Add("0:v:0");
            args.Add("-map"); args.Add("1:a:0");
        }

        switch (container)
        {
            case VideoContainer.Mp4H264:
                args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                               "-pix_fmt", "yuv420p"]);
                if (hasAudio) args.AddRange(["-c:a", "aac", "-b:a", "192k"]);
                args.AddRange(["-movflags", "+faststart"]);
                break;

            case VideoContainer.Mp4Copy:
                args.AddRange(["-c", "copy"]);
                break;

            case VideoContainer.MkvCopy:
                args.AddRange(["-c", "copy"]);
                break;
        }

        if (hasAudio) args.AddRange(["-shortest"]);

        args.Add(outputPath);

        return ProcessRunner.Run(ffmpeg, args, timeoutMs: 30 * 60 * 1000);
    }

    private static string FormatFps(double fps)
    {
        if (fps <= 0) return "30";
        return Math.Abs(fps - Math.Round(fps)) < 0.001
            ? ((int)Math.Round(fps)).ToString()
            : fps.ToString("0.###");
    }
}
