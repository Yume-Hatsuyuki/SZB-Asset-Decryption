using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SzbAssetDecryption.Core.Audio;

/// <summary>
/// <c>sound/WwiseIdMapping.bytes</c> — Wwise event id to event name.
///
/// <para>
/// File layout: 32-byte AES key, 16-byte IV, then AES-256-CBC (PKCS7) ciphertext.
/// Plaintext is <c>u32 count</c> followed by <c>u32 id, u32 nameLength, utf8 name</c> repeats,
/// 21 780 rows in the current build.
/// </para>
/// <para>
/// The id is FNV-1 (32-bit) of the lowercased name, which is how Wwise derives event short ids.
/// A row whose id does not match the hash of its own name is still kept: the name is more
/// useful than nothing, and <see cref="HashMatches"/> reports how many rows verified.
/// </para>
/// </summary>
public sealed class WwiseIdMap
{
    public const int KeyLength = 32;
    public const int IvLength = 16;

    private readonly Dictionary<uint, string> _byId;

    private WwiseIdMap(Dictionary<uint, string> byId, int hashMatches)
    {
        _byId = byId;
        HashMatches = hashMatches;
    }

    public int Count => _byId.Count;

    /// <summary>Rows whose stored id equals FNV-1 of the lowercased name.</summary>
    public int HashMatches { get; }

    public string? Find(uint id) => _byId.TryGetValue(id, out var name) ? name : null;

    public static WwiseIdMap? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? Load(File.ReadAllBytes(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public static WwiseIdMap Load(ReadOnlySpan<byte> file)
    {
        if (file.Length <= KeyLength + IvLength)
            throw new InvalidDataException("WwiseIdMapping.bytes 长度不足，缺少密钥或 IV");

        using var aes = Aes.Create();
        aes.Key = file[..KeyLength].ToArray();
        aes.IV = file.Slice(KeyLength, IvLength).ToArray();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        var body = file[(KeyLength + IvLength)..].ToArray();
        byte[] plain;
        try
        {
            plain = aes.CreateDecryptor().TransformFinalBlock(body, 0, body.Length);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidDataException("WwiseIdMapping.bytes 解密失败", ex);
        }

        return Parse(plain);
    }

    public static WwiseIdMap Parse(ReadOnlySpan<byte> plain)
    {
        if (plain.Length < 4)
            throw new InvalidDataException("WwiseIdMapping.bytes 明文为空");

        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(plain);
        var map = new Dictionary<uint, string>(count);
        int pos = 4;
        int matches = 0;

        for (int i = 0; i < count; i++)
        {
            if (pos + 8 > plain.Length) break;

            uint id = BinaryPrimitives.ReadUInt32LittleEndian(plain[pos..]);
            int len = (int)BinaryPrimitives.ReadUInt32LittleEndian(plain[(pos + 4)..]);
            if (len < 0 || pos + 8 + len > plain.Length) break;

            string name = Encoding.UTF8.GetString(plain.Slice(pos + 8, len));
            pos += 8 + len;

            map[id] = name;
            if (HashId(name) == id) matches++;
        }

        if (map.Count == 0)
            throw new InvalidDataException("WwiseIdMapping.bytes 没有任何条目");

        return new WwiseIdMap(map, matches);
    }

    /// <summary>FNV-1 (32-bit) over the lowercased name — Wwise's event id derivation.</summary>
    public static uint HashId(string name)
    {
        uint hash = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(name.ToLowerInvariant()))
            hash = unchecked(hash * 16777619) ^ b;
        return hash;
    }
}
