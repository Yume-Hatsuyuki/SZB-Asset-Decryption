using System.Diagnostics;
using System.Numerics;
using SzbAssetDecryption.Core.Crypto;
using SzbAssetDecryption.Core.UnityFs;

namespace SzbAssetDecryption.Core.Keys;

/// <summary>What one candidate key length produced.</summary>
public sealed record BaseLengthTrial(
    int BaseLength, int Equations, int Rank, int Nullity, bool Consistent);

public sealed class BaseKeySolveResult
{
    public byte[]? BaseKey { get; internal set; }

    /// <summary>Null on success; a human-readable reason otherwise.</summary>
    public string? Failure { get; internal set; }

    public int Probes { get; internal set; }
    public int PairsConsidered { get; internal set; }
    public int PairsUsable { get; internal set; }
    public int Windows { get; internal set; }
    public int Equations { get; internal set; }
    public int Rank { get; internal set; }
    public int Nullity { get; internal set; }
    public int CandidatesTried { get; internal set; }
    public TimeSpan Elapsed { get; internal set; }

    public List<BaseLengthTrial> Trials { get; } = new();

    public bool Success => BaseKey is { Length: > 0 };
    public int Period => BaseKey is null ? 0 : BaseKey.Length * 8;
}

/// <summary>Two archives believed to hold the same content, plus the measured windows.</summary>
internal sealed class ProbePair
{
    public required BundleProbe A { get; init; }
    public required BundleProbe B { get; init; }
    public List<(long OffsetA, long OffsetB, byte R)> Windows { get; } = new();
}

/// <summary>
/// Recovers the base key from nothing but local game files.
///
/// <para>
/// Three stages, and none of them needs a known plaintext file:
/// </para>
/// <list type="number">
/// <item>Find two archives that hold the same content but start their data at different
/// offsets. XORing their ciphertexts cancels the plaintext, leaving
/// <c>base[a] ^ base[b] = known</c>.</item>
/// <item>Guess the key length and ask whether that system over GF(2) is consistent.
/// A wrong length produces <c>0 = 1</c>.</item>
/// <item>Enumerate the null space and let LZ4 pick the candidate that decompresses to the
/// declared block size. Exactly one of 2^nullity survives.</item>
/// </list>
/// <para>
/// The equations pin the eleven bytes down to eight bits of freedom — the whole vector may
/// be XORed with any constant and every equation still holds. That last byte cannot be
/// determined by algebra, only by verification. See docs/11_BASE_KEY_SOLVER.md.
/// </para>
/// </summary>
public static class BaseKeySolver
{
    /// <summary>Bytes read from each side of a pair. 6 KiB yields ~750 windows per pair.</summary>
    public const int SampleBytes = 6144;

    public const int WindowSize = 8;

    public const int MinBaseLength = 2;

    /// <summary>Upper bound; 64 bytes is a 512-bit periodic keystream, far past plausible.</summary>
    public const int MaxBaseLength = 64;

    /// <summary>Above this many free bits, brute force stops being sensible.</summary>
    public const int MaxNullity = 12;

    /// <summary>Candidate pairs to inspect. More pairs means more equations but more IO.</summary>
    public const int MaxPairs = 48;

    /// <summary>A pair must agree on this many windows before it counts as same-plaintext.</summary>
    private const int MinWindowsPerPair = 16;

    /// <summary>How many consistent lengths to try, fewest free bits first.</summary>
    private const int MaxLengthsToTry = 8;

    private static readonly Random Rng = new(20261107);

    // ------------------------------------------------------------------ public API

