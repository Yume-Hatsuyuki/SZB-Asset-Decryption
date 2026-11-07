using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using SzbTool.Core.Crypto;
using SzbTool.Core.Keys;
using SzbTool.Core.Manifest;

namespace SzbTool.App.Services;

public enum LogLevel
{
    Info,
    Success,
    Warn,
    Error,
}

/// <summary>One line in the shared run log.</summary>
public sealed class LogEntry
{
    private static readonly Brush InfoBrush = Freeze(Color.FromRgb(0x8B, 0x97, 0xAC));
    private static readonly Brush OkBrush = Freeze(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush WarnBrush = Freeze(Color.FromRgb(0xD2, 0x99, 0x22));
    private static readonly Brush ErrBrush = Freeze(Color.FromRgb(0xF8, 0x51, 0x49));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public required DateTime Time { get; init; }
    public required LogLevel Level { get; init; }
    public required string Text { get; init; }

    public string TimeText => Time.ToString("HH:mm:ss");

    public string LevelText => Level switch
    {
        LogLevel.Success => "成功",
        LogLevel.Warn => "警告",
        LogLevel.Error => "错误",
        _ => "信息",
    };

    public Brush LevelBrush => Level switch
    {
        LogLevel.Success => OkBrush,
        LogLevel.Warn => WarnBrush,
        LogLevel.Error => ErrBrush,
        _ => InfoBrush,
    };
}

/// <summary>
/// Everything the pages share: chosen folders, the loaded manifest, behaviour switches,
/// and one run log. Settings are persisted to <c>%LOCALAPPDATA%\SzbTool\settings.json</c>
/// so the user does not have to re-pick folders every launch.
/// </summary>
public sealed class Workspace : INotifyPropertyChanged
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SzbTool");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public static Workspace Current { get; } = new();

    private string _datRoot = string.Empty;
    private string _outputRoot = string.Empty;
    private int _parallelism = Math.Max(2, Environment.ProcessorCount);
    private bool _verifyBlocks;
    private bool _copyWhenKeyMissing;
    private string _baseKeyHex = string.Empty;
    private string _keyState = "尚未推导";
    private string _keyDetails = string.Empty;
    private int _deriving;
    private Task<bool>? _deriveTask;
    private readonly object _deriveGate = new();
    private bool _busy;

