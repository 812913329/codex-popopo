using System.Text;
using System.Windows;
using System.Windows.Controls;
using CodexProfileLauncher.ViewModels;
using Microsoft.Win32;

namespace CodexProfileLauncher.Views.Panels;

public partial class AiSettingsPanel : UserControl
{
    public AiSettingsPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    public AiSettingsDialogViewModel? ViewModel => DataContext as AiSettingsDialogViewModel;

    public event EventHandler? SettingsSaved;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.InitializeLiveModelsAsync().ConfigureAwait(true);
        }
    }

    private async void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsLoaded && e.NewValue is AiSettingsDialogViewModel vm)
        {
            await vm.InitializeLiveModelsAsync().ConfigureAwait(true);
        }
    }

    private void CopyKey_Click(object sender, RoutedEventArgs e) => ViewModel?.CopyKey();

    private void OpenSettingsFile_Click(object sender, RoutedEventArgs e) => ViewModel?.OpenSettingsFile();

    private void OpenPromptFile_Click(object sender, RoutedEventArgs e) => ViewModel?.OpenPromptFile();

    private void ClearApi_Click(object sender, RoutedEventArgs e) => ViewModel?.ClearApi();

    private void UndoPrompt_Click(object sender, RoutedEventArgs e) => ViewModel?.UndoPrompt();

    private void ClearPrompt_Click(object sender, RoutedEventArgs e) => ViewModel?.ClearPrompt();

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.TestConnectionAsync().ConfigureAwait(true);
        }
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.RefreshModelsAsync().ConfigureAwait(true);
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.ReloadAsync().ConfigureAwait(true);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        if (await ViewModel.SaveAsync().ConfigureAwait(true))
        {
            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void ImportPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "导入系统提示词",
            Filter = "提示词文件 (*.md;*.txt)|*.md;*.txt|Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt",
            Multiselect = false,
            CheckFileExists = true,
            AddToRecent = false,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
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
}