    public static BaseKeySolveResult Solve(
        IReadOnlyList<BundleProbe> probes,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var result = new BaseKeySolveResult { Probes = probes.Count };

        if (probes.Count < 4)
        {
            result.Failure = $"可用的包太少（{probes.Count} 个）。求解至少要 4 个。";
            result.Elapsed = sw.Elapsed;
            return result;
        }

        // ---- stage 1: candidate pairs -------------------------------------
        log?.Report($"按内容指纹给 {probes.Count:N0} 个样本分组…");
        var pairs = FindPairs(probes);
        result.PairsConsidered = pairs.Count;

        if (pairs.Count == 0)
        {
            result.Failure = "找不到\"内容相同、数据起点不同\"的包对。没有包对就列不出方程。";
            result.Elapsed = sw.Elapsed;
            return result;
        }

        // ---- stage 2: which pairs are really the same plaintext -----------
        log?.Report($"检验 {pairs.Count} 个候选包对是否同明文…");
        int usable = 0;
        long windows = 0;

        foreach (var pair in pairs)
        {
            ct.ThrowIfCancellationRequested();
            Measure(pair);

            if (pair.Windows.Count >= MinWindowsPerPair)
            {
                usable++;
                windows += pair.Windows.Count;
            }
            else
            {
                pair.Windows.Clear();          // not the same content; drop it quietly
            }
        }

        result.PairsUsable = usable;
        result.Windows = (int)windows;

        if (usable == 0)
        {
            result.Failure =
                "没有任何包对通过\"连续 8 字节异或恒定\"的检验。这通常说明清单里的 key " +
                "不属于这批文件。";
            result.Elapsed = sw.Elapsed;
            return result;
        }

        log?.Report($"可用包对 {usable} 个，恒定窗口 {windows:N0} 个。开始按长度试解…");

        // ---- stage 3: one elimination per candidate length -----------------
        var consistent = new List<(int Length, ulong[] Solution, List<ulong[]> Null, int Rank, int Equations)>();

        for (int length = MinBaseLength; length <= MaxBaseLength; length++)
        {
            ct.ThrowIfCancellationRequested();

            int nvars = length * 8;
            int words = (nvars + 63) >> 6;

            var rows = BuildRows(pairs, nvars, words);
            if (rows is null || rows.Count < nvars) continue;

            var outcome = Eliminate(rows, nvars, words);
            result.Trials.Add(new BaseLengthTrial(
                length, rows.Count, outcome.Rank,
                outcome.NullSpace?.Count ?? 0, outcome.Consistent));

            if (!outcome.Consistent) continue;

            log?.Report($"  Lb={length,2} 自洽 · 方程 {rows.Count,5} 秩 {outcome.Rank,3} 零空间 {outcome.NullSpace!.Count,2}");
            consistent.Add((length, outcome.Solution!, outcome.NullSpace!, outcome.Rank, rows.Count));
        }

        if (consistent.Count == 0)
        {
            result.Failure =
                "长度 2..64 全部导致方程组矛盾。包对里混进了不同明文的样本，或者 key 不对。";
            result.Elapsed = sw.Elapsed;
            return result;
        }

        // Fewest free bits first: that is the cheapest brute force. Ties go to the shorter
        // length, because 22 and 33 are just the 11-byte period restated.
        consistent.Sort((x, y) =>
        {
            int c = x.Null.Count.CompareTo(y.Null.Count);
            return c != 0 ? c : x.Length.CompareTo(y.Length);
        });

        var verifier = new Verifier(probes);
        if (!verifier.Ready)
        {
            result.Failure = "找不到可用于 LZ4 校验的参照包。";
            result.Elapsed = sw.Elapsed;
            return result;
        }

        // ---- stage 4: let LZ4 decide --------------------------------------
        foreach (var (length, solution, nullSpace, rank, equations) in consistent.Take(MaxLengthsToTry))
        {
            int n = nullSpace.Count;
            if (n > MaxNullity)
            {
                log?.Report($"  Lb={length} 零空间 {n} 维，枚举量太大，跳过");
                continue;
            }

            log?.Report($"  Lb={length}：枚举 {1 << n} 个候选…");
            int combos = 1 << n;

            for (int combo = 0; combo < combos; combo++)
            {
                ct.ThrowIfCancellationRequested();

                var v = (ulong[])solution.Clone();
                for (int k = 0; k < n; k++)
                {
                    if (((combo >> k) & 1) == 0) continue;
                    var basis = nullSpace[k];
                    for (int w = 0; w < v.Length; w++) v[w] ^= basis[w];
                }

                var candidate = BitsToBytes(v, length);
                result.CandidatesTried++;

                if (!verifier.Accepts(candidate)) continue;

                result.BaseKey = candidate;
                result.Equations = equations;
                result.Rank = rank;
                result.Nullity = n;
                result.Elapsed = sw.Elapsed;
                return result;
            }

            log?.Report($"  Lb={length}：没有候选通过 LZ4 校验");
        }

        result.Failure = "解出了自洽的方程组，但没有任何候选能通过 LZ4 校验。";
        result.Elapsed = sw.Elapsed;
        return result;
    }

    // ------------------------------------------------------------------ pairing

