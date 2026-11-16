using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Meili.App.Views.Controls;

/// <summary>Reusable help icon: tooltip on hover/focus, persistent popup on activation.</summary>
public sealed class AppHelp : AppPopover
{
    public static readonly DependencyProperty HelpTextProperty=DependencyProperty.Register(nameof(HelpText),typeof(string),typeof(AppHelp),new PropertyMetadata(""));
    public string HelpText{get=>(string)GetValue(HelpTextProperty);set=>SetValue(HelpTextProperty,value);}
    public bool IsHelpOpen=>IsExpanded;
    public AppHelp()
    {
        VerticalAlignment=VerticalAlignment.Center;
        Width=28;Height=28;MinWidth=28;MinHeight=28;PopupWidth=320;
        Content=new LucideIcon{Kind="Help",Width=18,Height=18};SetResourceReference(HeaderProperty,"UX_Help");
        TextBlock Message(){var text=new TextBlock{MaxWidth=320,FontSize=14,TextWrapping=TextWrapping.Wrap};text.SetBinding(TextBlock.TextProperty,new Binding(nameof(HelpText)){Source=this});text.SetResourceReference(TextBlock.ForegroundProperty,"TextFillColorPrimaryBrush");text.SetResourceReference(TextBlock.FontFamilyProperty,"AppFont");return text;}
        PopupContent=Message();var tip=new ToolTip{Content=Message(),Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom,PlacementTarget=this};ToolTip=tip;
        GotKeyboardFocus+=(_,_)=>{if(!IsMouseOver && !IsExpanded)tip.IsOpen=true;};LostKeyboardFocus+=(_,_)=>tip.IsOpen=false;Unloaded+=(_,_)=>tip.IsOpen=false;
    }
}
