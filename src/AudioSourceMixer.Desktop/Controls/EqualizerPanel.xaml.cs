namespace AudioSourceMixer.Desktop.Controls;

public partial class EqualizerPanel : System.Windows.Controls.UserControl
{
    public EqualizerPanel() => InitializeComponent();

    private void EqualizerExpanded(object sender, System.Windows.RoutedEventArgs e)
        => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => BringIntoView(new System.Windows.Rect(0, 0, ActualWidth, ActualHeight))));
}
