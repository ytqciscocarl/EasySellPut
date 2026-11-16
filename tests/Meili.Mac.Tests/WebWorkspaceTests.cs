using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Meili.Core.Settings;
using Meili.Mac.Platform;

namespace Meili.Mac.Tests;

public sealed class WebWorkspaceTests
{
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    [AvaloniaFact]
    public void Builtin_pages_and_websites_share_one_narrow_rail_with_one_selection()
    {
        MacHost.EnsureComposed(); var w=new MainWindow{Width=980,Height=620};w.Show();MacHost.Pump();
        try
        {
            Assert.Null(w.FindControl<Control>("Nav"));
            var rail=w.FindControl<StackPanel>("BuiltinRail")!;Assert.Equal(MainWindow.PageOrder.Count-2,rail.Children.Count);
            foreach(var page in MainWindow.PageOrder.Where(p=>p!=AppPage.Dashboard))
            {
                var button=page==AppPage.Settings?w.FindControl<ToggleButton>("SettingsButton")!:rail.Children.OfType<ToggleButton>().Single(b=>Equals(b.Tag,page));
                Assert.Equal(LocalizationService.Current[MainWindow.TitleKey(page)],AutomationProperties.GetName(button));
                Assert.NotNull(ToolTip.GetTip(button));
                button.BringIntoView();MacHost.Pump();Click(button);MacHost.Pump();
                Assert.Equal(page,w.CurrentPage);Assert.True(button.IsChecked);
                Assert.Single(w.GetVisualDescendants().OfType<ToggleButton>(),b=>(b.Classes.Contains("Workspace") || b.Name=="SettingsButton") && b.IsChecked==true);
                Assert.InRange(w.CurrentView!.TranslatePoint(new Point(),w)!.Value.X,63,65);
            }
            var home=w.FindControl<ToggleButton>("HomeWorkspace")!;Click(home);MacHost.Pump();Assert.Equal(AppPage.Dashboard,w.CurrentPage);Assert.True(home.IsChecked);
            Assert.True(w.FindControl<ScrollViewer>("SidebarScroll")!.Bounds.Width<=64);
            foreach(var owner in w.GetVisualDescendants().OfType<Button>().Where(b=>b.Classes.Contains("Workspace") || b.Classes.Contains("StatusAction")))
            {
                if(owner.Content is not Control icon)continue;
                var center=icon.TranslatePoint(new Point(icon.Bounds.Width/2,icon.Bounds.Height/2),owner)!.Value;
                Assert.InRange(Math.Abs(center.X-owner.Bounds.Width/2),0,.6);Assert.InRange(Math.Abs(center.Y-owner.Bounds.Height/2),0,.6);
                if(owner.Classes.Contains("Workspace"))Assert.InRange(owner.TranslatePoint(new Point(owner.Bounds.Width/2,0),w)!.Value.X,31,33);
            }
            var footer=w.FindControl<Border>("BottomStatusBar")!;
            foreach(var name in new[]{"SettingsButton","LanguageButton","ThemeButton","MotionButton"})
            {
                var control=w.FindControl<Control>(name)!;Assert.Contains(footer,control.GetVisualAncestors());
                Assert.Equal(28,control.Bounds.Width);Assert.Equal(24,control.Bounds.Height);
            }
        }
        finally{w.Close();}
    }
    [AvaloniaFact]
    public async Task Website_editor_persists_tabs_and_switching_preserves_attached_browser()
    {
        MacHost.EnsureComposed();
        var workspace = new WebWorkspace(MacHost.DataDir);
        foreach (var tab in workspace.Tabs.ToList()) workspace.Remove(tab.Id);
        var w = new MainWindow(); w.Show(); MacHost.Pump();
        try
        {
            var editing = w.EditWebsite(null); MacHost.Pump(); var dialog = w.OwnedWindows.Single();
            var inputs = dialog.GetVisualDescendants().OfType<TextBox>().ToList();
            inputs.Single(t => t.Name == "WebsiteName").Text = "测试网页";
            inputs.Single(t => t.Name == "WebsiteAddress").Text = "file:///tmp/blocked";
            var save = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, LocalizationService.Current["Web_Save"]));
            Click(save); MacHost.Pump(); Assert.True(dialog.IsVisible);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == LocalizationService.Current["Web_Invalid"] && t.IsVisible);
            inputs.Single(t => t.Name == "WebsiteAddress").Text = "https://example.com/";
            Click(save); MacHost.Pump(); await editing;
            var saved = Assert.Single(new WebWorkspace(MacHost.DataDir).Tabs);
            Assert.False(w.FindControl<ToggleButton>("HomeWorkspace")!.IsChecked);
            Assert.All(w.FindControl<StackPanel>("BuiltinRail")!.Children.OfType<ToggleButton>(),b=>Assert.False(b.IsChecked));
            var browser = Assert.Single(w.GetVisualDescendants().OfType<WebTabPane>());
            w.Navigate(AppPage.Dashboard); MacHost.Pump(); Assert.False(browser.IsEffectivelyVisible);
            w.OpenWebsite(saved); MacHost.Pump(); Assert.Same(browser, Assert.Single(w.GetVisualDescendants().OfType<WebTabPane>()));
            var wizard = w.WizardVisible; w.KeyTextInput("A"); MacHost.Pump(); Assert.Equal(wizard, w.WizardVisible);
            w.Navigate(AppPage.Dashboard); MacHost.Pump();
            Assert.True(w.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "HomeWorkspace").IsChecked);
        }
        finally { w.Close(); var clean = new WebWorkspace(MacHost.DataDir); foreach (var t in clean.Tabs.ToList()) clean.Remove(t.Id); }
    }
    [AvaloniaFact]
    public async Task Workspace_screenshots_cover_languages_themes_and_editor()
    {
        MacHost.EnsureComposed(); var store = new WebWorkspace(MacHost.DataDir);
        foreach (var t in store.Tabs.ToList()) store.Remove(t.Id);
        store.Save(null, "Financial Times · 测试网页", "https://www.ft.com/"); store.Save(null, "Reuters · 测试网页", "https://www.reuters.com/");
        var loc = LocalizationService.Current; var theme = AppServices.Get<MacTheme>(); var w = new MainWindow { Width=1320, Height=840 }; w.Show();
        try
        {
            theme.Apply(AppThemeMode.Dark); loc.Apply("zh-CN"); w.Navigate(AppPage.Dashboard); MacHost.Pump(); w.FindControl<ScrollViewer>("SidebarScroll")!.Offset=default; MacHost.Pump(); MacHost.Save(w, "single-rail-dark");
            var editing = w.EditWebsite(null); MacHost.Pump(); var editor = w.OwnedWindows.Single(); MacHost.Save(editor, "single-rail-add"); editor.Close(); await editing;
            theme.Apply(AppThemeMode.Light); loc.Apply("en-US"); w.Width = 980; w.FindControl<ScrollViewer>("SidebarScroll")!.Offset=default; MacHost.Pump(); MacHost.Save(w, "single-rail-light-compact");
            var motion = w.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "MotionButton"); motion.IsChecked = true; Click(motion); MacHost.Pump(); Assert.Contains("ReducedMotion", w.Classes);
        }
        finally { w.Close(); var clean = new WebWorkspace(MacHost.DataDir); foreach (var t in clean.Tabs.ToList()) clean.Remove(t.Id); clean.SetReduceMotion(false); theme.Apply(AppThemeMode.System); loc.Apply("zh-CN"); }
    }
}
