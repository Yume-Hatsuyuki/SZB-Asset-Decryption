using System.Reflection;
using System.Windows.Controls;

namespace SzbAssetDecryption.App.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();

        var v = Assembly.GetExecutingAssembly().GetName().Version;
        string runtime = Environment.Version.ToString();
        VersionText.Text = $"版本 {(v is null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}")} · 运行时 .NET {runtime}";
    }
}
