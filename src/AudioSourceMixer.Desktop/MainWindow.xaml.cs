using System.ComponentModel;
using System.Windows.Controls;
using AudioSourceMixer.Desktop.ViewModels;
using AudioSourceMixer.Desktop.Views;

namespace AudioSourceMixer.Desktop;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainViewModel _viewModel;
    public bool AllowClose { get; set; }
    internal ItemsControl SourceItems => MixerPageView.SourceItems;
    internal SettingsView SettingsPage => SettingsPageView;
    internal BrowserSetupView BrowserSetupPage => BrowserSetupPageView;
    internal MixerView MixerPage => MixerPageView;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    internal void SelectMixerPage() => _viewModel.SelectMixerForDiagnostics();
    internal void SelectSettingsPage() => _viewModel.SelectSettingsForDiagnostics();
    internal void SelectBrowserSetupPage() => _viewModel.SelectBrowserSetupForDiagnostics();

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
