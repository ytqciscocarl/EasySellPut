using System.Windows;
using System.Windows.Media;
using Meili.Shared.Services;
namespace Meili.App.Views.Controls;
public sealed class QuoteSparkline : FrameworkElement
{
    public IntradaySparkline Data{get;set;}=IntradaySparkline.Empty;
    public Brush LineBrush{get;set;}=Brushes.Gray;
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);var w=ActualWidth;var h=ActualHeight;if(Data.Points.Count<2||w<=0||h<=0)return;
        if(Data.Baseline is { } b)context.DrawLine(new Pen(LineBrush,.6){DashStyle=DashStyles.Dash},new Point(0,b*h),new Point(w,b*h));
        SparkPoint? previous=null;foreach(var p in Data.Points){if(previous is { } old && !p.Break)context.DrawLine(new Pen(LineBrush,1.7),new Point(old.X*w,old.Y*h),new Point(p.X*w,p.Y*h));previous=p;}
        var last=Data.Points[^1];context.DrawEllipse(LineBrush,null,new Point(last.X*w,last.Y*h),2,2);
    }
}
