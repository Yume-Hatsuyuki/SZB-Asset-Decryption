using System.Text;
using SzbTool.Core.IO;

namespace SzbTool.Core.Audio;

/// <summary>A single waveform inside an AKPK container.</summary>
public sealed class WemEntry
{
    /// <summary>Wwise media id when known (from DIDX), otherwise a sequential index.</summary>
    public uint Id { get; init; }

    /// <summary>Absolute offset of the payload in the container file.</summary>
    public long Offset { get; init; }

    /// <summary>Payload length in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Why this entry exists: <c>didx</c> or <c>riff</c>.</summary>
    public string Origin { get; init; } = "didx";

    public override string ToString() => $"{Id} @{Offset}+{Size} ({Origin})";
}

/// <summary>A raw chunk header found while walking the container.</summary>
public sealed record AkpkChunk(string Tag, long Offset, uint Size);

/// <summary>
/// Reader for Wwise <c>AKPK</c> packages.
///
/// <para>
/// The container is a flat chain of <c>[4-byte tag][u32 LE payload size][payload]</c> records.
/// No encryption anywhere. Two payload arrangements occur in this title:
/// </para>
/// <list type="bullet">
///   <item><c>BKHD + DIDX + DATA + HIRC</c> — the index table names offsets inside DATA.</item>
///   <item><c>BKHD + HIRC</c> followed by whole <c>RIFF</c> files — each one is a complete WEM.</item>
/// </list>
/// <para>
/// Both fall out of the same loop, because a RIFF header is byte-compatible with a chunk
/// header: <c>"RIFF" | u32 payloadLength | ...</c>.
/// </para>
/// </summary>
public sealed class AkpkPackage
{
    public List<AkpkChunk> Chunks { get; } = new();
    public List<WemEntry> Entries { get; } = new();
    public long FileSize { get; private set; }
    public string Path { get; private set; } = string.Empty;

    public static AkpkPackage Open(string path)
    {
        var pkg = new AkpkPackage { Path = path };
        using var fs = File.OpenRead(path);
        pkg.FileSize = fs.Length;

        long pos = 0;
        long didxPayload = -1;
        uint didxSize = 0;
        long dataPayload = -1;
        uint dataSize = 0;

        Span<byte> hdr = stackalloc byte[8];

        while (pos + 8 <= fs.Length)
        {
            fs.Position = pos;
            if (fs.Read(hdr) < 8) break;

            string tag = Encoding.ASCII.GetString(hdr[..4].ToArray());
            // Chunk sizes are little-endian; RIFF uses the same convention.
            uint size = (uint)(hdr[4] | (hdr[5] << 8) | (hdr[6] << 16) | (hdr[7] << 24));
            long payload = pos + 8;

            if (payload + size > fs.Length)
                break;                              // truncated or not a chunk

            // Keep only tags made of printable ASCII; anything else means we drifted.
            if (!tag.All(ch => ch is >= ' ' and <= '~'))
                break;

            pkg.Chunks.Add(new AkpkChunk(tag, pos, size));

            switch (tag)
            {
                case "DIDX":
                    didxPayload = payload;
                    didxSize = size;
                    break;
                case "DATA":
                    dataPayload = payload;
                    dataSize = size;
                    break;
            }

            pos = payload + size;
        }

        // ---- entries backed by an index table ----
        long maxDataAvailable = dataPayload >= 0 ? dataSize : 0;

        if (didxPayload >= 0 && dataPayload >= 0)
        {
            int count = (int)(didxSize / 12);
            var table = new byte[didxSize];
            fs.Position = didxPayload;
            fs.ReadExactly(table);

            var r = new SpanReader(table);
            for (int i = 0; i < count; i++)
            {
                uint id = r.U32LE();
                uint off = r.U32LE();
                uint len = r.U32LE();

                if (off + (long)len > maxDataAvailable) continue;

                pkg.Entries.Add(new WemEntry
                {
                    Id = id,
                    Offset = dataPayload + off,
                    Size = len,
                    Origin = "didx",
                });
            }
        }

        // ---- entries that are whole RIFF files ----
        uint seq = 0;
        foreach (var c in pkg.Chunks.Where(c => c.Tag == "RIFF"))
        {
            pkg.Entries.Add(new WemEntry
            {
                Id = seq++,
                Offset = c.Offset,
                Size = c.Size + 8L,
                Origin = "riff",
            });
        }

        return pkg;
    }

    /// <summary>Copy one waveform's bytes out of the container.</summary>
    public static void Extract(Stream source, WemEntry entry, Stream destination)
    {
        source.Position = entry.Offset;
        var buf = new byte[64 * 1024];
        long remaining = entry.Size;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buf.Length, remaining);
            int got = source.Read(buf, 0, want);
            if (got <= 0) break;
            destination.Write(buf, 0, got);
            remaining -= got;
        }
    }
}
