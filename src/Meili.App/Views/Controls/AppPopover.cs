using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;

namespace Meili.App.Views.Controls;

/// <summary>Reusable icon-triggered secondary controls with keyboard dismissal and retained content.</summary>
public class AppPopover : Button
{
    public static readonly DependencyProperty HeaderProperty=DependencyProperty.Register(nameof(Header),typeof(string),typeof(AppPopover),new PropertyMetadata(""));
    public static readonly DependencyProperty PopupContentProperty=DependencyProperty.Register(nameof(PopupContent),typeof(object),typeof(AppPopover));
    public static readonly DependencyProperty PopupWidthProperty=DependencyProperty.Register(nameof(PopupWidth),typeof(double),typeof(AppPopover),new PropertyMetadata(320d));
    public static readonly DependencyProperty IsExpandedProperty=DependencyProperty.Register(nameof(IsExpanded),typeof(bool),typeof(AppPopover),new FrameworkPropertyMetadata(false,FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,ExpandedChanged));
    public string Header{get=>(string)GetValue(HeaderProperty);set=>SetValue(HeaderProperty,value);}
    public object? PopupContent{get=>GetValue(PopupContentProperty);set=>SetValue(PopupContentProperty,value);}
    public double PopupWidth{get=>(double)GetValue(PopupWidthProperty);set=>SetValue(PopupWidthProperty,value);}
    public bool IsExpanded{get=>(bool)GetValue(IsExpandedProperty);set=>SetValue(IsExpandedProperty,value);}
    private readonly Popup _popup;
    public AppPopover()
    {
        Width=32;Height=32;MinWidth=32;MinHeight=32;Padding=default;SetResourceReference(StyleProperty,"GhostButton");
        Content=new LucideIcon{Kind="Settings",Width=18,Height=18};
        SetBinding(AutomationProperties.NameProperty,new Binding(nameof(Header)){Source=this});SetBinding(ToolTipProperty,new Binding(nameof(Header)){Source=this});
        var presenter=new ContentControl();presenter.SetBinding(ContentControl.ContentProperty,new Binding(nameof(PopupContent)){Source=this});presenter.SetBinding(DataContextProperty,new Binding(nameof(DataContext)){Source=this});
        var scroll=new ScrollViewer{MaxHeight=400,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=presenter};scroll.SetBinding(WidthProperty,new Binding(nameof(PopupWidth)){Source=this});
        var frame=new Border{CornerRadius=new CornerRadius(12),Padding=new Thickness(16),BorderThickness=new Thickness(1),Child=scroll};
        frame.SetResourceReference(Border.BackgroundProperty,"CardBackgroundFillColorDefaultBrush");frame.SetResourceReference(Border.BorderBrushProperty,"ControlStrokeColorDefaultBrush");frame.SetResourceReference(TextBlock.ForegroundProperty,"TextFillColorPrimaryBrush");frame.SetResourceReference(TextBlock.FontFamilyProperty,"AppFont");
        _popup=new Popup{PlacementTarget=this,Placement=PlacementMode.Bottom,StaysOpen=false,AllowsTransparency=true,Child=frame};
        frame.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){IsExpanded=false;Focus();e.Handled=true;}};
        _popup.Opened+=(_,_)=>ComponentMotion.Enter(frame);_popup.Closed+=(_,_)=>SetCurrentValue(IsExpandedProperty,false);
        Unloaded+=(_,_)=>IsExpanded=false;
    }
    private static void ExpandedChanged(DependencyObject d,DependencyPropertyChangedEventArgs e){var c=(AppPopover)d;if((bool)e.NewValue && c.ToolTip is ToolTip tip)tip.IsOpen=false;if(c._popup is not null)c._popup.IsOpen=(bool)e.NewValue;}
    protected override void OnClick(){if(ToolTip is ToolTip tip)tip.IsOpen=false;base.OnClick();IsExpanded=!IsExpanded;}
    protected override void OnPreviewKeyDown(KeyEventArgs e){if(e.Key==Key.Escape && IsExpanded){IsExpanded=false;Focus();e.Handled=true;}base.OnPreviewKeyDown(e);}
}
