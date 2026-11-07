using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SzbAssetDecryption.App.Services;
using SzbAssetDecryption.Core.Restore;

namespace SzbAssetDecryption.App.Views;

public partial class AssetView : UserControl
{
    private CancellationTokenSource? _cts;

    public ObservableCollection<StatItem> Stats { get; } = new();

    public AssetView() => InitializeComponent();

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        // A stale dat root is the most common reason a run does nothing, so check it loudly.
        if (!Workspace.Current.IsReady)
        {
            Workspace.Current.Error(
                Workspace.Current.ManifestLoaded
                    ? "输出目录还没设置。请回到「概览」页选一个输出目录。"
                    : "还没读取到清单，或数据目录无效。请回到「概览」页检查目录后点「重新读取清单」。");
            return;
        }

        _ = RunAsync(ParseLimit());
    }

    /// <summary>Runs the restore. <paramref name="limit"/> of 0 means everything.</summary>
    public async Task RunAsync(int limit)
    {
        var w = Workspace.Current;
        LimitBox.Text = limit > 0 ? limit.ToString() : "0";

        // The key is derived from the local files, not fetched. First run pays ~20 seconds.
        if (!w.HasKey)
        {
            w.Warn("还没有基础密钥，先从本地文件推导一次。");
            if (!await w.DeriveBaseKeyAsync())
            {
                w.Error("基础密钥为空，无法解密。到「设置」页点「计算」可以看到具体卡在哪一步。");
                return;
            }
        }

        _cts = new CancellationTokenSource();
        SetBusy(true);
        Stats.Clear();
        Bar.Value = 0;

        w.Info(limit > 0 ? $"开始还原，本次上限 {limit} 个包。" : "开始还原全部包。");

        var opts = new RestoreOptions
        {
            DatRoot = w.DatRoot,
            OutputRoot = w.OutputRoot,
            Manifest = w.Manifest!,
            BaseKey = w.BaseKey,
            Parallelism = w.Parallelism,
            VerifyBlocks = w.VerifyBlocks,
            CopyWhenKeyMissing = w.CopyWhenKeyMissing,
            Cancellation = _cts.Token,
        };

        var progress = new Progress<RestoreProgress>(p =>
        {
            Bar.Value = p.Fraction * 100.0;
            StateText.Text = $"{p.Done:N0} / {p.Total:N0}";
            CurrentFile.Text = p.CurrentFile;
            UpdateStats(p.Done, p.Total, p.Done - p.Failed - p.Skipped, p.Failed, p.Skipped, p.BytesWritten);
        });

        try
        {
            var svc = new RestoreService(opts);

            var result = await Task.Run(() =>
            {
                var all = svc.EnumerateBundles();
                var files = limit > 0 ? all.Take(limit).ToList() : all;
                w.Info($"待处理 {files.Count:N0} 个包（目录内共识别到 {all.Count:N0} 个）。");
                return svc.Run(progress, files);
            }, _cts.Token);

            UpdateStats(result.Total, result.Total, result.Succeeded, result.Failed, result.Skipped, result.BytesWritten);

            if (result.Total == 0)
            {
                w.Warn("一个包都没识别到。请确认数据目录选的是 Persistent/dat，而不是它的上级或下级目录。");
            }
            else if (result.Failed == 0 && result.Skipped == 0)
            {
                w.Success($"全部完成：成功 {result.Succeeded:N0} 个，写入 {Fmt.Bytes(result.BytesWritten)}，" +
                          $"耗时 {Fmt.Duration(result.Elapsed)}。");
            }
            else
            {
                w.Warn($"完成：成功 {result.Succeeded:N0}，失败 {result.Failed:N0}，跳过 {result.Skipped:N0}，" +
                       $"写入 {Fmt.Bytes(result.BytesWritten)}，耗时 {Fmt.Duration(result.Elapsed)}。");
            }

            foreach (var (file, error) in result.Errors.Take(30))
                w.Error($"{file} — {error}");

            if (result.Errors.Count > 30)
                w.Warn($"另有 {result.Errors.Count - 30:N0} 条失败未逐条列出。");

            if (result.Succeeded > 0)
                w.Info($"产物在：{w.OutputRoot}");
        }
        catch (OperationCanceledException)
        {
            w.Warn("已取消。");
        }
        catch (Exception ex)
        {
            w.Error($"还原中断：{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void UpdateStats(int done, int total, int ok, int failed, int skipped, long bytes)
    {
        Stats.Clear();
        Stats.Add(new StatItem("进度", $"{done:N0}/{total:N0}"));
        Stats.Add(new StatItem("成功", ok.ToString("N0")));
        Stats.Add(new StatItem("失败", failed.ToString("N0")));
        Stats.Add(new StatItem("跳过", skipped.ToString("N0")));
        Stats.Add(new StatItem("已写入", Fmt.Bytes(bytes)));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Workspace.Current.Warn("已请求取消，正在收尾……");
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        string path = Workspace.Current.OutputRoot;
        if (string.IsNullOrWhiteSpace(path))
        {
            Workspace.Current.Warn("输出目录还没设置。");
            return;
        }

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

    private static int ParseLimit(string? text)
        => int.TryParse(text?.Trim(), out int v) && v > 0 ? v : 0;

    private int ParseLimit() => ParseLimit(LimitBox.Text);

    private void SetBusy(bool busy)
    {
        Workspace.Current.Busy = busy;
        StartBtn.IsEnabled = !busy;
        CancelBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StateText.Text = busy ? "运行中…" : "就绪";
        if (busy) CurrentFile.Text = string.Empty;
    }
}
