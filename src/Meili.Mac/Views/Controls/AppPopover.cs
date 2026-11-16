using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;

namespace Meili.Mac.Views.Controls;

/// <summary>Small icon trigger for secondary controls. Content remains bound when the flyout closes.</summary>
public class AppPopover : Button
{
    public static readonly StyledProperty<string> HeaderProperty=AvaloniaProperty.Register<AppPopover,string>(nameof(Header),"");
    public static readonly StyledProperty<object?> PopupContentProperty=AvaloniaProperty.Register<AppPopover,object?>(nameof(PopupContent));
    public static readonly StyledProperty<bool> IsExpandedProperty=AvaloniaProperty.Register<AppPopover,bool>(nameof(IsExpanded),defaultBindingMode:BindingMode.TwoWay);
    public static readonly StyledProperty<double> PopupWidthProperty=AvaloniaProperty.Register<AppPopover,double>(nameof(PopupWidth),320);
    public string Header{get=>GetValue(HeaderProperty);set=>SetValue(HeaderProperty,value);}
    public object? PopupContent{get=>GetValue(PopupContentProperty);set=>SetValue(PopupContentProperty,value);}
    public bool IsExpanded{get=>GetValue(IsExpandedProperty);set=>SetValue(IsExpandedProperty,value);}
    public double PopupWidth{get=>GetValue(PopupWidthProperty);set=>SetValue(PopupWidthProperty,value);}
    protected virtual bool UsesHeaderToolTip=>true;
    protected override Type StyleKeyOverride=>typeof(Button);
    public AppPopover()
    {
        Width=32;Height=32;MinWidth=32;MinHeight=32;Padding=default;Classes.Add("Subtle");
        Content=new LucideIcon{Kind="Settings",Width=18,Height=18};
        this[!AutomationProperties.NameProperty]=new Binding(nameof(Header)){Source=this};

        var presenter=new ContentControl();presenter.Bind(ContentControl.ContentProperty,new Binding(nameof(PopupContent)){Source=this});presenter.Bind(DataContextProperty,new Binding(nameof(DataContext)){Source=this});
        var scroll=new ScrollViewer{MaxHeight=400,Content=presenter,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};scroll.Bind(WidthProperty,new Binding(nameof(PopupWidth)){Source=this});
        var flyout=new Flyout{Placement=PlacementMode.BottomEdgeAlignedRight,Content=scroll};flyout.FlyoutPresenterClasses.Add("AppPopoverPresenter");
        flyout.Opened+=(_,_)=>{SetCurrentValue(IsExpandedProperty,true);AppMotion.Enter(presenter);};
        flyout.Closed+=(_,_)=>SetCurrentValue(IsExpandedProperty,false);Flyout=flyout;
        AttachedToVisualTree+=(_,_)=>{if(IsExpanded && !flyout.IsOpen)flyout.ShowAt(this);};
        DetachedFromVisualTree+=(_,_)=>{flyout.Hide();SetCurrentValue(IsExpandedProperty,false);};
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.Property==HeaderProperty && UsesHeaderToolTip)ToolTip.SetTip(this,Header);
        if(e.Property==IsExpandedProperty && Flyout is Flyout flyout)
        {
            if(IsExpanded){ToolTip.SetIsOpen(this,false);if(!flyout.IsOpen && VisualRoot is not null){Place(flyout);flyout.ShowAt(this);}}
            else if(!IsExpanded && flyout.IsOpen)flyout.Hide();
        }
    }
    private void Place(Flyout flyout){if(TopLevel.GetTopLevel(this) is { } root && this.TranslatePoint(new Point(),root) is { } point)flyout.Placement=point.X+Bounds.Width/2<root.Bounds.Width/2?PlacementMode.BottomEdgeAlignedLeft:PlacementMode.BottomEdgeAlignedRight;}
    protected override void OnClick(){ToolTip.SetIsOpen(this,false);if(IsExpanded){Flyout?.Hide();return;}if(Flyout is Flyout flyout)Place(flyout);base.OnClick();}
}
