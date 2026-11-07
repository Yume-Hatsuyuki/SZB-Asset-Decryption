namespace SzbAssetDecryption.Core.Restore;

/// <summary>What a file in <c>Persistent/dat</c> actually is.</summary>
public enum DatFileKind
{
    Unknown,
    UnityBundle,
    WwisePackage,
    CridVideo,
    Png,
    Font,
    Other,
}

public static class FileClassifier
{
    public static DatFileKind Classify(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return Classify(fs);
        }
        catch
        {
            return DatFileKind.Unknown;
        }
    }

    public static DatFileKind Classify(Stream stream)
    {
        Span<byte> head = stackalloc byte[8];
        stream.Position = 0;
        int n = stream.Read(head);
        if (n < 4) return DatFileKind.Unknown;

        if (head[..8].SequenceEqual("UnityFS\0"u8)) return DatFileKind.UnityBundle;
        if (head[..4].SequenceEqual("AKPK"u8)) return DatFileKind.WwisePackage;
        if (head[..4].SequenceEqual("CRID"u8)) return DatFileKind.CridVideo;

        if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
            return DatFileKind.Png;

        if (head[..4].SequenceEqual("OTTO"u8) || head[..4].SequenceEqual("ttcf"u8))
            return DatFileKind.Font;

        return DatFileKind.Other;
    }

    public static string Describe(DatFileKind kind) => kind switch
    {
        DatFileKind.UnityBundle => "UnityFS 资源包",
        DatFileKind.WwisePackage => "Wwise 音频包",
        DatFileKind.CridVideo => "CRI 视频",
        DatFileKind.Png => "PNG 图片",
        DatFileKind.Font => "字体",
        DatFileKind.Other => "其它",
        _ => "未知",
    };
}
