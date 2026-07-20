using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CodexProfileLauncher.Views;

public partial class MessageDialog
{
    public MessageDialog(string title, string message, string? details, bool isError)
    {
        InitializeComponent();

        Title = title;
        HeadingTextBlock.Text = title;
        MessageTextBlock.Text = message;
        DetailsTextBox.Text = details ?? string.Empty;
        DetailsExpander.Visibility = string.IsNullOrWhiteSpace(details)
            ? Visibility.Collapsed
            : Visibility.Visible;

        IconTextBlock.Text = isError ? "\uEA39" : "\uE946";
        IconBorder.SetResourceReference(
            Border.BackgroundProperty,
            isError ? "App.Brush.DangerSoft" : "App.Brush.AccentSoft");
        IconTextBlock.SetResourceReference(
            TextBlock.ForegroundProperty,
            isError ? "App.Brush.Danger" : "App.Brush.Accent");

        AutomationProperties.SetName(this, title);
        Loaded += (_, _) => Keyboard.Focus(CloseButton);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
