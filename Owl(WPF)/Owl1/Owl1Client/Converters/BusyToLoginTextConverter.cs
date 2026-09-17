using System.Globalization;
using System.Windows.Data;

namespace Owl1Client.Converters;

public class BusyToLoginTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? "로그인 중..." : "로그인";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
