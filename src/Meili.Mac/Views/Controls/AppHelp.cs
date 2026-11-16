using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;

namespace Meili.Mac.Views.Controls;

/// <summary>Compact help: full text on hover/focus, persistent on click or keyboard activation.</summary>
public sealed class AppHelp : AppPopover
{
    public static readonly StyledProperty<string> HelpTextProperty=AvaloniaProperty.Register<AppHelp,string>(nameof(HelpText),"");
    public string HelpText{get=>GetValue(HelpTextProperty);set=>SetValue(HelpTextProperty,value);}
    public bool IsHelpOpen=>IsExpanded;
    protected override bool UsesHeaderToolTip=>false;
    public AppHelp()
    {
        VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center;
        Width=28;Height=28;MinWidth=28;MinHeight=28;PopupWidth=320;
        Content=new LucideIcon{Kind="Help",Width=18,Height=18};
        this[!HeaderProperty]=this.GetResourceObservable("UX_Help").ToBinding();
        TextBlock Message(){var text=new TextBlock{MaxWidth=320,TextWrapping=TextWrapping.Wrap,FontSize=14};text.Bind(TextBlock.TextProperty,new Binding(nameof(HelpText)){Source=this});text[!TextBlock.ForegroundProperty]=this.GetResourceObservable("TextFillColorPrimaryBrush").ToBinding();return text;}
        PopupContent=Message();ToolTip.SetTip(this,Message());
        GotFocus+=(_,_)=>{if(!IsPointerOver && !IsExpanded)ToolTip.SetIsOpen(this,true);};
        LostFocus+=(_,_)=>ToolTip.SetIsOpen(this,false);
        DetachedFromVisualTree+=(_,_)=>ToolTip.SetIsOpen(this,false);
    }
}
