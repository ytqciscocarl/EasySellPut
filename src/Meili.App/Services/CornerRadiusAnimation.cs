using System.Windows;
using System.Windows.Media.Animation;

namespace Meili.App.Services;

/// <summary>Small chrome-only animation; never rasterizes or translates a native WebView.</summary>
internal sealed class CornerRadiusAnimation : AnimationTimeline
{
    private readonly CornerRadius from, to;
    public CornerRadiusAnimation(CornerRadius from, CornerRadius to, Duration duration) { this.from = from; this.to = to; Duration = duration; }
    public override Type TargetPropertyType => typeof(CornerRadius);
    protected override Freezable CreateInstanceCore() => new CornerRadiusAnimation(from, to, Duration);
    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock clock)
    {
        var t = clock.CurrentProgress ?? 0;
        t = 1 - Math.Pow(1 - t, 3);
        double Lerp(double a, double b) => a + (b - a) * t;
        return new CornerRadius(Lerp(from.TopLeft, to.TopLeft), Lerp(from.TopRight, to.TopRight), Lerp(from.BottomRight, to.BottomRight), Lerp(from.BottomLeft, to.BottomLeft));
    }
}
