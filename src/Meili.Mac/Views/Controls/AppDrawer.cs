using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace Meili.Mac.Views.Controls;

/// <summary>Pinned inspector surface. Escape closes through the original two-way binding.</summary>
public sealed class AppDrawer : Border
{
    public static readonly StyledProperty<bool> IsOpenProperty=AvaloniaProperty.Register<AppDrawer,bool>(nameof(IsOpen),false,defaultBindingMode:BindingMode.TwoWay);
    public static readonly StyledProperty<Control?> ReturnFocusTargetProperty=AvaloniaProperty.Register<AppDrawer,Control?>(nameof(ReturnFocusTarget));
    public Control? ReturnFocusTarget{get=>GetValue(ReturnFocusTargetProperty);set=>SetValue(ReturnFocusTargetProperty,value);}
    public bool IsOpen{get=>GetValue(IsOpenProperty);set=>SetValue(IsOpenProperty,value);}
    public AppDrawer()
    {
        BorderThickness=new Thickness(1,0,0,0);
        this[!BackgroundProperty]=this.GetResourceObservable("CardBackgroundFillColorDefaultBrush").ToBinding();
        this[!BorderBrushProperty]=this.GetResourceObservable("ControlStrokeColorDefaultBrush").ToBinding();
        IsVisible=false;
        AddHandler(KeyDownEvent,(_,e)=>{if(e.Key==Key.Escape && IsOpen){SetCurrentValue(IsOpenProperty,false);e.Handled=true;}},Avalonia.Interactivity.RoutingStrategies.Bubble);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if(change.Property==IsOpenProperty){IsVisible=IsOpen;if(IsOpen)AppMotion.Enter(Child);if(!IsOpen && ReturnFocusTarget?.IsEffectivelyVisible==true)ReturnFocusTarget.Focus();}
    }
}
