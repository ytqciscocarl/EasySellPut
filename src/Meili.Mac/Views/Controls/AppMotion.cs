using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Meili.Mac.Views.Controls;

/// <summary>Short feedback on app-owned chrome only. High-frequency data and browser surfaces stay immediate.</summary>
public static class AppMotion
{
    private static readonly List<WeakReference<Control>> Active=[];
    static AppMotion()=>DesignMotion.Changed+=(_,_)=>{if(!DesignMotion.Reduced)return;foreach(var w in Active)if(w.TryGetTarget(out var c)){c.Transitions=null;c.Opacity=1;}Active.Clear();};

    public static void Enter(Control? content)
    {
        if(content is null || content is NativeControlHost || content.GetVisualDescendants().Any(x=>x is NativeControlHost))return;
        Active.RemoveAll(w=>!w.TryGetTarget(out var c)||ReferenceEquals(c,content));
        if(Active.Count<256)Active.Add(new(content));
        content.Transitions=null;content.Opacity=DesignMotion.Reduced?1:0;
        if(!DesignMotion.Reduced)content.Transitions=new Transitions{new DoubleTransition{Property=Visual.OpacityProperty,Duration=TimeSpan.FromMilliseconds(DesignTokens.MotionStandardMs)}};
        content.Opacity=1;
    }
}
