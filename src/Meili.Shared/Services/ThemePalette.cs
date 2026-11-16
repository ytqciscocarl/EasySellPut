namespace Meili.Shared.Services;

/// <summary>
/// Claude-inspired desktop colour roles, generated from DesignSystem/tokens.json. This is the single source both front ends and
/// <see cref="ChartPalette"/> read (0xAARRGGBB).
/// </summary>
public sealed record ThemePalette(
    uint Window, uint Sidebar, uint Card, uint Chart, uint Control, uint Border,
    uint PrimaryText, uint SecondaryText, uint MutedText,
    uint Up, uint Down, uint Healthy, uint Degraded, uint DownState, uint Unknown,
    uint Accent, uint OnAccent, uint RowHighlight, uint ItmTint, uint Placeholder,
    uint Badge, uint BadgeText, uint ErrorBar, uint InfoBar,
    byte GridAlpha,
    uint Series1, uint Series2, uint Series3, uint Series4, uint Series5, uint Series6, uint PreMarketTint, uint AfterHoursTint)
{
    /// <summary>Alpha of the up/down tag fills behind up/down text.</summary>
    // 0x1A left dark-theme Down on the card fill at 4.49:1; 0x18 (~9%) restores 2265 4.5:1.
    public const byte TintAlpha = 0x18;

    public static ThemePalette Dark => DesignTokens.Dark;
    public static ThemePalette Light => DesignTokens.Light;

    public static ThemePalette For(bool dark) => dark ? Dark : Light;

    /// <summary>The up and down colours for a convention (red-up for China, green-up for the US).</summary>
    public (uint Up, uint Down) UpDown(Meili.Core.Settings.UpDownColorMode mode) =>
        mode == Meili.Core.Settings.UpDownColorMode.RedUp ? (Up, Down) : (Down, Up);

    public static uint Tint(uint color) => ((uint)TintAlpha << 24) | (color & 0x00FFFFFF);

    /// <summary>
    /// Resource keys both front ends install after a theme apply (and that macOS ThemeDictionaries mirror).
    /// WPF-UI resets the Fluent keys to #202020/#FFFFFF on every Apply; these must be written afterwards.
    /// </summary>
    public IEnumerable<(string Key, uint Argb)> ResourceBrushes()
    {
        var accentHover = (0xCC000000) | (Accent & 0x00FFFFFF);
        var accentPressed = (0x99000000) | (Accent & 0x00FFFFFF);
        return new (string Key,uint Argb)[]
        {
            ("CheckBoxCheckBackgroundFillChecked", Accent),
            ("CheckBoxCheckGlyphForegroundChecked", OnAccent),
            ("ToggleSwitchFillOn", Accent),
            ("ToggleSwitchStrokeOn", Accent),
            ("ToggleSwitchKnobFillOn", OnAccent),
            ("RadioButtonOuterEllipseFillChecked", Accent),
            ("RadioButtonCheckGlyphFill", OnAccent),
            ("CheckBoxCheckBackgroundFillCheckedPointerOver", Accent),
            ("CheckBoxCheckGlyphForegroundCheckedPointerOver", OnAccent),
            ("ToggleSwitchFillOnPointerOver", Accent),
            ("ToggleSwitchStrokeOnPointerOver", Accent),
            ("ToggleSwitchKnobFillOnPointerOver", OnAccent),
            ("RadioButtonOuterEllipseFillCheckedPointerOver", Accent),
            ("RadioButtonCheckGlyphFillPointerOver", OnAccent),
            ("CheckBoxCheckBackgroundFillCheckedPressed", Accent),
            ("CheckBoxCheckGlyphForegroundCheckedPressed", OnAccent),
            ("ToggleSwitchFillOnPressed", Accent),
            ("ToggleSwitchStrokeOnPressed", Accent),
            ("ToggleSwitchKnobFillOnPressed", OnAccent),
            ("RadioButtonOuterEllipseFillCheckedPressed", Accent),
            ("RadioButtonCheckGlyphFillPressed", OnAccent),
            ("SystemControlHighlightAccentBrush", Accent),
            ("ButtonBackground", Control),
            ("ButtonBackgroundPointerOver", RowHighlight),
            ("ButtonForeground", PrimaryText),
            ("ButtonForegroundPointerOver", PrimaryText),
            ("ButtonBorderBrush", Border),
            ("TextControlBackground", Card),
            ("TextControlBackgroundFocused", Card),
            ("TextControlForeground", PrimaryText),
            ("TextControlBorderBrush", Border),
            ("ComboBoxItemBackgroundSelected", RowHighlight),
            ("ComboBoxItemForegroundSelected", PrimaryText),
            ("ComboBoxItemBackgroundSelectedPointerOver", RowHighlight),
            ("ComboBoxItemForegroundSelectedPointerOver", PrimaryText),
            ("ComboBoxItemBackgroundSelectedPressed", RowHighlight),
            ("ComboBoxItemForegroundSelectedPressed", PrimaryText),
            ("ComboBoxItemBackgroundSelectedDisabled", RowHighlight),
            ("ComboBoxItemForegroundSelectedDisabled", PrimaryText),
            ("ComboBoxDropDownBackground", Card),
            ("ComboBoxDropDownBorderBrush", Border),
            ("ComboBoxItemBackground", Card),
            ("ComboBoxItemBackgroundPointerOver", Control),
            ("ComboBoxItemForeground", PrimaryText),
            ("ComboBoxBackground", Card),
            ("ComboBoxBackgroundPointerOver", Control),
            ("ComboBoxForeground", PrimaryText),
            ("ComboBoxBorderBrush", Border),
            ("ApplicationBackgroundBrush", Window),
            ("WindowBackground", Window),
            ("SidebarBackgroundBrush", Sidebar),
            ("CardBackgroundFillColorDefaultBrush", Card),
            ("CardBackgroundFillColorSecondaryBrush", Card),
            ("SolidBackgroundFillColorBaseBrush", Window),
            ("SolidBackgroundFillColorSecondaryBrush", Card),
            ("SolidBackgroundFillColorTertiaryBrush", Control),
            ("SolidBackgroundFillColorQuarternaryBrush", Control),
            ("SolidBackgroundFillColorBaseAltBrush", Sidebar),
            ("LayerFillColorDefaultBrush", Window),
            ("LayerFillColorAltBrush", Card),
            ("NavigationViewContentBackground", Window),
            ("LoadingScreenBackground", Window),
            ("ControlFillColorDefaultBrush", Control),
            ("ControlFillColorSecondaryBrush", Control),
            ("ControlFillColorTertiaryBrush", Control),
            ("ControlFillColorInputActiveBrush", Control),
            ("ControlSolidFillColorDefaultBrush", Control),
            ("ControlStrokeColorDefaultBrush", Border),
            ("ControlStrokeColorSecondaryBrush", Border),
            ("CardStrokeColorDefaultBrush", Border),
            ("CardStrokeColorDefaultSolidBrush", Border),
            ("DividerStrokeColorDefaultBrush", Border),
            ("NavigationViewContentGridBorderBrush", Border),
            ("TextFillColorPrimaryBrush", PrimaryText),
            ("TextFillColorSecondaryBrush", SecondaryText),
            ("TextFillColorTertiaryBrush", MutedText),
            ("TextControlPlaceholderForeground", Placeholder),
            ("AccentFillColorDefaultBrush", Accent),
            ("AccentFillColorSecondaryBrush", accentHover),
            ("AccentFillColorTertiaryBrush", accentPressed),
            ("SystemAccentColorPrimaryBrush", Accent),
            ("SystemAccentColorSecondaryBrush", accentHover),
            ("SystemAccentColorTertiaryBrush", accentPressed),
            ("SystemAccentColorBrush", Accent),
            ("AccentTextFillColorPrimaryBrush", Accent),
            ("TextOnAccentFillColorPrimaryBrush", OnAccent),
            ("NavigationViewSelectionIndicatorForeground", Accent),
            ("ErrorBarBrush", ErrorBar),
            ("InfoBarBrush", InfoBar),
            ("InfoBarErrorSeverityBackgroundBrush", ErrorBar),
            ("InfoBarInformationalSeverityBackgroundBrush", InfoBar),
            ("SystemFillColorCriticalBackgroundBrush", ErrorBar),
            ("SystemFillColorAttentionBackgroundBrush", InfoBar),
            ("StaleBadgeBrush", Badge),
            ("BadgeTextBrush", BadgeText),
        }.Concat(DesignTokens.ComponentBrushes(ReferenceEquals(this, Dark)));
    }
}
