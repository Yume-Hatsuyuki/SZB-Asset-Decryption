using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SzbTool.App.Services;
using SzbTool.Core.External;

namespace SzbTool.App;

public partial class MainWindow : Window
{
    private static readonly string[] PageNames =
    [
        "概览", "资源还原", "音频", "视频", "设置", "关于",
    ];

    public MainWindow()
    {
        InitializeComponent();

        Workspace.Current.Load();
        Workspace.Current.PropertyChanged += OnWorkspaceChanged;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshEnvironment();

        var w = Workspace.Current;
        if (!string.IsNullOrWhiteSpace(w.DatRoot) && Directory.Exists(w.DatRoot) && !w.ManifestLoaded)
        {
            if (w.TryLoadManifest(out string msg)) w.Info(msg);
            else w.Warn(msg);
        }

        UpdateStatus();

        if (w.Manifest is null)
            w.Info("欢迎使用。先在「概览」页选择游戏数据目录，那里是 Persistent/dat。");
        else
            w.Info($"清单已就绪，共 {w.Manifest.Count:N0} 条记录。");

        // The base key is neither compiled in nor downloaded: it is derived from the local
        // install. Running it here is cheap when a previous derivation is cached, and runs
        // in the background when it is not, so the window is usable immediately either way.
        if (w.Manifest is not null && !w.HasKey)
            _ = w.DeriveBaseKeyAsync();
    }

    private void RefreshEnvironment()
    {
        ToolLocator.Refresh();

        bool ff = ToolLocator.FfmpegAvailable;
        bool vgm = ToolLocator.VgmstreamAvailable;

        StateDot.Fill = (Brush)FindResource(ff || vgm ? "Success" : "Danger");

        var parts = new List<string>
        {
            ff ? "ffmpeg 就绪" : "缺少 ffmpeg",
            vgm ? "vgmstream 就绪" : "缺少 vgmstream",
        };

        if (!ff || !vgm) parts.Add("详见「设置 → 外部依赖」");
        StateText.Text = string.Join(" · ", parts);
    }

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Workspace.Manifest)
            or nameof(Workspace.DatRoot)
            or nameof(Workspace.OutputRoot)
            or nameof(Workspace.ManifestStatus)
            or nameof(Workspace.BaseKey))
        {
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        var w = Workspace.Current;

        string manifest = w.Manifest is null ? "未加载清单" : $"{w.Manifest.Count:N0} 条清单记录";
        string output = string.IsNullOrWhiteSpace(w.OutputRoot) ? "未设输出目录" : w.OutputRoot;
        string key = w.HasKey ? $"密钥 {w.BaseKey.Length} 字节" : "密钥未获取";

        RightText.Text = $"{key} · {manifest} · {output}";
        RightText.ToolTip = output;
    }

    // ---------------------------------------------------------------- navigation

    /// <summary>Page content by index. Used by the screenshot / smoke-test path.</summary>
    internal FrameworkElement? PageContent(int index)
        => index >= 0 && index < Pages.Items.Count
            ? (Pages.Items[index] as TabItem)?.Content as FrameworkElement
            : null;

    /// <summary>Switches to a page by index and keeps the sidebar in sync.</summary>
    public void SelectPage(int index)
    {
        if (Pages is null || index < 0 || index >= PageNames.Length) return;

        Pages.SelectedIndex = index;
        PageTitle.Text = PageNames[index];

        if (FindName($"Nav{index}") is RadioButton nav && nav.IsChecked != true)
            nav.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // The first radio button raises this while the template is still being built.
        if (sender is not RadioButton rb) return;
        if (rb.Tag is not string tag || !int.TryParse(tag, out int index)) return;

        SelectPage(index);
    }

    // ---------------------------------------------------------------- window chrome

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
