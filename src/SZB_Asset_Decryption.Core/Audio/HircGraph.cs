using System.Buffers.Binary;

namespace SzbAssetDecryption.Core.Audio;

/// <summary>
/// Minimal <c>HIRC</c> reader. Parses the object table and walks
/// <c>Event → Action → Sound</c> so a media id can be matched to an event name.
///
/// <para>
/// Object layout in bank version ≥150: <c>u8 type | u32 size | u32 id | body</c>, where
/// <paramref name="size"/> counts the id and body but not the type byte and size field.
/// </para>
/// <para>
/// Children are not resolved through a field table: every 4-byte window of a body is checked
/// against the set of object ids in the same bank. With ~40 objects and 32-bit ids a collision
/// is rare, and a wrong edge only costs a name, never a wrong byte in the output.
/// </para>
/// </summary>
public static class HircGraph
{
    /// <summary>Wwise object types this reader cares about.</summary>
    private const byte TypeSound = 2;   // CAkSound: payload carries the media id
    private const byte TypeEvent = 4;   // CAkEvent: id = FNV-1 of the event name

    public const byte SoundTypeId = TypeSound;
    public const byte EventTypeId = TypeEvent;

    public sealed record HircObject(byte Type, uint Id, byte[] Body);

    /// <summary>Parse a HIRC chunk payload. Returns null when the layout does not match.</summary>
    public static List<HircObject>? TryParse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return null;

        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        if (count <= 0 || count > 200_000) return null;

        var objects = new List<HircObject>(count);
        int pos = 4;

        for (int i = 0; i < count; i++)
        {
            if (pos + 9 > payload.Length) return null;

            byte type = payload[pos];
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(payload[(pos + 1)..]);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(payload[(pos + 5)..]);

            if (size < 4) return null;
            long end = pos + 5L + size;
            if (end > payload.Length) return null;

            objects.Add(new HircObject(type, id,
                payload.Slice(pos + 9, (int)size - 4).ToArray()));
            pos = (int)end;
        }

        return pos == payload.Length ? objects : null;
    }

    /// <summary>Media id of a Sound object, or 0 when the body is too short.</summary>
    public static uint MediaIdOf(HircObject sound) =>
        sound.Body.Length >= 9 ? BinaryPrimitives.ReadUInt32LittleEndian(sound.Body.AsSpan(5)) : 0;

    /// <summary>
    /// Resolve media id to event name. Events that reach the same media id are ranked:
    /// <c>Play_*</c> first, then ordinal order, so the output is stable across runs.
    /// </summary>
    public static Dictionary<uint, string> ResolveMediaNames(
        IReadOnlyList<HircObject> objects, WwiseIdMap names)
    {
        var byId = new Dictionary<uint, HircObject>(objects.Count);
        foreach (var o in objects) byId[o.Id] = o;

        var refs = new Dictionary<uint, List<uint>>(objects.Count);
        foreach (var o in objects)
        {
            var list = new List<uint>();
            for (int i = 0; i + 4 <= o.Body.Length; i++)
            {
                uint v = BinaryPrimitives.ReadUInt32LittleEndian(o.Body.AsSpan(i));
                if (v != o.Id && byId.ContainsKey(v)) list.Add(v);
            }
            refs[o.Id] = list;
        }

        var candidates = new Dictionary<uint, List<string>>();

        foreach (var ev in objects)
        {
            if (ev.Type != TypeEvent) continue;

            string? name = names.Find(ev.Id);
            if (string.IsNullOrEmpty(name)) continue;

            var seen = new HashSet<uint> { ev.Id };
            var stack = new Stack<uint>();
            stack.Push(ev.Id);

            while (stack.Count > 0)
            {
                uint cur = stack.Pop();
                var obj = byId[cur];

                if (obj.Type == TypeSound)
                {
                    uint media = MediaIdOf(obj);
                    if (media == 0) continue;

                    if (!candidates.TryGetValue(media, out var list))
                        candidates[media] = list = new List<string>();
                    list.Add(name);
                    continue;
                }

                foreach (var next in refs[cur])
                    if (seen.Add(next)) stack.Push(next);
            }
        }

        var result = new Dictionary<uint, string>(candidates.Count);
        foreach (var (media, list) in candidates)
        {
            list.Sort(StringComparer.Ordinal);
            string best = list.Find(n => n.StartsWith("Play_", StringComparison.OrdinalIgnoreCase))
                          ?? list[0];
            result[media] = best;
        }
        return result;
    }
}
