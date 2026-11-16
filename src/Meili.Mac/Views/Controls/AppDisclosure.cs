using Avalonia;
using Avalonia.Controls;

namespace Meili.Mac.Views.Controls;

/// <summary>Keyboard-accessible progressive disclosure that preserves its content and field bindings.</summary>
public sealed class AppDisclosure : Expander
{
    public AppDisclosure()=>Classes.Add("Disclosure");
    protected override Type StyleKeyOverride=>typeof(Expander);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if(e.Property==IsExpandedProperty && IsExpanded)AppMotion.Enter(Content as Control);
    }
}
