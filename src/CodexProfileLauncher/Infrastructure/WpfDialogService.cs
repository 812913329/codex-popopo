using System.Windows;
using CodexProfileLauncher.Core.Models;
using CodexProfileLauncher.ViewModels;
using CodexProfileLauncher.Views;
using Microsoft.Win32;

namespace CodexProfileLauncher.Infrastructure;

public sealed class WpfDialogService
{
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false,
        };

        return dialog.ShowDialog(Application.Current.MainWindow) == true
            ? dialog.FolderName
            : null;
    }

    public CodexProfile? EditProfile(ProfileEditorViewModel viewModel)
    {
        var dialog = new ProfileEditorDialog(viewModel)
        {
            Owner = Application.Current.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public bool Confirm(
        string title,
        string message,
        string confirmText = "继续",
        string cancelText = "取消",
        bool isDestructive = false)
    {
        var dialog = new ConfirmationDialog(
            title,
            message,
            confirmText,
            cancelText,
            isDestructive)
        {
            Owner = Application.Current.MainWindow,
        };

        return dialog.ShowDialog() == true && dialog.Choice == ConfirmationDialogChoice.Confirm;
    }

    public ConfirmationDialogChoice ConfirmWithAlternate(
        string title,
        string message,
        string confirmText,
        string alternateText,
        string cancelText)
    {
        var dialog = new ConfirmationDialog(
            title,
            message,
            confirmText,
            cancelText,
            isDestructive: false,
            secondaryText: alternateText)
        {
            Owner = Application.Current.MainWindow,
        };

        _ = dialog.ShowDialog();
        return dialog.Choice;
    }

    public void ShowError(string title, string message, string details)
    {
        var dialog = new MessageDialog(title, message, details, isError: true)
        {
            Owner = Application.Current.MainWindow,
        };
        _ = dialog.ShowDialog();
    }

    public void ShowInformation(string title, string message)
    {
        var dialog = new MessageDialog(title, message, details: null, isError: false)
        {
            Owner = Application.Current.MainWindow,
        };
        _ = dialog.ShowDialog();
    }
}
