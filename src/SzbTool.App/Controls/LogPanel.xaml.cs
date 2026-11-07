using System.Collections.Specialized;
using System.Windows.Controls;
using SzbTool.App.Services;

namespace SzbTool.App.Controls;

/// <summary>
/// Shared run log. Binds straight to <see cref="Workspace.Log"/> via x:Static so it
/// keeps working no matter what DataContext the host page sets.
/// </summary>
public partial class LogPanel : UserControl
{
    public LogPanel()
    {
        InitializeComponent();
        Workspace.Current.Log.CollectionChanged += OnLogChanged;
        Loaded += (_, _) => Scroller.ScrollToEnd();
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add) Scroller.ScrollToEnd();
    }

    private void Clear_Click(object sender, System.Windows.RoutedEventArgs e)
        => Workspace.Current.ClearLog();
}
