namespace SzbAssetDecryption.App;

/// <summary>A label/value pair rendered as a small statistic tile.</summary>
public sealed record StatItem(string Label, string Value, string? Hint = null);

/// <summary>Formatting helpers shared by the pages.</summary>
public static class Fmt
{
    public static string Bytes(long bytes)
    {
        if (bytes < 0) return "—";
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {units[i]}";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalSeconds < 1 ? $"{t.TotalMilliseconds:0} ms"
        : t.TotalMinutes < 1 ? $"{t.TotalSeconds:0.0} 秒"
        : $"{t.Minutes} 分 {t.Seconds} 秒";

    public static string Count(int n) => n.ToString("N0");
}
