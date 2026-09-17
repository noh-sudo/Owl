using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Owl1Client.Converters;

/// <summary>연결 상태(true=정상/연결됨, false=이상/연결끊김)를 초록/빨강 LED 색상으로 변환한다.</summary>
public class BoolToStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ok = value is bool b && b;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) ok = !ok;
        var key = ok ? "SuccessGreenBrush" : "DangerRedBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
