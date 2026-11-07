using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SzbAssetDecryption.App.Services;
using SzbAssetDecryption.Core.External;

namespace SzbAssetDecryption.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;

        ParallelBox.Text = Workspace.Current.Parallelism.ToString();
        CpuHint.Text = $"本机 {Environment.ProcessorCount} 个逻辑核心，建议 {Math.Max(2, Environment.ProcessorCount)}～{Math.Max(2, Environment.ProcessorCount) * 2}。";

        Workspace.Current.PropertyChanged += OnWorkspaceChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Reprobe();
        SyncKey();
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Workspace.HasKey)
            or nameof(Workspace.KeyState)
            or nameof(Workspace.KeyDetails)
            or nameof(Workspace.Deriving))
        {
            SyncKey();
        }
    }

    private void Reprobe_Click(object sender, RoutedEventArgs e)
    {
        Reprobe();
        Workspace.Current.Info("已重新探测外部依赖。");
    }

    private void Reprobe()
    {
        ToolLocator.Refresh();

        ShowTool(FfmpegDot, FfmpegPath, ToolLocator.Ffmpeg);
        ShowTool(VgmDot, VgmPath, ToolLocator.Vgmstream);
    }

    private void ShowTool(System.Windows.Shapes.Ellipse dot, TextBlock label, string? path)
    {
        bool ok = path is not null;
        dot.Fill = (Brush)FindResource(ok ? "Success" : "Danger");
        label.Text = path ?? "未找到";
    }

    // ---------------------------------------------------------------- key

    private async void Derive_Click(object sender, RoutedEventArgs e)
    {
        await Workspace.Current.DeriveBaseKeyAsync();
        SyncKey();
    }

    private async void Rederive_Click(object sender, RoutedEventArgs e)
    {
        Workspace.Current.Info("强制重算：忽略上次的结果，重新扫描本地文件。");
        await Workspace.Current.DeriveBaseKeyAsync(forceRederive: true);
        SyncKey();
    }

    private void ApplyKey_Click(object sender, RoutedEventArgs e)
    {
        var w = Workspace.Current;
        bool ok = w.TrySetBaseKey(KeyBox.Text, out string message);
        if (ok) w.Success(message);
        else w.Error(message);
        SyncKey();
    }

    /// <summary>Mirror the workspace key into the read-only line and colour the status.</summary>
    private void SyncKey()
    {
        var w = Workspace.Current;

        KeyValue.Text = w.HasKey ? w.BaseKeyHex : "（还没有）";
        KeyValue.Foreground = (Brush)FindResource(w.HasKey ? "TextMuted" : "TextMuted");
        KeyDetail.Text = w.HasKey ? w.KeyDetails : "点「计算」开始，大约 20 秒。";

        KeyState.Text = w.KeyState;
        KeyState.Foreground = (Brush)FindResource(w.HasKey ? "TextMuted" : "Warning");

        DeriveButton.IsEnabled = !w.Deriving;
        KeyBox.Text = w.BaseKeyHex;
    }

    // ---------------------------------------------------------------- misc

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var w = Workspace.Current;
        if (int.TryParse(ParallelBox.Text?.Trim(), out int p))
        {
            w.Parallelism = p;
            ParallelBox.Text = w.Parallelism.ToString();
        }
        else
        {
            ParallelBox.Text = w.Parallelism.ToString();
        }

        w.Save();
        w.Success($"设置已保存，并发数 {w.Parallelism}。");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        Workspace.Current.ClearLog();
        Workspace.Current.Info("日志已清空。");
    }

    private void OpenAppDir_Click(object sender, RoutedEventArgs e)
        => Open(AppContext.BaseDirectory);

    private void OpenDatDir_Click(object sender, RoutedEventArgs e)
    {
        string path = Workspace.Current.DatRoot;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            Workspace.Current.Warn("数据目录还没设置或不存在。");
            return;
        }
        Open(path);
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Workspace.Current.Error($"打不开目录：{ex.Message}");
        }
    }
}
