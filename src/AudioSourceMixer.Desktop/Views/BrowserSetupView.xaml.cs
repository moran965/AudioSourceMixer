using System.Windows.Controls;

namespace AudioSourceMixer.Desktop.Views;

public partial class BrowserSetupView : System.Windows.Controls.UserControl
{
    public BrowserSetupView() => InitializeComponent();
    internal ScrollViewer PageScrollViewer => BrowserSetupScrollViewer;
}
