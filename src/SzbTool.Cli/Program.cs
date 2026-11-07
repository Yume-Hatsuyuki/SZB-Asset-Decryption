using System.Diagnostics;
using SzbTool.Core.Audio;
using SzbTool.Core.Crypto;
using SzbTool.Core.External;
using SzbTool.Core.Keys;
using SzbTool.Core.Manifest;
using SzbTool.Core.Media;
using SzbTool.Core.Restore;
using SzbTool.Core.UnityFs;
using SzbTool.Core.Video;

namespace SzbTool.Cli;

internal static class OptionExtensions
{
    public static string? Get(this Dictionary<string, string?> o, string key) =>
        o.TryGetValue(key, out var v) ? v : null;

    public static bool Has(this Dictionary<string, string?> o, string key) =>
        o.ContainsKey(key);

    public static int Int(this Dictionary<string, string?> o, string key, int fallback) =>
        int.TryParse(o.Get(key), out var v) ? v : fallback;

    public static long Long(this Dictionary<string, string?> o, string key, long fallback) =>
        long.TryParse(o.Get(key), out var v) ? v : fallback;
}

internal static class Program
{
    /// <summary>IProgress that prints immediately and in order (no thread-pool hop).</summary>
    private sealed class ConsoleLog : IProgress<string>
    {
        private readonly string _indent;
        public ConsoleLog(string indent) => _indent = indent;
        public void Report(string value) => Console.WriteLine(_indent + value);
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        ToolLocator.Refresh();

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        var opt = ParseArgs(args.Skip(1).ToArray());

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "scan" => CmdScan(opt),
                "restore" => CmdRestore(opt),
                "verify" => CmdVerify(opt),
                "audio" => CmdAudio(opt),
                "video" => CmdVideo(opt),
                "info" => CmdInfo(opt),
                "derive" => CmdDerive(opt),
                "selftest" => CmdSelfTest(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"错误: {ex.Message}");
            if (opt.Has("--verbose")) Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // ------------------------------------------------------------------
    //  Commands
    // ------------------------------------------------------------------

