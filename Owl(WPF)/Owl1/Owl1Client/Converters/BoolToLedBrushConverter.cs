using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Owl1Client.Converters;

/// <summary>
/// bool 값을 LED 색상 Brush로 변환한다.
/// ConverterParameter로 "켜짐" 상태일 때 사용할 리소스 키를 지정하고, 꺼짐 상태는 항상 LedOffBrush를 사용한다.
/// 예) Fill="{Binding IsVideoActive, Converter={StaticResource BoolToLedBrushConverter}, ConverterParameter=SuccessGreenBrush}"
/// </summary>
public class BoolToLedBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isOn = value is bool b && b;
        var onKey = parameter as string ?? "SuccessGreenBrush";
        var key = isOn ? onKey : "LedOffBrush";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
