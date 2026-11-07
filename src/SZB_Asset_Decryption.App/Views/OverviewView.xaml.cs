using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SzbAssetDecryption.App.Services;
using SzbAssetDecryption.Core.External;
using SzbAssetDecryption.Core.Restore;

namespace SzbAssetDecryption.App.Views;

public partial class OverviewView : UserControl
{
    private CancellationTokenSource? _cts;
    private bool _initialised;

    public ObservableCollection<StatItem> Stats { get; } = new();

    public ObservableCollection<StatItem> Kinds { get; } = new();

    public OverviewView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Workspace.Current.PropertyChanged += (_, _) => RefreshManifestState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // TabControl re-parents its content, so Loaded can fire more than once.
        if (_initialised) return;
        _initialised = true;

        var w = Workspace.Current;

        if (!w.ManifestLoaded && !string.IsNullOrWhiteSpace(w.DatRoot) && Directory.Exists(w.DatRoot))
        {
            if (w.TryLoadManifest(out string msg)) w.Success(msg);
            else w.Warn(msg);
        }

        RefreshManifestState();
        RefreshDependencies();
    }

    private void RefreshManifestState()
    {
        bool ok = Workspace.Current.ManifestLoaded;
        ManifestDot.Fill = ok
            ? (Brush)FindResource("Success")
            : (Brush)FindResource("TextMuted");
    }

    private void RefreshDependencies()
    {
        ToolLocator.Refresh();

        var missing = new List<string>();
        if (!ToolLocator.FfmpegAvailable) missing.Add("ffmpeg（视频相关功能不可用）");
        if (!ToolLocator.VgmstreamAvailable) missing.Add("vgmstream-cli（音频无法转 wav）");

        if (missing.Count == 0)
        {
            DepBar.Visibility = Visibility.Collapsed;
            Workspace.Current.Info("依赖检查通过：ffmpeg 与 vgmstream-cli 均可用。");
            return;
        }

        DepText.Text = "缺少外部工具：" + string.Join("；", missing) +
                       "。把可执行文件放进程序目录的 Dependencies 文件夹，或在「设置」里查看下载说明。";
        DepBar.Visibility = Visibility.Visible;
        Workspace.Current.Warn("依赖缺失：" + string.Join("；", missing));
    }

    // ---------------------------------------------------------------- folders

    private void BrowseDat_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择 Persistent/dat 目录" };
        if (dlg.ShowDialog() != true) return;

        var w = Workspace.Current;
        w.DatRoot = dlg.FolderName;

        if (w.TryLoadManifest(out string msg)) w.Success(msg);
        else w.Warn(msg);
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择输出目录" };
        if (dlg.ShowDialog() != true) return;
        Workspace.Current.OutputRoot = dlg.FolderName;
    }

    private void ReloadManifest_Click(object sender, RoutedEventArgs e)
    {
        var w = Workspace.Current;
        if (w.TryLoadManifest(out string msg)) w.Success(msg);
        else w.Warn(msg);
    }

    // ---------------------------------------------------------------- census

    private async void RunCensus_Click(object sender, RoutedEventArgs e)
    {
        var w = Workspace.Current;

        if (string.IsNullOrWhiteSpace(w.DatRoot) || !Directory.Exists(w.DatRoot))
        {
            w.Error("请先选择一个有效的数据目录。");
            return;
        }

        if (!w.ManifestLoaded && !w.TryLoadManifest(out string loadMsg))
        {
            w.Warn(loadMsg + " 体检仍会继续，但无法比对清单里的尺寸。");
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        Stats.Clear();
        Kinds.Clear();
        w.Info("开始体检……");

        var progress = new Progress<(int Done, int Total)>(p =>
        {
            CensusBar.Value = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
        });

        var svc = new CensusService(w.DatRoot, w.Manifest!, w.BaseKey);

        try
        {
            var report = await Task.Run(() => svc.Run(progress, _cts.Token));
            Fill(report);
        }
        catch (Exception ex)
        {
            w.Error($"体检失败：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void CancelCensus_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Workspace.Current.Warn("已请求取消体检。");
    }

    private void Fill(CensusReport r)
    {
        Stats.Clear();
        Stats.Add(new StatItem("文件总数", Fmt.Count(r.Total)));
        Stats.Add(new StatItem("总体积", Fmt.Bytes(r.TotalBytes)));
        Stats.Add(new StatItem("Unity 包", Fmt.Count(r.BundlesParsed)));
        Stats.Add(new StatItem("结构自洽", Fmt.Count(r.BundlesLayoutOk)));
        Stats.Add(new StatItem("跨界加密", Fmt.Count(r.BundlesStraddlingBlocksInfo)));
        Stats.Add(new StatItem("清单缺失", Fmt.Count(r.BundlesWithoutManifestKey)));
        Stats.Add(new StatItem("尺寸不符", Fmt.Count(r.ManifestSizeMismatch)));
        Stats.Add(new StatItem("耗时", Fmt.Duration(r.Elapsed)));

        Kinds.Clear();
        foreach (var kv in r.ByKind.OrderByDescending(k => k.Value.Bytes))
        {
            Kinds.Add(new StatItem(
                FileClassifier.Describe(kv.Key),
                Fmt.Count(kv.Value.Count),
                Fmt.Bytes(kv.Value.Bytes)));
        }

        bool hasKinds = Kinds.Count > 0;
        KindBox.Visibility = hasKinds ? Visibility.Visible : Visibility.Collapsed;
        KindTitle.Visibility = hasKinds ? Visibility.Visible : Visibility.Collapsed;
        KindList.Visibility = hasKinds ? Visibility.Visible : Visibility.Collapsed;

        var w = Workspace.Current;
        w.Success($"体检完成：{r.Total:N0} 个文件，{Fmt.Bytes(r.TotalBytes)}，" +
                  $"Unity 包 {r.BundlesParsed:N0} 个，结构自洽 {r.BundlesLayoutOk:N0} 个，" +
                  $"其中 {r.BundlesStraddlingBlocksInfo:N0} 个的块表跨过了加密起点。");

        if (r.BundlesParsed > 0 && r.BundlesLayoutOk < r.BundlesParsed)
            w.Warn($"有 {r.BundlesParsed - r.BundlesLayoutOk:N0} 个包的结构校验没通过，" +
                   "可能是清单里的 key 不对，或者游戏版本和本文档针对的版本不同。");

        if (r.BundlesWithoutManifestKey > 0)
            w.Warn($"有 {r.BundlesWithoutManifestKey:N0} 个文件在清单里找不到记录。");

        var comp = string.Join("、", r.BlocksInfoCompression
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"{(kv.Key == 0 ? "stored" : kv.Key switch { 1 => "LZMA", 2 => "LZ4", 3 => "LZ4HC", _ => $"0x{kv.Key:X}" })} {kv.Value}"));
        if (comp.Length > 0) w.Info($"块表压缩方式分布：{comp}。");
    }

    private void SetBusy(bool busy)
    {
        Workspace.Current.Busy = busy;
        RunCensus.IsEnabled = !busy;
        CancelCensus.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy) CensusBar.Value = 0;
    }
}
