using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SzbAssetDecryption.App.Services;
using SzbAssetDecryption.Core.External;
using SzbAssetDecryption.Core.Media;
using SzbAssetDecryption.Core.Restore;

namespace SzbAssetDecryption.App.Views;

public partial class VideoView : UserControl
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<StatItem> Stats { get; } = new();

    public VideoView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDependency();
    }

    private void RefreshDependency()
    {
        ToolLocator.Refresh();
        if (ToolLocator.FfmpegAvailable)
        {
            DepBar.Visibility = Visibility.Collapsed;
            return;
        }

        DepText.Text = "没有找到 ffmpeg，视频转换无法进行。把 ffmpeg.exe 放进程序目录的 Dependencies 文件夹，" +
                       "或放在 PATH 里。注意需要带 libx264 的完整版本。";
        DepBar.Visibility = Visibility.Visible;
    }

    private VideoContainer SelectedContainer =>
        CbCopy.IsChecked == true ? VideoContainer.Mp4Copy
        : CbMkv.IsChecked == true ? VideoContainer.MkvCopy
        : VideoContainer.Mp4H264;

    private static string DescribeContainer(VideoContainer c) => c switch
    {
        VideoContainer.Mp4Copy => "MP4 · 直接封装",
        VideoContainer.MkvCopy => "MKV · 直接封装",
        _ => "MP4 · H.264",
    };

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!Workspace.Current.IsReady)
        {
            Workspace.Current.Error("数据目录或清单还没准备好。请回到「概览」页检查。");
            return;
        }

        _ = RunAsync(ParseLimit());
    }

    /// <summary>Runs the conversion. <paramref name="limit"/> of 0 means every movie.</summary>
    public async Task RunAsync(int limit)
    {
        var w = Workspace.Current;
        LimitBox.Text = limit > 0 ? limit.ToString() : "0";

        if (!ToolLocator.FfmpegAvailable)
        {
            w.Error("缺少 ffmpeg，无法转换视频。请先在「设置」页查看依赖说明。");
            return;
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        Stats.Clear();
        Bar.Value = 0;

        var container = SelectedContainer;
        w.Info($"开始转换动画，输出格式 {DescribeContainer(container)}。");

        var opts = new VideoOptions
        {
            DatRoot = w.DatRoot,
            OutputRoot = Path.Combine(w.OutputRoot, "Video"),
            Manifest = w.Manifest!,
            Container = container,
            DeduplicateQualityTiers = DedupCheck.IsChecked == true,
            Cancellation = _cts.Token,
        };

        var progress = new Progress<VideoProgress>(p =>
        {
            Bar.Value = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
            StateText.Text = $"{p.Done:N0} / {p.Total:N0}";
            CurrentFile.Text = p.CurrentFile;
            Stats.Clear();
            Stats.Add(new StatItem("进度", $"{p.Done:N0}/{p.Total:N0}"));
            Stats.Add(new StatItem("成功", p.Succeeded.ToString("N0")));
            Stats.Add(new StatItem("失败", p.Failed.ToString("N0")));
        });

        try
        {
            var svc = new VideoService(opts);

            var (result, skipped) = await Task.Run(() =>
            {
                var all = svc.EnumerateVideos(out int dupes);
                var files = limit > 0 ? all.Take(limit).ToList() : all;
                w.Info($"待处理 {files.Count:N0} 个动画" +
                       (dupes > 0 ? $"，已按画质档去重 {dupes:N0} 个。" : "。"));
                var r = svc.Run(progress, files);
                r.SkippedDuplicates = dupes;
                return (r, dupes);
            }, _cts.Token);

            Stats.Clear();
            Stats.Add(new StatItem("动画总数", result.Total.ToString("N0")));
            Stats.Add(new StatItem("成功", result.Succeeded.ToString("N0")));
            Stats.Add(new StatItem("失败", result.Failed.ToString("N0")));
            Stats.Add(new StatItem("去重跳过", result.SkippedDuplicates.ToString("N0")));
            Stats.Add(new StatItem("耗时", Fmt.Duration(result.Elapsed)));

            if (result.Total == 0)
                w.Warn("没识别到动画文件。请确认清单里有 Category 为 Movie 的记录。");
            else if (result.Failed == 0)
                w.Success($"完成：{result.Succeeded:N0} 个动画转换成功，耗时 {Fmt.Duration(result.Elapsed)}。");
            else
                w.Warn($"完成：成功 {result.Succeeded:N0}，失败 {result.Failed:N0}，" +
                       $"耗时 {Fmt.Duration(result.Elapsed)}。");

            foreach (var (file, error) in result.Errors.Take(30))
                w.Error($"{file} — {error}");
        }
        catch (OperationCanceledException)
        {
            w.Warn("已取消。");
        }
        catch (Exception ex)
        {
            w.Error($"视频转换中断：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Workspace.Current.Warn("已请求取消，正在收尾……");
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Workspace.Current.OutputRoot))
        {
            Workspace.Current.Warn("输出目录还没设置。");
            return;
        }

        string path = Path.Combine(Workspace.Current.OutputRoot, "Video");
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Workspace.Current.Error($"打不开目录：{ex.Message}");
        }
    }

    private int ParseLimit()
        => int.TryParse(LimitBox.Text?.Trim(), out int v) && v > 0 ? v : 0;

    private void SetBusy(bool busy)
    {
        Workspace.Current.Busy = busy;
        StartBtn.IsEnabled = !busy;
        CancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CbH264.IsEnabled = CbCopy.IsEnabled = CbMkv.IsEnabled = !busy;
        StateText.Text = busy ? "运行中…" : "就绪";
        if (busy) CurrentFile.Text = string.Empty;
    }
}
