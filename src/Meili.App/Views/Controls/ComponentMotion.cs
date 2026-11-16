using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Meili.App.Views.Controls;

/// <summary>Small interruptible chrome feedback; never animates a native browser surface or page layout.</summary>
public static class ComponentMotion
{
    private static readonly List<WeakReference<UIElement>> Active=[];
    static ComponentMotion()=>DesignMotion.Changed+=(_,_)=>{if(!DesignMotion.Reduced)return;foreach(var w in Active)if(w.TryGetTarget(out var e))e.BeginAnimation(UIElement.OpacityProperty,null);Active.Clear();};
    private static void Track(UIElement e){Active.RemoveAll(w=>!w.TryGetTarget(out var old)||ReferenceEquals(old,e));if(Active.Count<256)Active.Add(new(e));}

    public static readonly DependencyProperty EnabledProperty=DependencyProperty.RegisterAttached("Enabled",typeof(bool),typeof(ComponentMotion),new PropertyMetadata(false,Changed));
    public static bool GetEnabled(DependencyObject value)=>(bool)value.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject value,bool enabled)=>value.SetValue(EnabledProperty,enabled);
    private static void Changed(DependencyObject value,DependencyPropertyChangedEventArgs e)
    {
        if(value is not Control control)return;
        if((bool)e.NewValue){control.PreviewKeyDown+=OnKeyboard;control.PreviewKeyUp+=OnKeyboard;control.LostKeyboardFocus+=OnFocusLost;control.IsEnabledChanged+=OnEnabledChanged;control.MouseEnter+=OnPointer;control.MouseLeave+=OnPointer;control.PreviewMouseDown+=OnPointer;control.PreviewMouseUp+=OnPointer;}
        else{control.PreviewKeyDown-=OnKeyboard;control.PreviewKeyUp-=OnKeyboard;control.LostKeyboardFocus-=OnFocusLost;control.IsEnabledChanged-=OnEnabledChanged;control.MouseEnter-=OnPointer;control.MouseLeave-=OnPointer;control.PreviewMouseDown-=OnPointer;control.PreviewMouseUp-=OnPointer;control.BeginAnimation(UIElement.OpacityProperty,null);}
    }
    public static void Enter(UIElement? content)
    {
        if(content is null || HasNative(content))return;
        Track(content);content.BeginAnimation(UIElement.OpacityProperty,null);
        if(!DesignMotion.Reduced)content.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(DesignTokens.MotionStandardMs)){FillBehavior=FillBehavior.Stop,EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
    }
    private static bool HasNative(DependencyObject element)
    {
        if(element is HwndHost)return true;
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(element);i++)if(HasNative(VisualTreeHelper.GetChild(element,i)))return true;
        return false;
    }
    private static void OnKeyboard(object sender,KeyEventArgs e)
    {
        if(e.Key is not (Key.Space or Key.Enter))return;
        var c=(Control)sender;if(!c.IsEnabled)return;
        Track(c);c.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(e.RoutedEvent==Keyboard.PreviewKeyDownEvent ? .9:1,TimeSpan.FromMilliseconds(DesignMotion.Reduced?0:DesignTokens.MotionFastMs)){FillBehavior=FillBehavior.Stop});
    }
    private static void OnFocusLost(object sender,KeyboardFocusChangedEventArgs e)=>((Control)sender).BeginAnimation(UIElement.OpacityProperty,null);
    private static void OnEnabledChanged(object sender,DependencyPropertyChangedEventArgs e)=>((Control)sender).BeginAnimation(UIElement.OpacityProperty,null);
    private static void OnPointer(object sender,MouseEventArgs e)
    {
        var control=(Control)sender;if(!control.IsEnabled)return;
        var opacity=control.IsMouseOver?(e.RoutedEvent==Mouse.PreviewMouseDownEvent ? .86 : .96):1;
        Track(control);control.BeginAnimation(UIElement.OpacityProperty,new DoubleAnimation(opacity,TimeSpan.FromMilliseconds(DesignMotion.Reduced?0:DesignTokens.MotionFastMs)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut}});
    }
}
