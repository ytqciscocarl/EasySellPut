using System.Windows;
using System.Windows.Controls;

namespace Meili.App.Views.Controls;

/// <summary>Reusable surface, optionally a settings row with an icon and header.</summary>
public sealed class AppCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty=DependencyProperty.Register(nameof(Header),typeof(object),typeof(AppCard));
    public static readonly DependencyProperty IconProperty=DependencyProperty.Register(nameof(Icon),typeof(object),typeof(AppCard));
    public object? Header{get=>GetValue(HeaderProperty);set=>SetValue(HeaderProperty,value);}
    public object? Icon{get=>GetValue(IconProperty);set=>SetValue(IconProperty,value);}
}
