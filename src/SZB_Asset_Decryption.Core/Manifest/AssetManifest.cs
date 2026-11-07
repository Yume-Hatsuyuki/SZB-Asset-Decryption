using System.Text;
using SzbAssetDecryption.Core.Crypto;

namespace SzbAssetDecryption.Core.Manifest;

/// <summary>One row of the manifest's <c>asset</c> or <c>raw_asset</c> table.</summary>
public sealed class ManifestEntry
{
    /// <summary>Real asset path, e.g. <c>sound/Windows/m/mx_M60.pck</c>.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>26-char uppercase base32 string — this is the file name on disk.</summary>
    public string Hash { get; init; } = string.Empty;

    /// <summary>Per-file XOR key. Meaningless for non-UnityFS entries, they are not encrypted.</summary>
    public long Key { get; init; }

    public long Size { get; init; }
    public string Category { get; init; } = string.Empty;

    /// <summary>True when the row came from the <c>raw_asset</c> table.</summary>
    public bool IsRaw { get; init; }

    public override string ToString() => $"{Hash} {Name} key={Key} size={Size}";
}

/// <summary>
/// Parsed <c>assetbundle.manifest</c>.
///
/// <para>
/// Layout: a msgpack map (the TOC), then the tables back to back, then a 16-byte MD5.
/// The TOC's offsets are relative to the END of the TOC, not to the start of the file.
/// </para>
/// </summary>
public sealed class AssetManifest
{
    private readonly Dictionary<string, ManifestEntry> _byHash = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ManifestEntry> _all = new();

    public IReadOnlyList<ManifestEntry> All => _all;
    public int Count => _all.Count;

    /// <summary>Where this manifest was loaded from, for display purposes.</summary>
    public string? SourcePath { get; private set; }

    /// <summary>Table row counts, for the UI.</summary>
    public Dictionary<string, int> TableCounts { get; } = new();

    public ManifestEntry? Find(string hash) =>
        _byHash.TryGetValue(hash, out var e) ? e : null;

