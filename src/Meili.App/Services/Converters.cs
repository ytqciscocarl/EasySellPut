using System.Windows;
using System.Globalization;
using System.Windows.Data;

namespace Meili.App.Services;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
public sealed class BoolToVisConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
/// <summary>Visible when the bound string is non-empty.</summary>
public sealed class NonEmptyToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>E1: icon per health state.</summary>
public sealed class HealthIconConverter : IValueConverter
{
    public static string Icon(Meili.Data.Routing.HealthState state) => state switch
    {
        Meili.Data.Routing.HealthState.Healthy => "CircleCheck",
        Meili.Data.Routing.HealthState.Degraded => "TriangleAlert",
        Meili.Data.Routing.HealthState.Down => "CircleX",
        _ => "Help",
    };

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        Icon((Meili.Data.Routing.HealthState)value);

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