    private static List<ProbePair> FindPairs(IReadOnlyList<BundleProbe> probes)
    {
        var groups = new Dictionary<string, List<BundleProbe>>(StringComparer.Ordinal);
        foreach (var p in probes)
        {
            if (!groups.TryGetValue(p.ContentId, out var list))
            {
                list = new List<BundleProbe>();
                groups[p.ContentId] = list;
            }
            list.Add(p);
        }

        var pairs = new List<ProbePair>();
        foreach (var group in groups.Values)
        {
            if (group.Count < 2) continue;

            var byStart = new Dictionary<long, List<BundleProbe>>();
            foreach (var p in group)
            {
                if (!byStart.TryGetValue(p.DataStart, out var list))
                {
                    list = new List<BundleProbe>();
                    byStart[p.DataStart] = list;
                }
                list.Add(p);
            }

            if (byStart.Count < 2) continue;

            var starts = byStart.Keys.OrderBy(k => k).ToArray();
            for (int i = 0; i < starts.Length; i++)
            {
                for (int j = 0; j < starts.Length; j++)
                {
                    if (i == j) continue;
                    pairs.Add(new ProbePair { A = byStart[starts[i]][0], B = byStart[starts[j]][0] });
                }
            }
        }

        Shuffle(pairs);
        return pairs.Count <= MaxPairs ? pairs : pairs.GetRange(0, MaxPairs);
    }

