using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Meili.App.Views.Controls;

/// <summary>One compact loading component; reduced motion shows a static, labelled activity bar.</summary>
public sealed class AppLoading : Border
{
    private readonly ProgressBar _bar=new(){Width=48,Height=4,MinWidth=0,MinHeight=0,Minimum=0,Maximum=100,Value=0};
    private readonly Border _rest=new(){Width=16,Height=4};
    public AppLoading()
    {
        ClipToBounds=true;_bar.ClipToBounds=true;Width=48;Height=16;VerticalAlignment=VerticalAlignment.Center;Child=new Grid{Children={_bar,_rest}};
        _rest.SetResourceReference(BackgroundProperty,"BrandBrush");
        SetResourceReference(AutomationProperties.NameProperty,"Reader_Loading");
        Loaded+=(_,_)=>{DesignMotion.Changed+=MotionChanged;Refresh();};Unloaded+=(_,_)=>{DesignMotion.Changed-=MotionChanged;_bar.IsIndeterminate=false;};
        IsVisibleChanged+=(_,_)=>Refresh();
    }
    private void MotionChanged(object? sender,EventArgs e)=>Refresh();
    private void Refresh(){_bar.IsIndeterminate=IsLoaded&&IsVisible&&!DesignMotion.Reduced;_bar.Visibility=DesignMotion.Reduced?Visibility.Collapsed:Visibility.Visible;_rest.Visibility=DesignMotion.Reduced?Visibility.Visible:Visibility.Collapsed;}
}
