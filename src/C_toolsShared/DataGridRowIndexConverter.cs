using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;

namespace C_toolsShared;

/// <summary>Converts a <see cref="DataGridRow"/> to a 1-based row number for index columns.</summary>
public sealed class DataGridRowIndexConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DataGridRow row)
            return (row.GetIndex() + 1).ToString(culture);
        return "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
