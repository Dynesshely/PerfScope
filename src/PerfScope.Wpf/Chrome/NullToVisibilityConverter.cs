using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PerfScope.App.Chrome
{
    /// <summary>null → Collapsed，非 null → Visible；ConverterParameter=invert 时反过来。</summary>
    public sealed class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool hasValue = value is not null;
            bool invert = parameter is string text && string.Equals(text, "invert", StringComparison.OrdinalIgnoreCase);
            if (invert)
            {
                hasValue = !hasValue;
            }

            return hasValue ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
