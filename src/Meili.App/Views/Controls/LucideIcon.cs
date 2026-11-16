using Path = System.Windows.Shapes.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using Meili.Shared.Services;

namespace Meili.App.Views.Controls;

/// <summary>Pinned Lucide geometry in its original 24px canvas; no font or bounds stretching.</summary>
public sealed class LucideIcon : Wpf.Ui.Controls.IconElement
{
    public static readonly DependencyProperty KindProperty=DependencyProperty.Register(nameof(Kind),typeof(string),typeof(LucideIcon),new PropertyMetadata("Info",(d,_)=>((LucideIcon)d).Update()));
    public string Kind { get=>(string)GetValue(KindProperty);set=>SetValue(KindProperty,value); }
    private Viewbox? _view;
    public LucideIcon(){Width=18;Height=18;HorizontalAlignment=HorizontalAlignment.Center;VerticalAlignment=VerticalAlignment.Center;}
    protected override UIElement InitializeChildren() => _view=View(NavigationIcons.Get(Kind),this,double.NaN);
    private void Update(){if(_view is not null)((Path)((Canvas)_view.Child).Children[0]).Data=Geometry.Parse(NavigationIcons.Get(Kind));}
    public static Viewbox View(string data,object foregroundSource,double size=24)
    {
        var path=new Path{Data=Geometry.Parse(data),Width=24,Height=24,Stretch=Stretch.None,StrokeThickness=2,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};
        path.SetBinding(Shape.StrokeProperty,new Binding("Foreground"){Source=foregroundSource});
        var canvas=new Canvas{Width=24,Height=24};canvas.Children.Add(path);
        return new Viewbox{Width=size,Height=size,Stretch=Stretch.Uniform,Child=canvas};
    }
}

[MarkupExtensionReturnType(typeof(LucideIcon))]
public sealed class LucideExtension(string kind) : MarkupExtension
{
    public double Size { get;set; }=20;
    public override object ProvideValue(IServiceProvider serviceProvider)=>new LucideIcon{Kind=kind,Width=Size,Height=Size};
}
