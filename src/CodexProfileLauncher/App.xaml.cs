using System.Windows;
using System.Security.Principal;
using CodexProfileLauncher.Core.Persistence;
using CodexProfileLauncher.Core.Services;
using CodexProfileLauncher.Infrastructure;
using CodexProfileLauncher.ViewModels;

namespace CodexProfileLauncher;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application, IDisposable
{
    private Mutex? _singleInstanceMutex;
    private JsonlFileLogger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        // The hidden keeper must bypass every UI/global-singleton side effect;
        // the visible launcher already owns the per-user UI mutex.
        if (WindowsJobBroker.TryRun(e.Args, out var brokerExitCode))
        {
            Shutdown(brokerExitCode);
            return;
        }

        base.OnStartup(e);

        var currentUserSid = WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("无法读取当前 Windows 用户 SID。");
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: $@"Global\CodexProfileLauncher.SingleInstance.{currentUserSid}",
            createdNew: out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Codex 环境管理器已经在运行。",
                "Codex 环境管理器",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var paths = new LauncherPaths();
        _logger = new JsonlFileLogger(paths.LogsDirectory);
        DispatcherUnhandledException += (_, args) =>
        {
            _logger.Error("UNHANDLED_UI_EXCEPTION", args.Exception.Message, args.Exception);
            MessageBox.Show(
                $"{args.Exception.Message}{Environment.NewLine}{Environment.NewLine}完整信息已写入本地日志。",
                "Codex 环境管理器遇到错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
            Shutdown(-1);
        };

        var processInspector = new WindowsProcessInspector();
        var aiSettingsService = new ProfileAiSettingsService();
        var viewModel = new MainWindowViewModel(
            paths,
            new AtomicJsonProfileRepository(paths.StateDirectory),
            aiSettingsService,
            new WindowsCodexAppLocator(),
            new CodexRuntimeMirrorManager(paths),
            new WindowsWindowController(),
            processInspector,
            new WindowsJobObjectManager(),
            new StartupIsolationVerifier(processInspector),
            _logger,
            new WpfDialogService(),
            new ShellService());

        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Dispose();
        base.OnExit(e);
    }

    public void Dispose()
    {
        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // The mutex was not owned because startup exited early.
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        GC.SuppressFinalize(this);
    }
}

