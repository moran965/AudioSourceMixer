using System.ComponentModel;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Desktop.ViewModels;

namespace AudioSourceMixer.Desktop;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainViewModel _viewModel;
    public bool AllowClose { get; set; }
    internal ItemsControl SourceItems => SourcesItemsControl;
    internal TabItem SettingsPage => SettingsTab;
    internal TabItem MixerPage => MixerTab;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

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

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.LogWindowCloseDecision(AllowClose);
        if (!AllowClose)
        {
            e.Cancel = true;
            var app = (App)System.Windows.Application.Current;
            if (_viewModel.CloseToTray) app.HideToTray();
            else _ = app.ExitAndRestoreAsync();
        }
        base.OnClosing(e);
    }
}
