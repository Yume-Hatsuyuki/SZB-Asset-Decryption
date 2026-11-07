using System.Windows;
using SzbTool.Core.External;

namespace SzbTool.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ToolLocator.Refresh();

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"发生未处理的错误：\n\n{args.Exception.GetType().Name}: {args.Exception.Message}\n\n" +
                $"{args.Exception.StackTrace}",
                "SZB Asset Decryption",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Documentation / smoke-test path: render every page to PNG and quit.
        if (ScreenshotMode.TryRun(e.Args))
        {
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
