using System.Windows.Controls;

namespace AudioSourceMixer.Desktop.Views;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    public SettingsView() => InitializeComponent();
    internal ScrollViewer PageScrollViewer => SettingsScrollViewer;
    internal System.Windows.Controls.ComboBox LanguageSelector => LanguageComboBox;
}
