using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Markup;
using AudioSourceMixer.Desktop.Localization;
using AudioSourceMixer.Desktop.ViewModels;
using AudioSourceMixer.Desktop.Views;

namespace AudioSourceMixer.Desktop;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainViewModel _viewModel;
    public bool AllowClose { get; set; }
    internal ItemsControl SourceItems => MixerPageView.SourceItems;
    internal ScrollViewer SourceScroller => MixerPageView.SourceScroller;
    internal SettingsView SettingsPage => SettingsPageView;
    internal BrowserSetupView BrowserSetupPage => BrowserSetupPageView;
    internal MixerView MixerPage => MixerPageView;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(LocalizationService.Current.CurrentLanguage);
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += ConstrainInitialSizeToWorkArea;
    }

    private void ConstrainInitialSizeToWorkArea(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= ConstrainInitialSizeToWorkArea;
        var workArea = System.Windows.SystemParameters.WorkArea;
        Width = Math.Min(1240, Math.Max(MinWidth, workArea.Width - 32));
        Height = Math.Min(820, Math.Max(MinHeight, workArea.Height - 32));
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
