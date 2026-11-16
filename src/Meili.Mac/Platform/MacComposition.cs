using Microsoft.Extensions.DependencyInjection;
using Meili.Core.Settings;

namespace Meili.Mac.Platform;

/// <summary>DI registrations for the Mac app: the shared ones plus the Avalonia theme, notifier and UI platform.</summary>
public static class MacComposition
{
    public static void Register(IServiceCollection s, DataDirectoryInfo dataDir)
    {
        Ui.Platform = new MacUiPlatform();
        ChartPainter.FontName = OperatingSystem.IsMacOS() ? "PingFang SC" : "Noto Sans CJK SC";   // CJK glyphs in chart titles and labels
        SharedComposition.Register(s, dataDir, MacStrings.Attach(new LocalizationService()));
        s.AddSingleton<MacTheme>();
        s.AddSingleton<IThemeController>(sp => sp.GetRequiredService<MacTheme>());
        s.AddSingleton<MacNotifier>();
        s.AddSingleton<IAlertNotifier>(sp => sp.GetRequiredService<MacNotifier>());
    }
}
