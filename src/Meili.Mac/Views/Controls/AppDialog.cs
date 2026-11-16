using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Meili.Mac.Views.Controls;

/// <summary>Shared local modal chrome; dialog content owns validation and explicit actions.</summary>
public class AppDialog : Window
{
    public AppDialog()
    {
        Opened+=(_,_)=>{DesignMotion.Changed+=MotionChanged;MotionChanged(null,EventArgs.Empty);AppMotion.Enter(Content as Control);};
        Closed+=(_,_)=>DesignMotion.Changed-=MotionChanged;
        Width=440;MinWidth=400;SizeToContent=SizeToContent.Height;CanResize=false;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
        this[!BackgroundProperty]=this.GetResourceObservable("CardBackgroundFillColorDefaultBrush").ToBinding();
        this[!ForegroundProperty]=this.GetResourceObservable("TextFillColorPrimaryBrush").ToBinding();
        this[!FontFamilyProperty]=this.GetResourceObservable("AppFont").ToBinding();FontSize=DesignTokens.BodyFontSize;
    }
    private void MotionChanged(object? sender,EventArgs e)=>Classes.Set("ReducedMotion",DesignMotion.Reduced);
}