    private static int CmdScan(Dictionary<string, string?> opt)
    {
        string dat = Require(opt, "--dat");
        var manifest = LoadManifest(dat, opt);

        Console.WriteLine($"目录: {dat}");
        Console.WriteLine($"清单: {manifest.SourcePath ?? "(内置/未找到)"}  条目 {manifest.Count}");
        foreach (var kv in manifest.TableCounts)
            Console.WriteLine($"      {kv.Key,-12} {kv.Value,7} 行");
        Console.WriteLine();

        var baseKey = ResolveBaseKey(opt);
        var svc = new CensusService(dat, manifest, baseKey);
        var report = svc.Run(new Progress<(int Done, int Total)>());

        Console.WriteLine();
        Console.WriteLine("文件构成");
        Console.WriteLine(new string('-', 62));
        Console.WriteLine($"{"类型",-14}{"数量",10}{"体积",14}");
        foreach (var kv in report.ByKind.OrderByDescending(k => k.Value.Count))
        {
            Console.WriteLine($"{FileClassifier.Describe(kv.Key),-14}{kv.Value.Count,10}{FormatSize(kv.Value.Bytes),14}");
        }
        Console.WriteLine(new string('-', 62));
        Console.WriteLine($"{"合计",-14}{report.Total,10}{FormatSize(report.TotalBytes),14}");

        Console.WriteLine();
        Console.WriteLine("UnityFS 诊断");
        Console.WriteLine($"  可解析           {report.BundlesParsed}");
        Console.WriteLine($"  布局校验通过     {report.BundlesLayoutOk}");
        Console.WriteLine($"  blocksInfo 跨界  {report.BundlesStraddlingBlocksInfo}");
        Console.WriteLine($"  清单缺失         {report.BundlesWithoutManifestKey}");
        Console.WriteLine($"  体积不一致       {report.ManifestSizeMismatch}");

        Console.WriteLine();
        Console.WriteLine("blocksInfo 压缩类型分布");
        foreach (var kv in report.BlocksInfoCompression.OrderBy(k => k.Key))
            Console.WriteLine($"  type {kv.Key}: {kv.Value}");

        Console.WriteLine();
        Console.WriteLine("data_start 分布（前 10）");
        foreach (var kv in report.DataStartHistogram.OrderByDescending(k => k.Value).Take(10))
            Console.WriteLine($"  0x{kv.Key:X4} ({kv.Key,6}): {kv.Value}");

        Console.WriteLine();
        Console.WriteLine($"耗时 {report.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    private static int CmdRestore(Dictionary<string, string?> opt)
    {
        string dat = Require(opt, "--dat");
        string outDir = Require(opt, "--out");
        var manifest = LoadManifest(dat, opt);
        var baseKey = ResolveBaseKey(opt);

        var svc = new RestoreService(new RestoreOptions
        {
            DatRoot = dat,
            OutputRoot = outDir,
            Manifest = manifest,
            BaseKey = baseKey,
            Parallelism = opt.Int("--jobs", Math.Max(2, Environment.ProcessorCount)),
            VerifyBlocks = opt.Has("--verify"),
            CopyWhenKeyMissing = opt.Has("--copy-missing"),
        });

        var files = svc.EnumerateBundles();
        int limit = opt.Int("--limit", 0);
        if (limit > 0 && limit < files.Count)
        {
            files = files.Take(limit).ToList();
            Console.WriteLine($"（限制测试模式：只处理前 {limit} 个）");
        }
        Console.WriteLine($"待还原 {files.Count} 个 → {outDir}");

        int lastPct = -1;
        var progress = new Progress<RestoreProgress>(p =>
        {
            int pct = (int)(p.Fraction * 100);
            if (pct != lastPct)
            {
                lastPct = pct;
                Console.Write($"\r  {pct,3}%  {p.Done}/{p.Total}  失败 {p.Failed}  跳过 {p.Skipped}   ");
            }
        });

        var result = svc.Run(progress, files);
        Console.WriteLine();

        Console.WriteLine();
        Console.WriteLine($"成功 {result.Succeeded}  失败 {result.Failed}  跳过 {result.Skipped}");
        Console.WriteLine($"写入 {FormatSize(result.BytesWritten)}  耗时 {result.Elapsed.TotalSeconds:F1}s");

        if (result.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"失败明细（前 {Math.Min(20, result.Errors.Count)} 条）:");
            foreach (var (file, err) in result.Errors.Take(20))
                Console.WriteLine($"  {file}: {err}");
        }

        return result.Failed == 0 ? 0 : 2;
    }

    private static int CmdVerify(Dictionary<string, string?> opt)
    {
        string dat = Require(opt, "--dat");
        var manifest = LoadManifest(dat, opt);
        var baseKey = ResolveBaseKey(opt);
        int sample = opt.Int("--sample", 200);

        var all = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(dat))
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var e = manifest.Find(Path.GetFileName(f));
                if (e is null || e.Category != "All") continue;
                all.Add(f);
            }
        }

        var rng = new Random(12345);
        var pick = all.OrderBy(_ => rng.Next()).Take(Math.Min(sample, all.Count)).ToList();

