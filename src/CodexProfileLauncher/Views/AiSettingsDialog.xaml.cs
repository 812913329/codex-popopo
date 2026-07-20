using Microsoft.Win32;
using System.ComponentModel;
using System.Text;
using CodexProfileLauncher.ViewModels;

namespace CodexProfileLauncher.Views;

public partial class AiSettingsDialog
{
    private bool _allowClose;
    private bool _hasActivated;

    public AiSettingsDialog(AiSettingsDialogViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closing += ConfirmDirtyClose;
        Activated += CheckExternalChanges;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        BaseUrlTextBox.Focus();
        await ViewModel.InitializeLiveModelsAsync().ConfigureAwait(true);
    }

    public AiSettingsDialogViewModel ViewModel { get; }

    private void CopyKey_Click(object sender, System.Windows.RoutedEventArgs e) => ViewModel.CopyKey();

    private void OpenSettingsFile_Click(object sender, System.Windows.RoutedEventArgs e) => ViewModel.OpenSettingsFile();

    private void OpenPromptFile_Click(object sender, System.Windows.RoutedEventArgs e) => ViewModel.OpenPromptFile();

    private void ClearApi_Click(object sender, System.Windows.RoutedEventArgs e) => ViewModel.ClearApi();

    private void UndoPrompt_Click(object sender, System.Windows.RoutedEventArgs e) => ViewModel.UndoPrompt();

    private void ClearPrompt_Click(object sender, System.Windows.RoutedEventArgs e) => ViewModel.ClearPrompt();

    private async void TestConnection_Click(object sender, System.Windows.RoutedEventArgs e) =>
        await ViewModel.TestConnectionAsync().ConfigureAwait(true);

    private async void RefreshModels_Click(object sender, System.Windows.RoutedEventArgs e) =>
        await ViewModel.RefreshModelsAsync().ConfigureAwait(true);

    private async void Reload_Click(object sender, System.Windows.RoutedEventArgs e) =>
        await ViewModel.ReloadAsync().ConfigureAwait(true);

    private async void Save_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (await ViewModel.SaveAsync().ConfigureAwait(true))
        {
            _allowClose = true;
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e) => DialogResult = false;

    private async void ImportPrompt_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入系统提示词",
            Filter = "提示词文件 (*.md;*.txt)|*.md;*.txt|Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt",
            Multiselect = false,
            CheckFileExists = true,
            AddToRecent = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var imported = await File.ReadAllTextAsync(dialog.FileName).ConfigureAwait(true);
            ViewModel.RememberPromptBeforeImport();
            ViewModel.SystemPrompt = imported;
            SystemPromptTextBox.Focus();
            SystemPromptTextBox.CaretIndex = 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            ViewModel.ReportError($"导入失败：{ex.Message}");
        }
    }

    private void ConfirmDirtyClose(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !ViewModel.IsDirty)
        {
            return;
        }

        var confirmation = new ConfirmationDialog(
            "放弃未保存的更改？",
            "API 配置或系统提示词还有未保存的更改。",
            "放弃更改",
            "继续编辑",
            isDestructive: true)
        {
            Owner = this,
        };
        if (confirmation.ShowDialog() == true &&
            confirmation.Choice == ConfirmationDialogChoice.Confirm)
        {
            _allowClose = true;
            return;
        }

        e.Cancel = true;
    }

    private async void CheckExternalChanges(object? sender, EventArgs e)
    {
        if (!_hasActivated)
        {
            _hasActivated = true;
            return;
        }

        await ViewModel.CheckForExternalChangesAsync().ConfigureAwait(true);
    }
}
