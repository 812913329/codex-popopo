using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace CodexProfileLauncher.Views;

public partial class ConfirmationDialog
{
    public ConfirmationDialog(
        string title,
        string message,
        string confirmText,
        string cancelText,
        bool isDestructive,
        string? secondaryText = null)
    {
        InitializeComponent();

        Title = title;
        HeadingTextBlock.Text = title;
        MessageTextBlock.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            SecondaryButton.Content = secondaryText;
            SecondaryButton.Visibility = Visibility.Visible;
            AutomationProperties.SetName(SecondaryButton, secondaryText);
        }

        ConfirmButton.Style = (Style)FindResource(
            isDestructive ? "App.DangerFilledButton" : "App.PrimaryButton");

        AutomationProperties.SetName(this, title);
        AutomationProperties.SetName(ConfirmButton, confirmText);
        AutomationProperties.SetName(CancelButton, cancelText);
        Loaded += (_, _) => Keyboard.Focus(CancelButton);
    }

    public ConfirmationDialogChoice Choice { get; private set; } = ConfirmationDialogChoice.Cancel;

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConfirmationDialogChoice.Cancel;
        DialogResult = false;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConfirmationDialogChoice.Secondary;
        DialogResult = true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Choice = ConfirmationDialogChoice.Confirm;
        DialogResult = true;
    }
}

public enum ConfirmationDialogChoice
{
    Confirm,
    Secondary,
    Cancel,
}