        int ok = 0, bad = 0;
        foreach (var path in pick)
        {
            var entry = manifest.Find(Path.GetFileName(path))!;
            try
            {
                using var fs = File.OpenRead(path);
                var b = UnityFsBundle.Parse(fs, entry.Key, baseKey);
                if (!b.LayoutValid) { bad++; Console.WriteLine($"FAIL layout {Path.GetFileName(path)}"); continue; }

                var (vok, at, err) = b.VerifyBlocks(fs, entry.Key, baseKey);
                if (vok) ok++;
                else { bad++; Console.WriteLine($"FAIL block {Path.GetFileName(path)} #{at}: {err}"); }
            }
            catch (Exception ex)
            {
                bad++;
                Console.WriteLine($"FAIL {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"抽样 {pick.Count}  通过 {ok}  失败 {bad}");
        Console.WriteLine(ok > 0 && bad == 0 ? "base 与 key 均正确 ✅" : "存在问题 ❌");
        return bad == 0 ? 0 : 2;
    }

    private static int CmdAudio(Dictionary<string, string?> opt)
    {
        string dat = Require(opt, "--dat");
        string outDir = Require(opt, "--out");
        var manifest = LoadManifest(dat, opt);
        bool wemOnly = opt.Get("--format") is "wem";

        var svc = new AudioService(new AudioOptions
        {
            DatRoot = dat,
            OutputRoot = outDir,
            Manifest = manifest,
            DecodeToWav = !wemOnly,
            KeepWem = opt.Has("--keep-wem"),
            HonourLoop = opt.Has("--loop"),
        });

        var pkgs = svc.EnumeratePackages();
        int limitA = opt.Int("--limit", 0);
        if (limitA > 0 && limitA < pkgs.Count)
        {
            pkgs = pkgs.Take(limitA).ToList();
            Console.WriteLine($"（限制测试模式：只处理前 {limitA} 个包）");
        }
        Console.WriteLine($"音频包 {pkgs.Count} 个 → {outDir}");
        if (!wemOnly && !ToolLocator.VgmstreamAvailable)
            Console.WriteLine("⚠ 未找到 vgmstream-cli.exe，只能导出 .wem");

        var result = svc.Run(new Progress<AudioProgress>(p =>
            Console.Write($"\r  {p.PackagesDone}/{p.PackagesTotal} 包  {p.WemsExtracted} wem  {p.WavConverted} wav   ")), pkgs);

        Console.WriteLine();
        Console.WriteLine($"包 {result.Packages}  波形 {result.Wems}  转 wav {result.Wavs}  失败 {result.Failures}");
        Console.WriteLine($"耗时 {result.Elapsed.TotalSeconds:F1}s");

        foreach (var (f, e) in result.Errors.Take(10)) Console.WriteLine($"  {f}: {e}");
        return 0;
    }

    private static int CmdVideo(Dictionary<string, string?> opt)
    {
        string dat = Require(opt, "--dat");
        string outDir = Require(opt, "--out");
        var manifest = LoadManifest(dat, opt);

        var container = opt.Get("--format")?.ToLowerInvariant() switch
        {
            "mkv" => VideoContainer.MkvCopy,
            "copy" => VideoContainer.Mp4Copy,
            _ => VideoContainer.Mp4H264,
        };

        if (!ToolLocator.FfmpegAvailable)
        {
            Console.Error.WriteLine("未找到 ffmpeg.exe。请运行 tools/fetch-deps.ps1 或把它放到程序目录。");
            return 1;
        }

        var svc = new VideoService(new VideoOptions
        {
            DatRoot = dat,
            OutputRoot = outDir,
            Manifest = manifest,
            Container = container,
            DeduplicateQualityTiers = !opt.Has("--all-tiers"),
            KeepIntermediates = opt.Has("--keep-intermediate"),
        });

        var files = svc.EnumerateVideos(out int dup);
        int limitV = opt.Int("--limit", 0);
        if (limitV > 0 && limitV < files.Count)
        {
            files = files.Take(limitV).ToList();
            Console.WriteLine($"（限制测试模式：只处理前 {limitV} 个）");
        }
        Console.WriteLine($"视频 {files.Count} 个（去重跳过 {dup} 个）→ {outDir}");

        var result = svc.Run(new Progress<VideoProgress>(p =>
            Console.Write($"\r  {p.Done}/{p.Total}  成功 {p.Succeeded}  失败 {p.Failed}   ")), files);

        Console.WriteLine();
        Console.WriteLine($"成功 {result.Succeeded}  失败 {result.Failed}  耗时 {result.Elapsed.TotalSeconds:F1}s");
        foreach (var (f, e) in result.Errors.Take(10)) Console.WriteLine($"  {f}: {e}");
        return 0;
    }

    private static int CmdInfo(Dictionary<string, string?> opt)
    {
        string path = Require(opt, "--file");
        var baseKey = ResolveBaseKey(opt);
        long key = opt.Long("--key", 0);

        using var fs = File.OpenRead(path);
        var kind = FileClassifier.Classify(fs);
        Console.WriteLine($"文件: {Path.GetFileName(path)}   {FormatSize(fs.Length)}   类型: {FileClassifier.Describe(kind)}");

        switch (kind)
        {
            case DatFileKind.UnityBundle:
            {
                var b = UnityFsBundle.Parse(fs, key, baseKey);
                Console.WriteLine($"  version       {b.Version}");
                Console.WriteLine($"  unityVersion  {b.UnityVersion}  revision {b.UnityRevision}");
                Console.WriteLine($"  flags         0x{b.Flags:X}");
                Console.WriteLine($"  blocksInfo    @0x{b.BlocksInfoOffset:X}  {b.CompressedBlocksInfoSize}→{b.UncompressedBlocksInfoSize}");
                Console.WriteLine($"  data_start    0x{b.DataStart:X} ({b.DataStart})");
                Console.WriteLine($"  blocks        {b.Blocks.Count}   压缩合计 {b.TotalCompressedData}   解压合计 {b.TotalUncompressedData}");
                Console.WriteLine($"  布局校验      {(b.LayoutValid ? "通过" : "失败")}");
                Console.WriteLine($"  blocksInfo 加密 {(b.BlocksInfoWasEncrypted ? "是" : "否")}");
                foreach (var node in b.Nodes.Take(5))
                    Console.WriteLine($"  node          {node.Path}  @{node.Offset}+{node.Size}");
                break;
            }
            case DatFileKind.WwisePackage:
            {
                var p = AkpkPackage.Open(path);
                Console.WriteLine($"  chunks        {string.Join(", ", p.Chunks.Select(c => c.Tag))}");
                Console.WriteLine($"  波形          {p.Entries.Count}");
                break;
            }
            case DatFileKind.CridVideo:
            {
                var u = UsmFile.Open(path);
                Console.WriteLine($"  块数          {u.Blocks.Count}");
                Console.WriteLine($"  编码          {u.Codec}");
                Console.WriteLine($"  尺寸          {u.Width}x{u.Height} @ {u.FrameRate}fps");
                Console.WriteLine($"  视频帧        {u.VideoFrameCount}");
                Console.WriteLine($"  音轨          {u.HasAudio}");
                Console.WriteLine($"  块链闭合      {u.ChainClosed}");
                break;
            }
        }
        return 0;
    }

    /// <summary>Sanity check without any game files: encrypt a synthetic bundle and decrypt it back.</summary>
    private static int CmdSelfTest()
    {
        Console.WriteLine("自检：合成包往返");

        var baseKey = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB };  // synthetic, not the real key
        long key = 0x0123456789ABCDEF;

        // 1. keystream periodicity
        var pat = XorKeystream.BuildPeriod(baseKey, key);
        Debug.Assert(pat.Length == 88);
        for (int i = 0; i < 88 * 3; i++)
        {
            byte expect = pat[i % 88];
            byte actual = XorKeystream.ByteAt(baseKey, key, i);
            if (expect != actual)
            {
                Console.WriteLine($"  ❌ 密钥流在偏移 {i} 不一致");
                return 1;
            }
        }
        Console.WriteLine("  ✓ 密钥流周期与 ByteAt 一致");

        // 2. round trip
        var plain = new byte[4096];
        new Random(1).NextBytes(plain);
        Array.Clear(plain, 0, 0x100);

        var cipher = (byte[])plain.Clone();
        XorKeystream.Apply(cipher, 0, baseKey, key);

        for (int i = 0; i < 0x100; i++)
        {
            if (cipher[i] != plain[i])
            {
                Console.WriteLine($"  ❌ 明文区 {i} 被改动");
                return 1;
            }
        }
        Console.WriteLine("  ✓ 前 0x100 字节保持明文");

        var back = (byte[])cipher.Clone();
        XorKeystream.Apply(back, 0, baseKey, key);

        if (!back.AsSpan().SequenceEqual(plain))
        {
            Console.WriteLine("  ❌ 往返不一致");
            return 1;
        }
        Console.WriteLine("  ✓ 加解密往返一致（含非 8 字节对齐偏移）");

        // 3. chunked == whole
        var chunked = (byte[])plain.Clone();
        for (int off = 0; off < chunked.Length; off += 333)
        {
            int n = Math.Min(333, chunked.Length - off);
            XorKeystream.Apply(chunked.AsSpan(off, n), off, baseKey, key);
        }
        if (!chunked.AsSpan().SequenceEqual(cipher))
        {
            Console.WriteLine("  ❌ 分块处理与整块处理结果不同");
            return 1;
        }
        Console.WriteLine("  ✓ 任意分块边界结果一致");

        // 4. LZ4 known vector: "abcabcabcabc" = 3 literals + 9-byte match at offset 3.
        //    token 0x35 -> literal nibble 3, match nibble 5 (5 + 4 = 9)
        byte[] lz4 = [0x35, (byte)'a', (byte)'b', (byte)'c', 0x03, 0x00];
        var dec = Lz4Block.Decompress(lz4, 12);
        Console.WriteLine(dec.Length == 12 && dec.AsSpan().SequenceEqual("abcabcabcabc"u8)
            ? "  ✓ LZ4 解压正确"
            : $"  ❌ LZ4 解压错误：{System.Text.Encoding.ASCII.GetString(dec)}");

        Console.WriteLine();
        Console.WriteLine("自检完成");
        return 0;
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static AssetManifest LoadManifest(string datRoot, Dictionary<string, string?> opt)
    {
        string? path = opt.Get("--manifest");
        if (path is null) path = AssetManifest.LocateLocal(datRoot);

        if (path is null || !File.Exists(path))
            throw new FileNotFoundException(
                "未找到 manifest。用 --manifest 指定，或确认 --dat 指向 Persistent/dat。");

        return AssetManifest.Load(path);
    }

    private static byte[] ResolveBaseKey(Dictionary<string, string?> opt)
    {
        string? manualText = opt.Get("--base");
        if (!string.IsNullOrWhiteSpace(manualText))
        {
            var manual = XorKeystream.TryParseBaseKey(manualText);
            if (manual is not { Length: > 0 })
                throw new ArgumentException($"--base 无法解析：{manualText}（应为 hex 或 base64）");

            Console.WriteLine($"基础密钥：来自 --base，{manual.Length} 字节，周期 {manual.Length * 8}");
            return manual;
        }

        string? dat = opt.Get("--dat");
        if (string.IsNullOrWhiteSpace(dat) || !Directory.Exists(dat))
        {
            Console.Error.WriteLine(
                "基础密钥要从本地游戏文件推导，但没给 --dat。\n" +
                "  用 --dat <Persistent/dat>，或者用 --base 手动提供一个。");
            return [];
        }

        Console.WriteLine("基础密钥：从本地文件推导（不联网）");
        var report = BaseKeyDerivation.Run(
            dat,
            log: new ConsoleLog("  "),
            cachePath: opt.Has("--no-cache") ? null : BaseKeyCache.DefaultPath,
            forceRederive: opt.Has("--rederive"));

        if (!report.Success)
        {
            Console.Error.WriteLine($"基础密钥：推导失败——{report.Failure}");
            return [];
        }

        return AnnounceBaseKey(report);
    }

    private static byte[] AnnounceBaseKey(BaseKeyDerivationReport report)
    {
        var key = report.BaseKey!;
        Console.WriteLine(
            $"基础密钥：{Convert.ToHexString(key).ToLowerInvariant()}  " +
            $"（{key.Length} 字节，周期 {report.Period}，试了 {report.CandidatesTried} 个候选，" +
            $"{report.Elapsed.TotalSeconds:F1}s）");
        return key;
    }

    private static int CmdDerive(Dictionary<string, string?> opt)
    {
        string dat = Require(opt, "--dat");
        if (!Directory.Exists(dat))
        {
            Console.Error.WriteLine($"目录不存在：{dat}");
            return 2;
        }

        Console.WriteLine("只做一件事：从本地游戏文件把 base 密钥算出来。此过程不访问网络。");
        Console.WriteLine();

        var report = BaseKeyDerivation.Run(
            dat,
            log: new ConsoleLog("  "),
            cachePath: opt.Has("--no-cache") ? null : BaseKeyCache.DefaultPath,
            forceRederive: opt.Has("--rederive"));

        Console.WriteLine();
        if (!report.Success)
        {
            Console.Error.WriteLine($"推导失败：{report.Failure}");
            return 1;
        }

        string hex = Convert.ToHexString(report.BaseKey!).ToLowerInvariant();
        Console.WriteLine($"base    = {hex}");
        Console.WriteLine($"          {report.BaseKey!.Length} 字节，周期 {report.Period}");
        Console.WriteLine($"base64  = {Convert.ToBase64String(report.BaseKey!)}");
        Console.WriteLine();
        Console.WriteLine($"依据：{report.Probes:N0} 个包，{report.PairsUsable} 对同明文，" +
                          $"{report.Windows:N0} 个窗口，秩 {report.Rank}，零空间 {report.Nullity} 位");
        Console.WriteLine($"耗时：{report.Elapsed.TotalSeconds:F1}s");

        if (opt.Has("--check"))
        {
            var given = XorKeystream.TryParseBaseKey(opt.Get("--check"));
            bool same = given is not null && given.AsSpan().SequenceEqual(report.BaseKey);
            Console.WriteLine(same
                ? "对照：与 --check 给出的值一致"
                : "对照：与 --check 给出的值不同");
            return same ? 0 : 1;
        }

        return 0;
    }

    private static string Require(Dictionary<string, string?> opt, string name)
    {
        var v = opt.Get(name);
        if (string.IsNullOrWhiteSpace(v))
            throw new ArgumentException($"缺少必需参数 {name}。用 --help 查看用法。");
        return v;
    }

    private static Dictionary<string, string?> ParseArgs(string[] args)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith('-')) continue;

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                dict[a] = args[i + 1];
                i++;
            }
            else dict[a] = null;
        }
        return dict;
    }

    private static int Unknown(string cmd)
    {
        Console.Error.WriteLine($"未知命令: {cmd}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            SZB Asset Decryption — command line

            用法:
              SzbTool.Cli derive  --dat <Persistent/dat> [--check hex:..]
              SzbTool.Cli scan    --dat <dir> [--manifest <file>]
              SzbTool.Cli restore --dat <dir> --out <dir> [--base hex:..] [--jobs N]
                                  [--verify] [--copy-missing] [--manifest <file>]
              SzbTool.Cli verify  --dat <dir> [--sample N] [--base hex:..]
              SzbTool.Cli audio   --dat <dir> --out <dir> [--format wav|wem]
                                  [--keep-wem] [--loop]
              SzbTool.Cli video   --dat <dir> --out <dir> [--format mp4|mkv|copy]
                                  [--all-tiers] [--keep-intermediate]
              SzbTool.Cli info    --file <one dat file> [--key <int64>] [--base hex:..]
              SzbTool.Cli selftest

            说明:
              --dat     游戏的 ShadowverseWB_Data/Persistent/dat 目录
              --out     输出根目录（会按资源真实路径建树）
              --base    手动指定基础密钥（hex 或 base64），跳过推导
              --check   仅 derive：给一个已知值，比对本机推导结果
              --verify  还原前逐块校验（慢，但能立刻发现 key 不对）
              --manifest 默认自动在 dat 里找游戏自带的清单

            基础密钥怎么来：
              不在程序里，也不从网上拉。derive 会读游戏自带的清单拿到每个包的 key，
              再在本地文件里找"内容相同、数据起点不同"的包对，列 GF(2) 线性方程组，
              高斯消元定出长度，最后用 LZ4 从 2^8 个候选里挑出唯一解。
              全程只读本地文件，约 20 秒。其他命令需要密钥时会自动走同一条路，
              也可以用 --base 直接给。

            依赖:
              audio 需要 vgmstream-cli.exe，video 需要 ffmpeg.exe。
              两者都可以用 tools/fetch-deps.ps1 下载。
            """);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return $"{v:0.##} {units[u]}";
    }
}
