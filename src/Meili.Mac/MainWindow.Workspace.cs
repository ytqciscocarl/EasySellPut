using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Meili.Mac.Platform;

namespace Meili.Mac;

public partial class MainWindow
{
    private WebWorkspace? _workspace;
    private string? _activeWebsite;
    private readonly Dictionary<string, WebTabPane> _webTabs = [];
    private void SetupWorkspace()
    {
        try { _workspace = new WebWorkspace(AppServices.DataDirectory.Path); }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or ArgumentException)
        { Meili.Core.Diagnostics.AppLog.Warn("web-workspace", "Workspace could not be loaded", ex); AddWebsite.IsEnabled = false; ShowBanner(_loc["Web_Edit"], _loc["Web_Failed"]); }
        SetupStatusIcons(); RebuildBuiltinRail(); RebuildWebsiteRail(); ApplyWorkspaceMotion();
        _loc.LanguageChanged += WorkspaceLanguageChanged; Closed += (_, _) => _loc.LanguageChanged -= WorkspaceLanguageChanged;
        Activated += (_, _) => ApplyWorkspaceMotion();
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if ((e.KeyModifiers & (KeyModifiers.Meta | KeyModifiers.Control)) == 0) return;
            if (e.Key == Key.T) { _ = EditWebsite(null); e.Handled = true; }
            if (e.Key == Key.L && _activeWebsite is { } id) { _webTabs[id].FocusAddress(); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
    }
    private void WorkspaceLanguageChanged(object? sender, EventArgs e) { RebuildBuiltinRail(); RebuildWebsiteRail(); }
    private void RebuildBuiltinRail()
    {
        BuiltinRail.Children.Clear();
        foreach(var page in PageOrder.Where(p => p is not (AppPage.Dashboard or AppPage.Settings)))
        {
            var tile = new ToggleButton { Tag = page }; tile.Classes.Add("Workspace");
            tile.Content = CreateNavigationIcon(NavigationIcons.For(page),tile);
            var title = _loc[TitleKey(page)]; ToolTip.SetTip(tile,title+" · ⌘"+(PageOrder.ToList().IndexOf(page)+1));
            AutomationProperties.SetName(tile,title);AutomationProperties.SetAutomationId(tile,"Navigate-"+page);
            tile.Click += (_,_) => Navigate(page);BuiltinRail.Children.Add(tile);
        }
        SelectBuiltinPage();
    }
    private void SelectBuiltinPage()
    {
        HomeWorkspace.IsChecked = _activeWebsite is null && CurrentPage == AppPage.Dashboard;
        SettingsButton.IsChecked = _activeWebsite is null && CurrentPage == AppPage.Settings;
        foreach(var tile in BuiltinRail.Children.OfType<ToggleButton>())tile.IsChecked=_activeWebsite is null && Equals(tile.Tag,CurrentPage);
    }
    private static Viewbox CreateNavigationIcon(string data, Avalonia.Controls.Primitives.TemplatedControl owner, double size=24) => Meili.Mac.Views.Controls.LucideIcon.View(data,owner,size);
    private void SetupStatusIcons()
    {
        AddWebsite.Content=CreateNavigationIcon(NavigationIcons.Add,AddWebsite);
        SettingsButton.Content=CreateNavigationIcon(NavigationIcons.Settings,SettingsButton,18);
        LanguageButton.Content=CreateNavigationIcon(NavigationIcons.Language,LanguageButton,18);
        ThemeButton.Content=CreateNavigationIcon(NavigationIcons.Theme,ThemeButton,18);
        BackgroundButton.Content=CreateNavigationIcon(NavigationIcons.Minus,BackgroundButton,18);
        MotionButton.Content=CreateNavigationIcon(NavigationIcons.Motion,MotionButton,18);
    }
    private void OnSettings(object? sender,RoutedEventArgs e) => Navigate(AppPage.Settings);
    private void RebuildWebsiteRail()
    {
        WebsiteRail.Children.Clear();
        foreach (var tab in _workspace?.Tabs ?? [])
        {
            var tile = new ToggleButton { Content = tab.Glyph, Tag = tab.Id, IsChecked = tab.Id == _activeWebsite };
            tile.Classes.Add("Workspace"); ToolTip.SetTip(tile, tab.Name + "\n" + tab.Host);
            AutomationProperties.SetName(tile, tab.Name); AutomationProperties.SetAutomationId(tile, "Website-" + tab.Id);
            tile.Click += (_, _) => OpenWebsite(tab);
            var menu = new ContextMenu(); var edit = new MenuItem(); edit[!MenuItem.HeaderProperty] = edit.GetResourceObservable("Web_Edit").ToBinding(); edit.Click += (_, _) => _ = EditWebsite(tab); menu.Items.Add(edit); tile.ContextMenu = menu;
            WebsiteRail.Children.Add(tile);
        }
        AddWebsite.IsEnabled = _workspace is not null && _workspace.Tabs.Count < WebWorkspace.MaximumTabs;
        ToolTip.SetTip(AddWebsite, _loc[_workspace is null ? "Web_Failed" : AddWebsite.IsEnabled ? "Web_Add" : "Web_Limit"]);
    }
    internal void OpenWebsite(WebWorkspaceTab tab)
    {
        if (!_webTabs.TryGetValue(tab.Id, out var pane))
        {
            pane = new WebTabPane(tab.Url); _webTabs[tab.Id] = pane;
            pane.EditRequested += () => _ = EditWebsite(_workspace?.Tabs.FirstOrDefault(t => t.Id == tab.Id));
            WebTabHost.Children.Add(pane);
        }
        _activeWebsite = tab.Id; PageHost.Content = null; WebTabHost.IsVisible = true;
        foreach (var (id, view) in _webTabs){view.IsVisible = id == tab.Id;view.SetPresentationActive(IsVisible && WindowState!=WindowState.Minimized && id==tab.Id);}
        SelectBuiltinPage();
        foreach (var button in WebsiteRail.Children.OfType<ToggleButton>()) button.IsChecked = Equals(button.Tag, tab.Id);
        ApplyWorkspaceMotion();
    }
    private void ShowMarketWorkspace()
    {
        _activeWebsite = null; WebTabHost.IsVisible = false; SelectBuiltinPage();
        foreach (var button in WebsiteRail.Children.OfType<ToggleButton>()) button.IsChecked = false;
    }
    private void OnHomeWorkspace(object? sender, RoutedEventArgs e) => Navigate(AppPage.Dashboard);
    private void OnAddWebsite(object? sender, RoutedEventArgs e) { AddWebsite.IsChecked = false; _ = EditWebsite(null); }
    private void OnToggleMotion(object? sender, RoutedEventArgs e)
    {
        try { _workspace?.SetReduceMotion(MotionButton.IsChecked == true); }
        catch (System.IO.IOException) { ShowBanner(_loc["Web_Edit"], _loc["Web_Failed"]); }
        ApplyWorkspaceMotion();
    }
    private void ApplyWorkspaceMotion()
    {
        var reduced = _workspace?.ReduceMotion == true || SystemReduceMotion();
        DesignMotion.SetReduced(reduced); Classes.Set("ReducedMotion", reduced); MotionButton.IsChecked = _workspace?.ReduceMotion == true;
    }
    private static bool SystemReduceMotion()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try { return SendBool(SendObject(ObjClass("NSWorkspace"), ObjSelector("sharedWorkspace")), ObjSelector("accessibilityDisplayShouldReduceMotion")); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { Meili.Core.Diagnostics.AppLog.Warn("web-workspace", "System motion preference unavailable", ex); return true; }
    }
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")] private static extern IntPtr ObjClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")] private static extern IntPtr ObjSelector(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern IntPtr SendObject(IntPtr obj, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(IntPtr obj, IntPtr selector);

    internal async Task EditWebsite(WebWorkspaceTab? tab)
    {
        if (_workspace is null) return;
        var dialog = new Meili.Mac.Views.Controls.AppDialog { Title = _loc[tab is null ? "Web_Add" : "Web_Edit"], Width = 440, MinWidth = 400, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, FontSize = 14 };
        dialog.Bind(BackgroundProperty, new DynamicResourceExtension("ApplicationBackgroundBrush")); dialog.Bind(ForegroundProperty, new DynamicResourceExtension("TextFillColorPrimaryBrush"));
        var body = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        var name = new TextBox { Text = tab?.Name ?? "", MaxLength = 40, Name = "WebsiteName" };
        var url = new TextBox { Text = tab?.Url ?? "", PlaceholderText = "https://", MaxLength = 2048, Name = "WebsiteAddress" };
        var error = new TextBlock { IsVisible = false, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
        body.Children.Add(new TextBlock { Text = _loc["Web_Name"] }); body.Children.Add(name); body.Children.Add(new TextBlock { Text = _loc["Web_Address"] }); body.Children.Add(url);
        AutomationProperties.SetName(name, _loc["Web_Name"]); AutomationProperties.SetName(url, _loc["Web_Address"]);
        body.Children.Add(new TextBlock { Text = _loc["Web_Privacy"], TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 }); body.Children.Add(error);
        var actions = new WrapPanel { Orientation = Orientation.Horizontal }; body.Children.Add(actions);
        Button Button(string key, Action action) { var button = new Button { Content = _loc[key], Margin = new Thickness(0, 0, 8, 8) }; button.Click += (_, _) => action(); actions.Children.Add(button); return button; }
        void Fail(string key) { error.Text = _loc[key]; error.IsVisible = true; }
        void Save()
        {
            if (!WebWorkspace.TryNormalize(name.Text, url.Text, out _, out _)) { Fail("Web_Invalid"); return; }
            try
            {
                var saved = _workspace.Save(tab?.Id, name.Text!, url.Text!);
                if (tab is not null && tab.Url != saved.Url && _webTabs.Remove(tab.Id, out var old)) WebTabHost.Children.Remove(old);
                RebuildWebsiteRail(); OpenWebsite(saved); dialog.Close();
            }
            catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or ArgumentException) { Fail("Web_Failed"); }
        }
        Button("Web_Save", Save).Classes.Add("Primary"); Button("Web_Cancel", () => dialog.Close());
        if (tab is not null)
        {
            void Move(int delta) { try { _workspace.Move(tab.Id, delta); RebuildWebsiteRail(); } catch (System.IO.IOException) { Fail("Web_Failed"); } }
            Button("Web_Up", () => Move(-1)); Button("Web_Down", () => Move(1));
            Button? remove = null; var confirmed = false;
            remove = Button("Web_Remove", () =>
            {
                if (!confirmed) { confirmed = true; remove!.Content = _loc["Web_ConfirmRemove"]; return; }
                try { _workspace.Remove(tab.Id); if (_webTabs.Remove(tab.Id, out var old)) WebTabHost.Children.Remove(old); if (_activeWebsite == tab.Id) Navigate(CurrentPage); RebuildWebsiteRail(); dialog.Close(); }
                catch (System.IO.IOException) { Fail("Web_Failed"); }
            });
        }
        dialog.Content = body; dialog.KeyDown += (_, e) => { if (e.Key == Key.Escape) { dialog.Close(); e.Handled = true; } if (e.Key == Key.Enter && (name.IsKeyboardFocusWithin || url.IsKeyboardFocusWithin)) { Save(); e.Handled = true; } };
        dialog.Opened += (_, _) => name.Focus(); await dialog.ShowDialog(this); AddWebsite.Focus();
    }
}
