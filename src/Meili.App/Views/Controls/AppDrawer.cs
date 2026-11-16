using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Meili.App.Views.Controls;

/// <summary>Pinned inspector surface. Escape closes through the original two-way binding.</summary>
public sealed class AppDrawer : Border
{
    public static readonly DependencyProperty IsOpenProperty=DependencyProperty.Register(nameof(IsOpen),typeof(bool),typeof(AppDrawer),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,(d,e)=>((AppDrawer)d).SetOpen((bool)e.NewValue)));
    public static readonly DependencyProperty ReturnFocusTargetProperty=DependencyProperty.Register(nameof(ReturnFocusTarget),typeof(Control),typeof(AppDrawer));
    public Control? ReturnFocusTarget{get=>(Control?)GetValue(ReturnFocusTargetProperty);set=>SetValue(ReturnFocusTargetProperty,value);}
    private void SetOpen(bool open){Visibility=open?Visibility.Visible:Visibility.Collapsed;if(open)ComponentMotion.Enter(Child);if(!open && ReturnFocusTarget?.IsVisible==true)ReturnFocusTarget.Focus();}
    public bool IsOpen{get=>(bool)GetValue(IsOpenProperty);set=>SetValue(IsOpenProperty,value);}
    public AppDrawer()
    {
        Visibility=Visibility.Collapsed;BorderThickness=new Thickness(1,0,0,0);
        SetResourceReference(BackgroundProperty,"CardBackgroundFillColorDefaultBrush");SetResourceReference(BorderBrushProperty,"ControlStrokeColorDefaultBrush");
        KeyDown+=(_,e)=>{if(e.Key==Key.Escape && IsOpen){SetCurrentValue(IsOpenProperty,false);e.Handled=true;}};
    }
}
