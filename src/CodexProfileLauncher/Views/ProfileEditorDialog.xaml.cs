using Microsoft.Win32;
using CodexProfileLauncher.ViewModels;

namespace CodexProfileLauncher.Views;

public partial class ProfileEditorDialog
{
    public ProfileEditorDialog(ProfileEditorViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => NameTextBox.Focus();
    }

    public ProfileEditorViewModel ViewModel { get; }

    public CodexProfileLauncher.Core.Models.CodexProfile? Result { get; private set; }

    private void BrowseDataRoot_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var selected = ChooseFolder("选择环境目录或存放位置", ViewModel.DataRoot);
        if (selected is not null)
        {
            ViewModel.DataRoot = selected;
        }
    }

    private void BrowseWorkingDirectory_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var selected = ChooseFolder("选择默认工作目录", ViewModel.WorkingDirectory);
        if (selected is not null)
        {
            ViewModel.WorkingDirectory = selected;
        }
    }

    private void Cancel_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Save_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!ViewModel.Validate())
        {
            return;
        }

        Result = ViewModel.BuildProfile();
        DialogResult = true;
    }

    private string? ChooseFolder(string title, string initialDirectory)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
            ValidateNames = true,
            AddToRecent = false,
        };

        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
}
