using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SzbAssetDecryption.App.Views;

namespace SzbAssetDecryption.App;

/// <summary>
/// Dev helper behind <c>SZB_Asset_Decryption.exe --shot &lt;dir&gt; [--run]</c>: lays out every page, writes a
/// PNG for each, then exits. It produces the screenshots under <c>docs/img</c> and doubles as
/// a smoke test — if a page fails to build its visual tree, this throws instead of leaving the
/// breakage to be found by hand.
///
/// <para>
/// With <c>--run</c> it first executes a small real job on each task page (restore, audio,
/// video) through the exact same code path the buttons use, so the progress plumbing and the
/// external tools are exercised end to end.
/// </para>
/// </summary>
internal static class ScreenshotMode
{
    private static readonly string[] Names =
    [
        "01_overview", "02_assets", "03_audio", "04_video", "05_settings", "06_about",
    ];

    /// <summary>Returns true when the app was started in screenshot mode.</summary>
    public static bool TryRun(string[] args)
    {
        int index = Array.IndexOf(args, "--shot");
        if (index < 0 || index + 1 >= args.Length) return false;

        string dir = args[index + 1];
        Directory.CreateDirectory(dir);

        bool run = args.Contains("--run");

        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            Height = 1000,
            Left = -30000,
            Top = -30000,
        };

        window.Show();
        Pump();
        AssertInitialPage(window);

        if (run)
        {
            RunJob(window, 1, el => ((AssetView)el).RunAsync(12));
            RunJob(window, 2, el => ((AudioView)el).RunAsync(2));
            RunJob(window, 3, el => ((VideoView)el).RunAsync(1));
        }

        for (int i = 0; i < Names.Length; i++)
        {
            window.SelectPage(i);
            Pump();
            Save(window, Path.Combine(dir, Names[i] + ".png"));
        }

        window.Close();
        return true;
    }

    private static void RunJob(MainWindow window, int pageIndex, Func<FrameworkElement, Task> job)
    {
        window.SelectPage(pageIndex);
        Pump();

        if (window.PageContent(pageIndex) is not { } content)
            throw new InvalidOperationException($"page {pageIndex} has no content");

        var task = job(content);

        // The continuation posts back to this dispatcher, so pump instead of blocking on Wait().
        while (!task.IsCompleted)
        {
            Pump();
            Thread.Sleep(10);
        }

        if (task.IsFaulted) throw task.Exception!.GetBaseException();
        Pump();
    }

    /// <summary>
    /// Regression guard for the window opening blank: the first page has to be selected and
    /// materialised on its own, without any manual navigation. Asserting it here means the
    /// screenshot step fails loudly instead of quietly writing six correct-looking pages.
    /// </summary>
    private static void AssertInitialPage(MainWindow window)
    {
        if (window.FindName("Pages") is not System.Windows.Controls.TabControl tabs) return;

        if (tabs.SelectedIndex != 0 || tabs.SelectedContent is not FrameworkElement)
            throw new InvalidOperationException(
                $"first page did not render: SelectedIndex={tabs.SelectedIndex}, " +
                $"SelectedContent={tabs.SelectedContent?.GetType().Name ?? "null"}");
    }

    /// <summary>Lets layout, bindings and the Loaded handlers settle before rendering.</summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Save(Window window, string path)
    {
        if (window.Content is not FrameworkElement root)
            throw new InvalidOperationException("window has no content");

        root.UpdateLayout();

        int w = (int)Math.Ceiling(root.ActualWidth);
        int h = (int)Math.Ceiling(root.ActualHeight);

        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"page laid out to {w}x{h}, nothing to render");

        var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
