using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Meili.Mac.Platform;

namespace Meili.Mac;

/// <summary>
/// The macOS menu bar: application menu (About, Settings… ⌘,, Hide ⌘H, Quit ⌘Q) and File / View / Window menus with
/// the usual ⌘ shortcuts (⌘1…⌘9 pages, ⌘R refresh, ⌘F find symbol, ⌘E export, ⌘M minimize). Rebuilt on a language switch.
/// </summary>
public static class MacMenu
{
    public static void Install(Application app, MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var loc = AppServices.Get<LocalizationService>();
        void Build()
        {
            NativeMenu.SetMenu(app, new NativeMenu
            {
                Item(loc["Menu_About"], null, window.ShowAbout),
                new NativeMenuItemSeparator(),
                Item(loc["Menu_Settings"], "Meta+OemComma", () => window.Navigate(AppPage.Settings)),
                new NativeMenuItemSeparator(),
                Item(loc["Menu_Hide"], "Meta+H", () => { window.RunInBackground();foreach(var panel in desktop.Windows.OfType<QuotePanelWindow>())panel.HidePanel();if (OperatingSystem.IsMacOS()) MacNative.HideApp(); }),
                Item(loc["Menu_Quit"], "Meta+Q", () => desktop.TryShutdown()),
            });
            var pages = new NativeMenu();
            var order = MainWindow.PageOrder;
            for (var i = 0; i < order.Count; i++)
            {
                var page = order[i];
                pages.Add(Item(loc[MainWindow.TitleKey(page)], $"Meta+D{i + 1}", () => window.Navigate(page)));
            }
            pages.Add(new NativeMenuItemSeparator());
            pages.Add(Item(loc["Menu_Refresh"], "Meta+R", window.RefreshCurrent));
            pages.Add(Item(loc["Menu_Find"], "Meta+F", window.OpenWizard));
            pages.Add(new NativeMenuItemSeparator());
            pages.Add(Item(loc["Menu_ToggleTheme"], "Meta+Shift+D", window.ToggleTheme));
            pages.Add(Item(loc["Menu_ToggleLanguage"], "Meta+Shift+L", window.ToggleLanguage));
            NativeMenu.SetMenu(window, new NativeMenu
            {
                Sub(loc["Menu_File"],
                    Item(loc["Menu_Export"], "Meta+E", window.ExportCurrent),
                    Item(loc["Menu_Close"], "Meta+W", window.Close)),
                new NativeMenuItem(loc["Menu_View"]) { Menu = pages },
                Sub(loc["Menu_Window"],
                    Item(loc["Menu_Minimize"], "Meta+M", () => window.WindowState = WindowState.Minimized),
                    Item(loc["Menu_ShowMain"], "Meta+D0", window.ShowFromDock)),
            });
        }
        Build();
        loc.LanguageChanged += (_, _) => Build();
    }

    private static NativeMenuItem Item(string header, string? gesture, Action action)
    {
        var item = new NativeMenuItem(header);
        if (gesture is not null) item.Gesture = KeyGesture.Parse(gesture);
        item.Click += (_, _) => action();
        return item;
    }

    private static NativeMenuItem Sub(string header, params NativeMenuItem[] items)
    {
        var menu = new NativeMenu();
        foreach (var i in items) menu.Add(i);
        return new NativeMenuItem(header) { Menu = menu };
    }
}
