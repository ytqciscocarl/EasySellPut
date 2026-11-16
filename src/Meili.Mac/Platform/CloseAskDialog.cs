using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Meili.Core.Settings;

namespace Meili.Mac.Platform;

/// <summary>Three-way close prompt: hide to Dock, exit, or cancel.</summary>
internal sealed class CloseAskDialog : Meili.Mac.Views.Controls.AppDialog
{
    public CloseAskDialog()
    {
        var loc = LocalizationService.Current;
        Title = loc["Close_AskTitle"];
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var hide = new Button { Content = loc["Close_ToTray"], MinWidth = 88, Margin = new Thickness(0, 0, 8, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var exit = new Button { Content = loc["Close_Exit"], IsDefault = true, Classes = { "Primary" }, MinWidth = 88, Margin = new Thickness(0, 0, 8, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = loc["Mac_Cancel"], IsCancel = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        hide.Click += (_, _) => Close(CloseAction.MinimizeToTray);
        exit.Click += (_, _) => Close(CloseAction.Exit);
        cancel.Click += (_, _) => Close(null);
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = loc["Close_AskBody"], TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 14 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { hide, exit, cancel },
                },
            },
        };
    }
}
