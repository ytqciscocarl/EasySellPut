using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Meili.Shared.Services;
namespace Meili.Mac.Views.Controls;
public sealed class QuoteSparkline : Control
{
    public IntradaySparkline Data{get;set;}=IntradaySparkline.Empty;
    public IBrush LineBrush{get;set;}=Brushes.Gray;
    public override void Render(DrawingContext context)
    {
        base.Render(context);var w=Bounds.Width;var h=Bounds.Height;if(Data.Points.Count<2||w<=0||h<=0)return;
        if(Data.Baseline is { } b)context.DrawLine(new Pen(LineBrush,.6,new DashStyle([3,4],0)),new Point(0,b*h),new Point(w,b*h));
        SparkPoint? previous=null;foreach(var p in Data.Points){if(previous is { } old && !p.Break)context.DrawLine(new Pen(LineBrush,1.7),new Point(old.X*w,old.Y*h),new Point(p.X*w,p.Y*h));previous=p;}
        var last=Data.Points[^1];context.DrawEllipse(LineBrush,null,new Point(last.X*w,last.Y*h),2,2);
    }
}