    private Workspace()
    {
        // No key here, and none on disk. It is computed from the local install on demand;
        // until then it is empty and every job that needs it refuses to start.
        BaseKey = [];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ---------------------------------------------------------------- folders

    public string DatRoot
    {
        get => _datRoot;
        set
        {
            if (!Set(ref _datRoot, value)) return;
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(DatRootSummary));
            // First time through, propose a sibling output folder so "开始" works straight away.
            if (string.IsNullOrWhiteSpace(_outputRoot) && !string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    string? parent = Directory.GetParent(value.TrimEnd('\\', '/'))?.FullName;
                    if (parent is not null) OutputRoot = Path.Combine(parent, "SZB_Restored");
                }
                catch { /* path is malformed, user will fix it */ }
            }
            Save();
        }
    }

    public string OutputRoot
    {
        get => _outputRoot;
        set
        {
            if (!Set(ref _outputRoot, value)) return;
            OnPropertyChanged(nameof(IsReady));
            OnPropertyChanged(nameof(OutputRootSummary));
            Save();
        }
    }

    public string DatRootSummary => string.IsNullOrWhiteSpace(_datRoot)
        ? "尚未选择"
        : Directory.Exists(_datRoot) ? _datRoot : $"{_datRoot}（目录不存在）";

    public string OutputRootSummary => string.IsNullOrWhiteSpace(_outputRoot) ? "尚未选择" : _outputRoot;

    // ---------------------------------------------------------------- switches

    public int Parallelism
    {
        get => _parallelism;
        set
        {
            int v = Math.Clamp(value, 1, 64);
            if (!Set(ref _parallelism, v)) return;
            Save();
        }
    }

    public bool VerifyBlocks
    {
        get => _verifyBlocks;
        set { if (Set(ref _verifyBlocks, value)) Save(); }
    }

    public bool CopyWhenKeyMissing
    {
        get => _copyWhenKeyMissing;
        set { if (Set(ref _copyWhenKeyMissing, value)) Save(); }
    }

    public string BaseKeyHex => _baseKeyHex;

    public byte[] BaseKey { get; private set; }

    public bool HasKey => BaseKey.Length > 0;

    /// <summary>Short status line for the settings page.</summary>
    public string KeyState
    {
        get => _keyState;
        private set => Set(ref _keyState, value);
    }

    /// <summary>Second line: how many bundles the answer was checked against.</summary>
    public string KeyDetails
    {
        get => _keyDetails;
        private set => Set(ref _keyDetails, value);
    }

    /// <summary>True while a derivation is running, so the UI can disable its buttons.</summary>
    public bool Deriving => Volatile.Read(ref _deriving) != 0;

    /// <summary>
    /// Work out the base key from the local install, off the UI thread.
    ///
    /// <para>
    /// Concurrent callers share one derivation: start-up may begin it while the user is
    /// already pressing Restore, and the second caller must wait for that same result
    /// rather than being told "busy" and handed an empty key.
    /// </para>
    /// </summary>
    public Task<bool> DeriveBaseKeyAsync(bool forceRederive = false)
    {
        lock (_deriveGate)
        {
            if (_deriveTask is { IsCompleted: false })
            {
                Info("推导已经在进行，等这一次的结果。");
                return _deriveTask;
            }

            if (HasKey && !forceRederive) return Task.FromResult(true);

            _deriveTask = DeriveCoreAsync(forceRederive);
            return _deriveTask;
        }
    }

    private async Task<bool> DeriveCoreAsync(bool forceRederive)
    {
        Interlocked.Exchange(ref _deriving, 1);
        OnPropertyChanged(nameof(Deriving));

        try
        {
            if (string.IsNullOrWhiteSpace(_datRoot) || !Directory.Exists(_datRoot))
            {
                KeyState = "没有可用的数据目录";
                Error("推导基础密钥需要数据目录。请先在「概览」页选到 Persistent\\dat。");
                return false;
            }

            KeyState = "正在从本地文件推导…";
            Info("开始从本地文件推导基础密钥（不联网）。");

            string dat = _datRoot;
            string? manifestPath = ManifestPath;
            string cachePath = BaseKeyCache.DefaultPath;

            // Progress<string> captures the UI context, so every step line lands on the
            // right thread without extra marshalling.
            var log = new Progress<string>(Info);

            var report = await Task.Run(() => BaseKeyDerivation.Run(
                dat,
                manifestPath: manifestPath,
                log: log,
                cachePath: cachePath,
                forceRederive: forceRederive));

            if (!report.Success)
            {
                BaseKey = [];
                _baseKeyHex = string.Empty;
                KeyState = $"推导失败：{report.Failure}";
                KeyDetails = string.Empty;
                Error($"基础密钥推导失败：{report.Failure}");
                NotifyKeyChanged();
                return false;
            }

            BaseKey = report.BaseKey!;
            _baseKeyHex = Convert.ToHexString(BaseKey).ToLowerInvariant();

            KeyState = report.FromCache
                ? $"来自上次推导（{report.CachedAt:MM-dd HH:mm}）"
                : $"刚从本地文件算出（{report.Elapsed.TotalSeconds:F1}s）";

            KeyDetails =
                $"{BaseKey.Length} 字节 · 周期 {report.Period} · " +
                $"{report.Probes:N0} 个包 · {report.PairsUsable} 对同明文 · 秩 {report.Rank}";

            Success(report.FromCache
                ? $"基础密钥已载入：{_baseKeyHex}"
                : $"基础密钥已算出：{_baseKeyHex}（{BaseKey.Length} 字节，周期 {report.Period}）");

            NotifyKeyChanged();
            return true;
        }
        catch (Exception ex)
        {
            BaseKey = [];
            _baseKeyHex = string.Empty;
            KeyState = $"推导出错：{ex.Message}";
            Error($"基础密钥推导出错：{ex.GetType().Name}: {ex.Message}");
            NotifyKeyChanged();
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _deriving, 0);
            OnPropertyChanged(nameof(Deriving));
        }
    }

    public bool TrySetBaseKey(string? text, out string message)
    {
        var parsed = XorKeystream.TryParseBaseKey(text);
        if (parsed is null || parsed.Length == 0)
        {
            message = "无法识别。支持 hex（22 个字符）或 base64；留空表示让程序自己推导。";
            return false;
        }

        BaseKey = parsed;
        _baseKeyHex = Convert.ToHexString(parsed).ToLowerInvariant();
        KeyState = $"手动指定 {parsed.Length} 字节（周期 {parsed.Length * 8}）";
        KeyDetails = "这一行是你填的，不是程序算的。";
        NotifyKeyChanged();

        message = $"已设为手动密钥（{parsed.Length} 字节）。";
        return true;
    }

    private void NotifyKeyChanged()
    {
        OnPropertyChanged(nameof(BaseKey));
        OnPropertyChanged(nameof(BaseKeyHex));
        OnPropertyChanged(nameof(BaseKeySummary));
        OnPropertyChanged(nameof(HasKey));
    }

    public string BaseKeySummary
    {
        get
        {
            if (!HasKey) return "未获取";
            string hex = _baseKeyHex;
            if (hex.Length <= 24) return hex;
            return $"{hex[..24]}…（{BaseKey.Length} 字节）";
        }
    }

    /// <summary>True while a job is running; pages use it to disable their buttons.</summary>
    public bool Busy
    {
        get => _busy;
        set => Set(ref _busy, value);
    }

    // ---------------------------------------------------------------- manifest

    public AssetManifest? Manifest { get; private set; }

    /// <summary>Where the manifest came from. Used to tell whether a cached key is stale.</summary>
    public string? ManifestPath { get; private set; }

    public string ManifestStatus { get; private set; } = "未加载。选择数据目录后会自动读取。";

    public bool ManifestLoaded => Manifest is not null;

    /// <summary>Enough state to run any job?</summary>
    public bool IsReady =>
        Manifest is not null &&
        !string.IsNullOrWhiteSpace(_outputRoot) &&
        !string.IsNullOrWhiteSpace(_datRoot) &&
        Directory.Exists(_datRoot);

    public bool TryLoadManifest(out string message)
    {
        Manifest = null;
        if (string.IsNullOrWhiteSpace(_datRoot) || !Directory.Exists(_datRoot))
        {
            ManifestStatus = "数据目录无效，无法查找清单表。";
            message = ManifestStatus;
            NotifyManifestChanged();
            return false;
        }

        try
        {
            string? path = AssetManifest.LocateLocal(_datRoot);
            if (path is null)
            {
                ManifestStatus = "在数据目录里没找到清单表（dat 下的 UX 目录）。";
                message = ManifestStatus;
                NotifyManifestChanged();
                return false;
            }

            var m = AssetManifest.Load(path);
            Manifest = m;
            ManifestPath = path;

            string tables = string.Join(" / ", m.TableCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key} {kv.Value:N0}"));

            ManifestStatus = $"已加载 {m.Count:N0} 条记录（{tables}）";
            message = ManifestStatus;
            NotifyManifestChanged();
            return true;
        }
        catch (Exception ex)
        {
            ManifestStatus = $"清单解析失败：{ex.GetType().Name}: {ex.Message}";
            message = ManifestStatus;
            NotifyManifestChanged();
            return false;
        }
    }

    private void NotifyManifestChanged()
    {
        OnPropertyChanged(nameof(Manifest));
        OnPropertyChanged(nameof(ManifestLoaded));
        OnPropertyChanged(nameof(ManifestStatus));
        OnPropertyChanged(nameof(IsReady));
    }

    // ---------------------------------------------------------------- logging

    public ObservableCollection<LogEntry> Log { get; } = new();

    public void Info(string text) => Append(LogLevel.Info, text);
    public void Success(string text) => Append(LogLevel.Success, text);
    public void Warn(string text) => Append(LogLevel.Warn, text);
    public void Error(string text) => Append(LogLevel.Error, text);

    public void ClearLog()
    {
        var app = Application.Current;
        if (app?.Dispatcher is { } d && !d.CheckAccess())
        {
            if (d.HasShutdownStarted || d.HasShutdownFinished) return;
            try { d.BeginInvoke(ClearLog); } catch (InvalidOperationException) { /* window closed */ }
            return;
        }
        Log.Clear();
    }

    /// <summary>Safe to call from worker threads; hops to the UI thread when needed.</summary>
    public void Append(LogLevel level, string text)
    {
        var app = Application.Current;
        if (app?.Dispatcher is { } d && !d.CheckAccess())
        {
            // A background fetch can finish after the window closed. Posting to a dead
            // dispatcher throws, and there is nobody left to read the line anyway.
            if (d.HasShutdownStarted || d.HasShutdownFinished) return;
            try { d.BeginInvoke(() => Append(level, text)); }
            catch (InvalidOperationException) { /* window closed mid-flight */ }
            return;
        }

        Log.Add(new LogEntry { Time = DateTime.Now, Level = level, Text = text });
        while (Log.Count > 2000) Log.RemoveAt(0);
    }

    // ---------------------------------------------------------------- persistence

    private sealed class SettingsDto
    {
        public string DatRoot { get; set; } = string.Empty;
        public string OutputRoot { get; set; } = string.Empty;
        public int Parallelism { get; set; } = Math.Max(2, Environment.ProcessorCount);
        public bool VerifyBlocks { get; set; }
        public bool CopyWhenKeyMissing { get; set; }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var dto = JsonSerializer.Deserialize<SettingsDto>(
                File.ReadAllText(SettingsPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto is null) return;

            _datRoot = dto.DatRoot ?? string.Empty;
            _outputRoot = dto.OutputRoot ?? string.Empty;
            _parallelism = Math.Clamp(dto.Parallelism <= 0 ? Environment.ProcessorCount : dto.Parallelism, 1, 64);
            _verifyBlocks = dto.VerifyBlocks;
            _copyWhenKeyMissing = dto.CopyWhenKeyMissing;

            OnPropertyChanged(nameof(DatRoot));
            OnPropertyChanged(nameof(OutputRoot));
            OnPropertyChanged(nameof(DatRootSummary));
            OnPropertyChanged(nameof(OutputRootSummary));
            OnPropertyChanged(nameof(Parallelism));
            OnPropertyChanged(nameof(VerifyBlocks));
            OnPropertyChanged(nameof(CopyWhenKeyMissing));
            OnPropertyChanged(nameof(BaseKeyHex));
            OnPropertyChanged(nameof(BaseKeySummary));
            OnPropertyChanged(nameof(HasKey));
        }
        catch (Exception ex)
        {
            Append(LogLevel.Warn, $"读取设置失败，已使用默认值：{ex.Message}");
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var dto = new SettingsDto
            {
                DatRoot = _datRoot,
                OutputRoot = _outputRoot,
                Parallelism = _parallelism,
                VerifyBlocks = _verifyBlocks,
                CopyWhenKeyMissing = _copyWhenKeyMissing,
            };
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* a read-only profile folder is not worth crashing over */ }
    }

    // ---------------------------------------------------------------- plumbing

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
