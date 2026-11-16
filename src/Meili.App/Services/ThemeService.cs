using System.Windows;
using System.Windows.Media;
using Meili.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Meili.App.Services;

/// <summary>Light/dark/system theme via WPF-UI, plus the up/down price colour brushes.</summary>
public sealed class ThemeService : IThemeController
{
    void IThemeController.Apply(AppThemeMode mode) => Apply(mode);
    void IThemeController.ApplyUpDownColors(UpDownColorMode mode) => ApplyUpDownColors(mode);

    /// <summary>
    /// 0.4.1: solid window background instead of Mica. WPF-UI only re-tints a Mica backdrop reliably at startup;
    /// after a runtime light/dark switch the transparent title bar and navigation pane kept showing the old tint
    /// (lepoco/wpfui #927, #1163, #1222, #1639). With no backdrop every surface is a theme brush and switches with it.
    /// </summary>
    public const WindowBackdropType Backdrop = WindowBackdropType.None;

    private Window? _watched;
    public AppThemeMode Mode { get; private set; } = AppThemeMode.System;
    public event EventHandler? ThemeChanged;

    public bool IsDark => ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    // Every theme change, including one the system watcher makes on its own, re-tones the semantic brushes.
    public ThemeService() => ApplicationThemeManager.Changed += (_, _) =>
    {
        ApplySemanticColors(IsDark);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    };

    /// <summary>
    /// Switches the WPF-UI theme dictionary at application level only. Pages and the window resolve every brush
    /// through DynamicResource against <see cref="Application.Resources"/>; nothing is copied onto elements
    /// (0.4.0 called ApplicationThemeManager.Apply(window), which snapshots the application resources into the
    /// window and left stale up/down, row and ITM brushes plus the old string dictionary shadowing the new ones).
    /// </summary>
    public void Apply(AppThemeMode mode, Window? mainWindow = null)
    {
        Mode = mode;
        mainWindow ??= Application.Current.MainWindow;
        ClearWatch();

        switch (mode)
        {
            case AppThemeMode.Light: ApplicationThemeManager.Apply(ApplicationTheme.Light, Backdrop, true); break;
            case AppThemeMode.Dark: ApplicationThemeManager.Apply(ApplicationTheme.Dark, Backdrop, true); break;
            default:
                // WPF-UI's system variant always asks for Mica; reset the window to the solid background afterwards.
                ApplicationThemeManager.ApplySystemTheme(true);
                WindowBackgroundManager.UpdateBackground(mainWindow, ApplicationThemeManager.GetAppTheme(), Backdrop);
                if (mainWindow is not null && mainWindow.IsLoaded)
                {
                    SystemThemeWatcher.Watch(mainWindow, Backdrop, true);
                    _watched = mainWindow;
                    mainWindow.Closing += OnWatchedClosing;
                }
                break;
        }
    }

    private void ClearWatch()
    {
        if(_watched is not { } window)return;
        window.Closing-=OnWatchedClosing;
        // WPF-UI rejects unloaded handles. Normal close detaches earlier in Closing.
        if(window.IsLoaded)SystemThemeWatcher.UnWatch(window);
        _watched=null;
    }
    private void OnWatchedClosing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        // MainWindow's close policy runs first: a canceled close still needs its watcher.
        if(!e.Cancel&&ReferenceEquals(sender,_watched))ClearWatch();
    }

    /// <summary>Quick toggle from the title bar: flips to the opposite explicit theme.</summary>
    public AppThemeMode Toggle() => IsDark ? AppThemeMode.Light : AppThemeMode.Dark;

    private static UpDownColorMode _upDown = UpDownColorMode.RedUp;

    /// <summary>Up/down colours for the chosen convention, in the tones of the active theme (E4 contrast).</summary>
    public static void ApplyUpDownColors(UpDownColorMode mode)
    {
        _upDown = mode;
        ApplySemanticColors(ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark);
    }

    /// <summary>Raised after the up/down and health brushes were replaced; view models holding one re-fetch it.</summary>
    public static event EventHandler? ColorsChanged;

    /// <summary>
    /// Writes the 0.4.2 palette over WPF-UI's Fluent brushes (which Apply resets to #202020/#FFFFFF) and then
    /// the semantic tones. Called from <see cref="ApplicationThemeManager.Changed"/> so it always runs after Apply,
    /// including a change the system watcher makes on its own. Never copies resources onto a window (0.4.1 fix).
    /// </summary>
    public static void ApplySemanticColors(bool dark)
    {
        var p = ThemePalette.For(dark);
        foreach (var (key, argb) in p.ResourceBrushes())
            Set(key, C(argb));
        var (up, down) = p.UpDown(_upDown);
        Set("UpBrush", C(up));
        Set("DownBrush", C(down));
        Set("UpBackgroundBrush", C(ThemePalette.Tint(up)));
        Set("DownBackgroundBrush", C(ThemePalette.Tint(down)));
        Set("HealthyBrush", C(p.Healthy));
        Set("DegradedBrush", C(p.Degraded));
        Set("DownStateBrush", C(p.DownState));
        Set("UnknownBrush", C(p.Unknown));
        Set("RowHighlightBrush", C(p.RowHighlight));
        Set("ItmBrush", C(p.ItmTint));
        Application.Current.Resources["ApplicationBackgroundColor"] = C(p.Window);
        Application.Current.Resources["SystemAccentColor"] = C(p.Accent);
        Application.Current.Resources["SystemAccentColorPrimary"] = C(p.Accent);
        ColorsChanged?.Invoke(null, EventArgs.Empty);
    }

    private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    // WPF freezes brushes held in application resources, so each change installs a new brush.
    private static void Set(string key, Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        Application.Current.Resources[key] = b;
    }
}

/// <summary>E4: the shared <see cref="SemanticColors"/> as WPF colours.</summary>
public sealed record SemanticPalette(Color Red, Color Green, Color Healthy, Color Degraded, Color DownState, Color Unknown, Color RowHighlight,
    Color ItmTint, Color Placeholder)
{
    private static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static SemanticPalette From(SemanticColors c) => new(C(c.Red), C(c.Green), C(c.Healthy), C(c.Degraded), C(c.DownState),
        C(c.Unknown), C(c.RowHighlight), C(c.ItmTint), C(c.Placeholder));

    public static readonly SemanticPalette Light = From(SemanticColors.Light);
    public static readonly SemanticPalette Dark = From(SemanticColors.Dark);

    /// <summary>Badge fill behind BadgeText, e.g. the stale-quote tag; the same in both themes.</summary>
    public static readonly Color Badge = C(SemanticColors.Badge);

    /// <summary>Alpha of the up/down tag fills behind up/down text.</summary>
    public const byte TintAlpha = SemanticColors.TintAlpha;

    public static SemanticPalette For(bool dark) => dark ? Dark : Light;
}
