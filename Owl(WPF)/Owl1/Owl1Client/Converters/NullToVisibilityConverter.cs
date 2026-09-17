using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Owl1Client.Converters;

/// <summary>
/// 값이 null이 아니면 Visible, null이면 Collapsed. ConverterParameter="Invert"를 주면 반대로 동작한다
/// (플레이스홀더처럼 값이 없을 때만 보여줘야 하는 경우에 사용).
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value != null;
        var invert = string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
        if (invert) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
