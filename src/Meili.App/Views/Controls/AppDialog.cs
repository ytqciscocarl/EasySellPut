using System.Windows;

namespace Meili.App.Views.Controls;

/// <summary>Shared local modal chrome; dialog content owns validation and explicit actions.</summary>
public class AppDialog : Window
{
    public AppDialog()
    {
        ContentRendered+=(_,_)=>ComponentMotion.Enter(Content as UIElement);
        Width=440;MinWidth=400;SizeToContent=SizeToContent.Height;ResizeMode=ResizeMode.NoResize;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
        SetResourceReference(BackgroundProperty,"CardBackgroundFillColorDefaultBrush");SetResourceReference(ForegroundProperty,"TextFillColorPrimaryBrush");
        SetResourceReference(FontFamilyProperty,"AppFont");FontSize=DesignTokens.BodyFontSize;
    }
}
