using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FrogLauncher.Core;

/// <summary>bool → Visibility; параметр "inv" инвертирует.</summary>
public sealed class BoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value is bool v && v;
        if ((p as string) == "inv") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Не null / непустая строка → Visible.</summary>
public sealed class NotNullToVisConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool has = value is string s ? !string.IsNullOrEmpty(s) : value != null;
        if ((p as string) == "inv") has = !has;
        return has ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>int == параметр → bool (для подсветки вкладок) / Visibility.</summary>
public sealed class IntEqConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool eq = value is int i && int.TryParse(p as string, out var n) && i == n;
        return t == typeof(Visibility) ? (eq ? Visibility.Visible : Visibility.Collapsed) : eq;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
