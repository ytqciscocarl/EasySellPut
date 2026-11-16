using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Meili.Mac.Platform;

/// <summary>Small modal OK/Cancel window (shown with ShowDialog, so it is awaited rather than blocking).</summary>
internal sealed class ConfirmDialog : Meili.Mac.Views.Controls.AppDialog
{
    public ConfirmDialog(string title, string message)
    {
        var loc = LocalizationService.Current;
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ok = new Button { Content = loc["Mac_Ok"], IsDefault = true, Classes = { "Primary" }, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = loc["Mac_Cancel"], IsCancel = true, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 14 },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
    }
}
