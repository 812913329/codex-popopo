using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using CodexProfileLauncher.ViewModels;

namespace CodexProfileLauncher;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private bool _initialized;
    private bool _closeRequestInProgress;
    private bool _allowClose;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += (_, _) => ViewModel.Dispose();
        if (EmbeddedAiSettingsPanel is not null)
        {
            EmbeddedAiSettingsPanel.SettingsSaved += async (_, _) =>
                await ViewModel.NotifyAiSettingsSavedAsync().ConfigureAwait(true);
        }
    }

    public MainWindowViewModel ViewModel { get; }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ViewModel.InitializeAsync();
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closeRequestInProgress)
        {
            return;
        }

        _closeRequestInProgress = true;
        try
        {
            if (await ViewModel.TryPrepareCloseAsync().ConfigureAwait(true))
            {
                // Even a logically asynchronous close guard can complete synchronously.
                // Queue the second Close so the original Closing callback has returned;
                // WPF rejects Close/Show/visibility changes while that callback is active.
                _ = Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    () =>
                    {
                        _allowClose = true;
                        Close();
                    });
            }
        }
        finally
        {
            _closeRequestInProgress = false;
        }
    }
}
