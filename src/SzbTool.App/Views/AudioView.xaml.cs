using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SzbTool.App.Services;
using SzbTool.Core.External;
using SzbTool.Core.Restore;

namespace SzbTool.App.Views;

public partial class AudioView : UserControl
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<StatItem> Stats { get; } = new();

    public AudioView()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDependency();
    }

    private void RefreshDependency()
    {
        ToolLocator.Refresh();
        if (ToolLocator.VgmstreamAvailable)
        {
            DepBar.Visibility = Visibility.Collapsed;
            KeepWemCheck.IsEnabled = true;
            return;
        }

        DepText.Text = "没有找到 vgmstream-cli，本次只能输出 .wem，无法转 wav。" +
                       "把 vgmstream-cli.exe 放进程序目录的 Dependencies 文件夹即可。";
        DepBar.Visibility = Visibility.Visible;
    }

    private void DecodeChanged(object sender, RoutedEventArgs e)
    {
        if (KeepWemCheck is null) return;
        KeepWemCheck.IsEnabled = DecodeCheck.IsChecked == true;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!Workspace.Current.IsReady)
        {
            Workspace.Current.Error("数据目录或清单还没准备好。请回到「概览」页检查。");
            return;
        }

        _ = RunAsync(ParseLimit());
    }

    /// <summary>Runs the extraction. <paramref name="limit"/> of 0 means every package.</summary>
    public async Task RunAsync(int limit)
    {
        var w = Workspace.Current;
        LimitBox.Text = limit > 0 ? limit.ToString() : "0";

        bool decode = DecodeCheck.IsChecked == true;
        if (decode && !ToolLocator.VgmstreamAvailable)
        {
            decode = false;
            w.Warn("vgmstream 缺失，本次只输出 .wem。");
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        Stats.Clear();
        Bar.Value = 0;
        w.Info("开始提取音频……");

        var opts = new AudioOptions
        {
            DatRoot = w.DatRoot,
            OutputRoot = Path.Combine(w.OutputRoot, "Audio"),
            Manifest = w.Manifest!,
            DecodeToWav = decode,
            KeepWem = KeepWemCheck.IsChecked == true,
            HonourLoop = LoopCheck.IsChecked == true,
            Cancellation = _cts.Token,
        };

        var progress = new Progress<AudioProgress>(p =>
        {
            Bar.Value = p.PackagesTotal == 0 ? 0 : p.PackagesDone * 100.0 / p.PackagesTotal;
            StateText.Text = $"包 {p.PackagesDone:N0} / {p.PackagesTotal:N0}";
            CurrentFile.Text = p.CurrentPackage;
            Stats.Clear();
            Stats.Add(new StatItem("音频包", p.PackagesDone.ToString("N0")));
            Stats.Add(new StatItem("拆出 wem", p.WemsExtracted.ToString("N0")));
            Stats.Add(new StatItem("转成 wav", p.WavConverted.ToString("N0")));
            Stats.Add(new StatItem("失败", p.WavFailed.ToString("N0")));
        });

        try
        {
            var svc = new AudioService(opts);
            var result = await Task.Run(() =>
            {
                var all = svc.EnumeratePackages();
                var packages = limit > 0 ? all.Take(limit).ToList() : all;
                w.Info($"待处理 {packages.Count:N0} 个音频包（目录内共 {all.Count:N0} 个）。");
                return svc.Run(progress, packages);
            }, _cts.Token);

            Stats.Clear();
            Stats.Add(new StatItem("音频包", result.Packages.ToString("N0")));
            Stats.Add(new StatItem("拆出 wem", result.Wems.ToString("N0")));
            Stats.Add(new StatItem("转成 wav", result.Wavs.ToString("N0")));
            Stats.Add(new StatItem("失败", result.Failures.ToString("N0")));
            Stats.Add(new StatItem("耗时", Fmt.Duration(result.Elapsed)));

            if (result.Packages == 0)
                w.Warn("没识别到音频包。请确认数据目录下有 Category 为 Sound 的 .pck 文件。");
            else if (result.Failures == 0)
                w.Success($"完成：{result.Packages:N0} 个包，拆出 {result.Wems:N0} 个 wem，" +
                          $"转成 {result.Wavs:N0} 个 wav，耗时 {Fmt.Duration(result.Elapsed)}。");
            else
                w.Warn($"完成：拆出 {result.Wems:N0} 个 wem，转成 {result.Wavs:N0} 个 wav，" +
                       $"失败 {result.Failures:N0}，耗时 {Fmt.Duration(result.Elapsed)}。");

            foreach (var (file, error) in result.Errors.Take(30))
                w.Error($"{file} — {error}");
        }
        catch (OperationCanceledException)
        {
            w.Warn("已取消。");
        }
        catch (Exception ex)
        {
            w.Error($"音频提取中断：{ex.GetType().Name}: {ex.Message}");
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

        string path = Path.Combine(Workspace.Current.OutputRoot, "Audio");
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
        StateText.Text = busy ? "运行中…" : "就绪";
        if (busy) CurrentFile.Text = string.Empty;
    }
}
