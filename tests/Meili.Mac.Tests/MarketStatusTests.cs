using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Meili.Shared.Tests;
using Avalonia.Headless.XUnit;
using Meili.Core.Settings;
using Meili.Mac.Platform;
using Meili.Mac.Views.Controls;
namespace Meili.Mac.Tests;
public sealed class MarketStatusTests
{
    [AvaloniaTheory]
    [InlineData("zh-CN", false)]
    [InlineData("en-US", true)]
    public async Task Market_and_source_health_use_one_compact_help_entry(string language, bool dark)
    {
        MacHost.EnsureComposed(); var loc = AppServices.Get<LocalizationService>(); var original = loc.Language;
        var theme = AppServices.Get<MacTheme>(); loc.Apply(language); theme.Apply(dark ? AppThemeMode.Dark : AppThemeMode.Light);
        using var update = new UpdateViewModel(new(MacHost.DataDir), loc, AppServices.DataDirectory, new FakeUpdates());
        var w = new MainWindow();
        try
        {
            w.UseUpdateState(update); w.Show(); await update.CheckUpdatesCommand.ExecuteAsync(null); update.DismissUpdatePromptCommand.Execute(null); w.Width = 1920; w.Height = 1080; MacHost.Pump();
            var shell = AppServices.Get<ShellViewModel>(); var status = w.FindControl<AppHelp>("MarketStatusIndicator")!;
            Assert.Equal(shell.MarketStatusTip, status.HelpText);
            Assert.Equal(shell.MarketStatusTip, Avalonia.Automation.AutomationProperties.GetName(status));
            Assert.NotEmpty(shell.SourceCountText); Assert.InRange(status.Bounds.Width, 40, 220);
            AssertAlignment(w);
            status.IsExpanded = true; MacHost.Pump(); Assert.True(status.IsHelpOpen);
            MacHost.Save(w, $"market-status-details-{language}-{dark}");
            status.IsExpanded = false; MacHost.Pump();
            MacHost.Save(w, $"market-status-1080p-{language}-{dark}");
            w.Width = 980; MacHost.Pump(); Assert.InRange(status.Bounds.Width, 40, 220);
            Assert.False(status.IsExpanded); AssertAlignment(w);
        }
        finally { w.AllowClose(); w.Close(); loc.Apply(original); theme.Apply(AppThemeMode.System); }
    }
    private static void AssertAlignment(MainWindow w)
    {
        var bar = w.FindControl<Border>("BottomStatusBar")!;
        double Center(Control c) => c.TranslatePoint(new Point(0, c.Bounds.Height / 2), bar)!.Value.Y;
        var names = new[] { "StatusMarketText", "StatusClockText", "StatusMcpText", "StatusUpdateText" };
        var center = Center(w.FindControl<TextBlock>(names[0])!);
        foreach (var name in names)
        {
            var text = w.FindControl<TextBlock>(name)!;
            Assert.True(text.IsEffectivelyVisible); Assert.InRange(Math.Abs(Center(text) - center), 0, 0.75);
            Assert.InRange(Math.Abs(text.Bounds.Height - DesignTokens.StatusLineHeight), 0, 0.75);
        }
        Assert.InRange(Math.Abs(center - bar.Bounds.Height / 2), 0, 0.75);
        foreach (var icon in bar.GetVisualDescendants().OfType<LucideIcon>()) Assert.InRange(Math.Abs(Center(icon) - center), 0, 0.75);
        foreach (var dot in bar.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>()) Assert.InRange(Math.Abs(Center(dot) - center), 0, 0.75);
        foreach (var name in new[] { "MarketStatusIndicator", "McpStatusIndicator", "UpdateStatusIndicator", "SettingsButton", "LanguageButton", "ThemeButton", "BackgroundButton", "MotionButton" })
            Assert.InRange(Math.Abs(Center(w.FindControl<Control>(name)!) - center), 0, 0.75);
    }
}
