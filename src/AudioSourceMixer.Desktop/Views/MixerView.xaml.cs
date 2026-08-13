using System.Windows.Controls;

namespace AudioSourceMixer.Desktop.Views;

public partial class MixerView : System.Windows.Controls.UserControl
{
    public MixerView() => InitializeComponent();
    internal ItemsControl SourceItems => SourcesItemsControl;
}
