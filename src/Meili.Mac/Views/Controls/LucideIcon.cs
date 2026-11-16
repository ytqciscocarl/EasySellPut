using Path = Avalonia.Controls.Shapes.Path;
using Meili.Shared.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Controls.Shapes;

namespace Meili.Mac.Views.Controls;

/// <summary>Pinned Lucide geometry in its original 24px canvas; no font or bounds stretching.</summary>
public sealed class LucideIcon : UserControl
{
    public static readonly StyledProperty<string> KindProperty=AvaloniaProperty.Register<LucideIcon,string>(nameof(Kind),"Info");
    public string Kind{get=>GetValue(KindProperty);set=>SetValue(KindProperty,value);}
    private Viewbox? _view;
    public LucideIcon(){Width=18;Height=18;HorizontalAlignment=Avalonia.Layout.HorizontalAlignment.Center;VerticalAlignment=Avalonia.Layout.VerticalAlignment.Center;Update();}
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change){base.OnPropertyChanged(change);if(change.Property==KindProperty)Update();}
    private void Update(){if(_view is null)Content=_view=View(NavigationIcons.Get(Kind),this,double.NaN);else ((Path)((Canvas)_view.Child!).Children[0]).Data=Geometry.Parse(NavigationIcons.Get(Kind));}
    public static Viewbox View(string data,object foregroundSource,double size=24)
    {
        var path=new Path{Data=Geometry.Parse(data),Width=24,Height=24,Stretch=Stretch.None,StrokeThickness=2,StrokeLineCap=PenLineCap.Round,StrokeJoin=PenLineJoin.Round};
        path.Bind(Shape.StrokeProperty,new Binding("Foreground"){Source=foregroundSource});
        var canvas=new Canvas{Width=24,Height=24};canvas.Children.Add(path);
        return new Viewbox{Width=size,Height=size,Stretch=Stretch.Uniform,Child=canvas};
    }
}
