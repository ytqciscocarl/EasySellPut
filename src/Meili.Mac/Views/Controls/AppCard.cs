using Avalonia.Controls;

namespace Meili.Mac.Views.Controls;

/// <summary>Reusable card surface; its single style owns spacing, corners and theme roles.</summary>
public sealed class AppCard : Border
{
    protected override System.Type StyleKeyOverride=>typeof(Border);
    public AppCard()=>Classes.Add("Card");
}
