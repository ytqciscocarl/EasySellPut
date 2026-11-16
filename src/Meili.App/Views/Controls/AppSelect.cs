using System.Windows;
using System.Windows.Controls;

namespace Meili.App.Views.Controls;

/// <summary>Reusable select using the shared style while retaining WPF keyboard, popup and IME behavior.</summary>
public sealed class AppSelect : ComboBox
{
    public AppSelect()=>SetResourceReference(StyleProperty,typeof(ComboBox));
}
