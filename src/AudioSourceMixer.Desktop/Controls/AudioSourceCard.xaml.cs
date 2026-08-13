using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Desktop.ViewModels;

namespace AudioSourceMixer.Desktop.Controls;

public partial class AudioSourceCard : System.Windows.Controls.UserControl
{
    public AudioSourceCard() => InitializeComponent();

    private void OutputDeviceItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox comboBox || comboBox.DataContext is not AudioSourceViewModel source ||
            e.OriginalSource is not DependencyObject origin ||
            ItemsControl.ContainerFromElement(comboBox, origin) is not ComboBoxItem item ||
            item.Content is not OutputDeviceInfo device) return;
        _ = source.UserSelectOutputDeviceAsync(device);
    }

    private void OutputDeviceKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not System.Windows.Controls.ComboBox comboBox ||
            comboBox.DataContext is not AudioSourceViewModel source ||
            comboBox.SelectedItem is not OutputDeviceInfo device) return;
        _ = source.UserSelectOutputDeviceAsync(device);
        comboBox.IsDropDownOpen = false;
        e.Handled = true;
    }
}
