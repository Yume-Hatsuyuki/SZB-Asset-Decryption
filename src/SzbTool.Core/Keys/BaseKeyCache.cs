using System.Text.Json;
using System.Text.Json.Serialization;

namespace SzbTool.Core.Keys;

/// <summary>What a previous derivation produced, and what it was derived from.</summary>
public sealed class BaseKeyCacheEntry
{
    public string DatRoot { get; set; } = string.Empty;
    public string ManifestPath { get; set; } = string.Empty;
    public long ManifestSize { get; set; }
    public long ManifestWriteTicks { get; set; }
    public string BaseKeyHex { get; set; } = string.Empty;
    public int ManifestEntries { get; set; }
    public int Probes { get; set; }
    public int Pairs { get; set; }
    public int Windows { get; set; }
    public int Rank { get; set; }
    public int Nullity { get; set; }
    public DateTimeOffset DerivedAt { get; set; }

    [JsonIgnore]
    public byte[]? Key =>
        BaseKeyHex.Length > 0 && BaseKeyHex.Length % 2 == 0
            ? Convert.FromHexString(BaseKeyHex)
            : null;
}

/// <summary>
/// Remembers the last successful derivation so start-up does not re-scan forty thousand
/// files every time.
///
/// <para>
/// This is <em>not</em> a place to store a key someone typed in. The file records what was
/// computed, when, and from which manifest; if that manifest changes (a game update), the
/// entry is discarded and the key is derived again. Deleting the file is always safe — the
/// worst that happens is one extra derivation.
/// </para>
/// </summary>
public static class BaseKeyCache
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SzbTool", "base-key.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Returns the cached key when it was derived from this same directory and this same
    /// manifest. Any change to either invalidates it.
    ///
    /// <para>
    /// The manifest path is taken from the entry itself, so a hit avoids scanning the
    /// install to find the manifest again — that scan is the slowest part of start-up.
    /// </para>
    /// </summary>
    public static BaseKeyCacheEntry? TryLoad(string datRoot, string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath)) return null;

            var entry = JsonSerializer.Deserialize<BaseKeyCacheEntry>(
                File.ReadAllText(cachePath), Options);
            if (entry is null) return null;

            if (!string.Equals(Normalise(entry.DatRoot), Normalise(datRoot),
                               StringComparison.OrdinalIgnoreCase))
                return null;

            if (string.IsNullOrWhiteSpace(entry.ManifestPath)) return null;
            if (!File.Exists(entry.ManifestPath)) return null;

            var info = new FileInfo(entry.ManifestPath);
            if (info.Length != entry.ManifestSize) return null;
            if (info.LastWriteTimeUtc.Ticks != entry.ManifestWriteTicks) return null;

            if (entry.Key is not { Length: > 0 }) return null;

            return entry;
        }
        catch
        {
            return null;                        // a broken cache is just a cache miss
        }
    }

    public static void Save(string cachePath, BaseKeyCacheEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(entry, Options));
        }
        catch
        {
            // Caching is an optimisation; failing to write must never fail the derivation.
        }
    }

    public static void Clear(string cachePath)
    {
        try { if (File.Exists(cachePath)) File.Delete(cachePath); }
        catch { /* ignore */ }
    }

    private static string Normalise(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch { return path; }
    }
}
