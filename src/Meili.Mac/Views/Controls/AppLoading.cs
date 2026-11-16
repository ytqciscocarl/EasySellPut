using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;

namespace Meili.Mac.Views.Controls;

/// <summary>One compact loading component; reduced motion shows a static, labelled activity bar.</summary>
public sealed class AppLoading : Border
{
    private readonly ProgressBar _bar=new(){Width=48,Height=4,MinWidth=0,MinHeight=0,Minimum=0,Maximum=100,Value=0};
    private readonly Border _rest=new(){Width=16,Height=4};
    public AppLoading()
    {
        ClipToBounds=true;_bar.ClipToBounds=true;Width=48;Height=16;VerticalAlignment=VerticalAlignment.Center;Child=new Grid{Children={_bar,_rest}};
        _rest[!BackgroundProperty]=this.GetResourceObservable("BrandBrush").ToBinding();
        this[!AutomationProperties.NameProperty]=this.GetResourceObservable("Reader_Loading").ToBinding();
        AttachedToVisualTree+=(_,_)=>{DesignMotion.Changed+=MotionChanged;Refresh();};
        DetachedFromVisualTree+=(_,_)=>{DesignMotion.Changed-=MotionChanged;_bar.IsIndeterminate=false;};
        PropertyChanged+=(_,e)=>{if(e.Property==IsVisibleProperty)Refresh();};
    }
    private void MotionChanged(object? sender,EventArgs e)=>Refresh();
    private void Refresh(){_bar.IsIndeterminate=VisualRoot is not null&&IsEffectivelyVisible&&!DesignMotion.Reduced;_bar.IsVisible=!DesignMotion.Reduced;_rest.IsVisible=DesignMotion.Reduced;}
}