    public static AssetManifest Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var m = Parse(bytes);
        m.SourcePath = path;
        return m;
    }

    public static AssetManifest Parse(ReadOnlyMemory<byte> data)
    {
        var manifest = new AssetManifest();

        var reader = new MsgPackReader(data);
        if (reader.Read() is not Dictionary<object, object?> toc)
            throw new InvalidDataException("manifest 的 TOC 不是一个 msgpack map");

        int tocEnd = reader.Consumed;

        foreach (var kv in toc)
        {
            if (kv.Key is not string tableName) continue;
            if (kv.Value is not List<object?> span || span.Count < 2) continue;

            long offset = ToLong(span[0]);
            long length = ToLong(span[1]);
            long start = tocEnd + offset;

            if (start < 0 || length <= 0 || start + length > data.Length)
                continue;                       // 表边界不合法，跳过

            List<object?>? rows;
            try
            {
                var sliceReader = new MsgPackReader(data, (int)start);
                rows = ExtractRows(sliceReader.Read());
            }
            catch
            {
                continue;                       // 单表解析失败不影响其它表
            }

            if (rows is null) continue;
            manifest.TableCounts[tableName] = rows.Count;

            foreach (var row in rows)
            {
                if (row is not List<object?> fields) continue;
                var entry = tableName switch
                {
                    "asset" => ParseAssetRow(fields),
                    "raw_asset" => ParseRawRow(fields),
                    _ => null,
                };
                if (entry is null || string.IsNullOrEmpty(entry.Hash)) continue;

                manifest._all.Add(entry);
                manifest._byHash[entry.Hash] = entry;
            }
        }

        return manifest;
    }

    /// <summary>
    /// Turn a raw table value into rows.
    ///
    /// <para>
    /// Small tables (like <c>config</c>) are plain msgpack arrays. Large ones are wrapped in
    /// <c>Ext(99, ...)</c> whose payload is a msgpack int32 with the uncompressed size,
    /// followed by an LZ4 block. Missing this is why an early build reported
    /// "manifest found, 0 entries".
    /// </para>
    /// </summary>
    private static List<object?>? ExtractRows(object? value)
    {
        if (value is List<object?> list) return list;

        if (value is MsgPackExt { Code: 99 } ext)
        {
            var inner = new MsgPackReader(new ReadOnlyMemory<byte>(ext.Data));
            long uncompressed = ToLong(inner.Read());
            int headerLength = inner.Consumed;

            if (uncompressed <= 0 || uncompressed > 256 * 1024 * 1024) return null;

            var compressed = ext.Data.AsSpan(headerLength);
            var plain = Lz4Block.Decompress(compressed, (int)uncompressed);

            var tableReader = new MsgPackReader(new ReadOnlyMemory<byte>(plain));
            return tableReader.Read() as List<object?>;
        }

        return null;
    }

    // asset:     [name, hash, asset_id, all_deps, key, size, category, group, checksum]
    private static ManifestEntry? ParseAssetRow(List<object?> f)
    {
        if (f.Count < 6) return null;
        return new ManifestEntry
        {
            Name = AsString(f[0]),
            Hash = AsString(f[1]),
            Key = ToLong(f[4]),
            Size = ToLong(f[5]),
            Category = f.Count > 6 ? AsString(f[6]) : string.Empty,
            IsRaw = false,
        };
    }

    // raw_asset: [name, hash, size, category, flag, key]
    private static ManifestEntry? ParseRawRow(List<object?> f)
    {
        if (f.Count < 6) return null;
        return new ManifestEntry
        {
            Name = AsString(f[0]),
            Hash = AsString(f[1]),
            Size = ToLong(f[2]),
            Category = AsString(f[3]),
            Key = ToLong(f[5]),
            IsRaw = true,
        };
    }

    private static string AsString(object? o) => o switch
    {
        string s => s,
        byte[] b => Encoding.UTF8.GetString(b),
        null => string.Empty,
        _ => o.ToString() ?? string.Empty,
    };

    private static long ToLong(object? o) => o switch
    {
        long l => l,
        int i => i,
        ulong u => (long)u,
        double d => (long)d,
        null => 0,
        _ => long.TryParse(o.ToString(), out var v) ? v : 0,
    };

    /// <summary>
    /// Look for the manifest the game keeps on disk.
    ///
    /// <para>
    /// The local copy is authoritative: its <c>asset</c> table has exactly one row per
    /// UnityFS file in the same install, so key coverage is 100%. A manifest downloaded
    /// from the CDN describes the newest build and typically only covers about half.
    /// </para>
    /// </summary>
    public static string? LocateLocal(string datRoot)
    {
        if (!Directory.Exists(datRoot)) return null;

        // 1) cheap pass: the manifest is a few MB and lives in a two-letter bucket.
        var candidates = new List<string>();

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(datRoot))
            {
                string name = Path.GetFileName(dir);
                if (name.Length != 2) continue;

                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Length is < 512 * 1024 or > 16 * 1024 * 1024) continue;
                        candidates.Add(f);
                    }
                    catch { /* unreadable, ignore */ }
                }
            }
        }
        catch (UnauthorizedAccessException) { return null; }

        // 2) inspect the first bytes of each candidate.
        foreach (var path in candidates)
        {
            try
            {
                using var fs = File.OpenRead(path);
                Span<byte> head = stackalloc byte[64];
                int n = fs.Read(head);
                if (n < 8) continue;

                // msgpack fixmap with 2..8 entries
                if (head[0] < 0x82 || head[0] > 0x88) continue;

                var text = Encoding.ASCII.GetString(head[..n].ToArray());
                if (!text.Contains("asset", StringComparison.Ordinal)) continue;

                // 3) full validation: the TOC must expose the expected table names.
                var bytes = File.ReadAllBytes(path);
                var probe = new MsgPackReader(bytes);
                if (probe.Read() is not Dictionary<object, object?> toc) continue;
                if (!toc.ContainsKey("asset") || !toc.ContainsKey("raw_asset")) continue;

                return path;
            }
            catch { /* not a manifest, keep looking */ }
        }

        return null;
    }
}
