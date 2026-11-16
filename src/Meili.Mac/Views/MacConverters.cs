using Avalonia.Data.Converters;
using Meili.Data.Routing;

namespace Meili.Mac.Views;

public static class MacConverters
{
    /// <summary>E1: the health state is told by shape too, not by colour alone.</summary>
    public static readonly IValueConverter HealthGlyph = new FuncValueConverter<HealthState, string>(s => s switch
    {
        HealthState.Healthy => "CircleCheck",
        HealthState.Degraded => "TriangleAlert",
        HealthState.Down => "CircleX",
        _ => "Help",
    });
}
