using System.Windows;
using System.Windows.Controls;

namespace Meili.App.Views.Controls;

/// <summary>Keyboard-accessible progressive disclosure that preserves its content and field bindings.</summary>
public sealed class AppDisclosure : Expander
{
    public AppDisclosure()
    {
        SetResourceReference(StyleProperty,typeof(Expander));
        Expanded+=(_,_)=>ComponentMotion.Enter(Content as UIElement);
    }
}