    private static void Shuffle<T>(IList<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // ------------------------------------------------------------------ measuring

    /// <summary>
    /// Fill <see cref="ProbePair.Windows"/> with every 8-byte window where the two archives
    /// agree.
    ///
    /// <para>
    /// The value computed here is <c>cipherA ^ cipherB ^ keyA ^ keyB</c>. If the plaintext
    /// is identical it equals <c>base[a] ^ base[b]</c>, which depends only on the two byte
    /// indices — and across eight consecutive bytes those indices do not change. Eight
    /// agreeing bytes therefore have a 2^-56 chance of being coincidence.
    /// </para>
    /// </summary>
    private static void Measure(ProbePair pair)
    {
        byte[] ca, cb;
        try
        {
            ca = ReadSlice(pair.A.Path, pair.A.DataStart, SampleBytes);
            cb = ReadSlice(pair.B.Path, pair.B.DataStart, SampleBytes);
        }
        catch
        {
            return;
        }

        Span<byte> ka = stackalloc byte[8];
        Span<byte> kb = stackalloc byte[8];
        WriteKey(pair.A.Key, ka);
        WriteKey(pair.B.Key, kb);

        int n = Math.Min(ca.Length, cb.Length);
        for (int j = 0; j + WindowSize <= n; j += WindowSize)
        {
            long oa = pair.A.DataStart + j;
            long ob = pair.B.DataStart + j;

            // Below 0x100 the file is plaintext; XORing it tells us nothing.
            if (oa < XorKeystream.HeaderSkip || ob < XorKeystream.HeaderSkip) continue;

            byte r = (byte)(ca[j] ^ cb[j] ^ ka[(int)(oa & 7)] ^ kb[(int)(ob & 7)]);
            bool same = true;

            for (int t = 1; t < WindowSize; t++)
            {
                byte v = (byte)(ca[j + t] ^ cb[j + t]
                                ^ ka[(int)((oa + t) & 7)] ^ kb[(int)((ob + t) & 7)]);
                if (v != r) { same = false; break; }
            }

            if (same) pair.Windows.Add((oa, ob, r));
        }
    }

    private static byte[] ReadSlice(string path, long offset, int count)
    {
        using var fs = File.OpenRead(path);
        if (offset >= fs.Length) return [];

        int want = (int)Math.Min(count, fs.Length - offset);
        var buffer = new byte[want];
        fs.Position = offset;

        int total = 0;
        while (total < want)
        {
            int read = fs.Read(buffer, total, want - total);
            if (read == 0) break;
            total += read;
        }

        return total == want ? buffer : buffer[..total];
    }

    private static void WriteKey(long key, Span<byte> destination)
    {
        for (int i = 0; i < 8; i++) destination[i] = (byte)(key >> (8 * i));
    }

    // ------------------------------------------------------------------ equations

    private readonly record struct Equation(ulong[] Coeff, bool Rhs);

    /// <summary>
    /// Turn the agreeing windows into GF(2) rows.
    /// Returns null when a window proves the pair is <em>not</em> the same plaintext
    /// (two equal indices must yield R = 0).
    /// </summary>
    private static List<Equation>? BuildRows(
        IReadOnlyList<ProbePair> pairs, int nvars, int words)
    {
        var rows = new List<Equation>(8192);
        var seen = new HashSet<long>();

        foreach (var pair in pairs)
        {
            foreach (var (offsetA, offsetB, r) in pair.Windows)
            {
                int ai = (int)((offsetA % nvars) >> 3);
                int bi = (int)((offsetB % nvars) >> 3);

                if (ai == bi)
                {
                    if (r != 0) return null;   // contradiction: not the same content
                    continue;                  // base[x] ^ base[x] = 0, no information
                }

                long key = ((long)ai << 16) | ((long)bi << 8) | r;
                if (!seen.Add(key)) continue;  // many windows restate the same equation

                for (int t = 0; t < 8; t++)
                {
                    var coeff = new ulong[words];
                    int x = ai * 8 + t;
                    int y = bi * 8 + t;
                    coeff[x >> 6] |= 1UL << (x & 63);
                    coeff[y >> 6] |= 1UL << (y & 63);
                    rows.Add(new Equation(coeff, ((r >> t) & 1) != 0));
                }
            }
        }

        return rows;
    }

    private readonly record struct Elimination(
        bool Consistent, int Rank, ulong[]? Solution, List<ulong[]>? NullSpace);

    private static Elimination Eliminate(List<Equation> rows, int nvars, int words)
    {
        var pivot = new Equation?[nvars];
        int rank = 0;

        foreach (var equation in rows)
        {
            var coeff = (ulong[])equation.Coeff.Clone();
            bool rhs = equation.Rhs;

            while (true)
            {
                int hb = HighestBit(coeff);
                if (hb < 0)
                {
                    if (rhs) return new Elimination(false, rank, null, null);   // 0 = 1
                    break;
                }

                if (pivot[hb] is { } p)
                {
                    for (int w = 0; w < words; w++) coeff[w] ^= p.Coeff[w];
                    rhs ^= p.Rhs;
                }
                else
                {
                    pivot[hb] = new Equation(coeff, rhs);
                    rank++;
                    break;
                }
            }
        }

        // Particular solution. Ascending pivot order works because a pivot row's highest
        // set bit is the pivot itself, so every other coefficient is smaller and already
        // decided by the time we get here.
        var solution = new ulong[words];
        for (int hb = 0; hb < nvars; hb++)
        {
            if (pivot[hb] is not { } p) continue;

            bool value = p.Rhs;
            for (int w = 0; w < words; w++)
                value ^= (BitOperations.PopCount(p.Coeff[w] & solution[w]) & 1) != 0;

            if (value) solution[hb >> 6] |= 1UL << (hb & 63);
        }

        // Null space: one basis vector per free variable.
        var nullSpace = new List<ulong[]>();
        for (int free = 0; free < nvars; free++)
        {
            if (pivot[free] is not null) continue;

            var v = new ulong[words];
            v[free >> 6] |= 1UL << (free & 63);

            for (int hb = 0; hb < nvars; hb++)
            {
                if (pivot[hb] is not { } p) continue;

                bool parity = false;
                for (int w = 0; w < words; w++)
                    parity ^= (BitOperations.PopCount(p.Coeff[w] & v[w]) & 1) != 0;

                if (parity) v[hb >> 6] |= 1UL << (hb & 63);
            }

            nullSpace.Add(v);
        }

        return new Elimination(true, rank, solution, nullSpace);
    }

    private static int HighestBit(ulong[] bits)
    {
        for (int i = bits.Length - 1; i >= 0; i--)
        {
            if (bits[i] == 0) continue;
            return i * 64 + 63 - BitOperations.LeadingZeroCount(bits[i]);
        }
        return -1;
    }

    private static byte[] BitsToBytes(ulong[] bits, int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
            bytes[i] = (byte)(bits[i >> 3] >> ((i & 7) * 8));
        return bytes;
    }

    // ------------------------------------------------------------------ verification

    /// <summary>
    /// One pre-parsed bundle used as the judge. Its block table sits in the plaintext
    /// region, so it can be parsed without knowing the answer — which is the whole point.
    /// </summary>
    private sealed class Verifier
    {
        private readonly string _path;
        private readonly long _key;
        private readonly UnityFsBundle _bundle;

        public bool Ready { get; }

        public Verifier(IReadOnlyList<BundleProbe> probes)
        {
            foreach (var probe in probes)
            {
                if (probe.Key == 0) continue;
                if (probe.FirstBlockType is not (2 or 3)) continue;

                try
                {
                    using var fs = File.OpenRead(probe.Path);
                    var bundle = UnityFsBundle.Parse(fs, probe.Key, default);
                    if (bundle.Blocks.Count == 0) continue;
                    if (bundle.Blocks[0].CompressionType is not (2 or 3)) continue;

                    _path = probe.Path;
                    _key = probe.Key;
                    _bundle = bundle;
                    Ready = true;
                    return;
                }
                catch
                {
                    // keep looking
                }
            }

            _path = string.Empty;
            _key = 0;
            _bundle = null!;
        }

        /// <summary>
        /// A wrong key makes LZ4 fail outright or produce the wrong length. The declared
        /// uncompressed size is therefore an exact test, not a heuristic.
        /// </summary>
        public bool Accepts(byte[] baseKey)
        {
            try
            {
                using var fs = File.OpenRead(_path);
                var data = _bundle.ReadBlock(fs, 0, _key, baseKey);
                return data.Length == _bundle.Blocks[0].UncompressedSize;
            }
            catch
            {
                return false;
            }
        }
    }
}
