using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IwoBridge.Wpf.Views
{
    /// <summary>
    /// «Показывать, если есть»: true, непустая строка, ненулевое число, любой не-null объект → Visible, иначе
    /// Collapsed. Параметр <c>invert</c> — наоборот. Один конвертер на все случаи (KISS).
    /// </summary>
    public sealed class VisibleWhenConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            bool present;
            switch (value)
            {
                case null:
                    present = false;
                    break;
                case bool flag:
                    present = flag;
                    break;
                case string text:
                    present = text.Length > 0;
                    break;
                case int number:
                    present = number != 0;
                    break;
                default:
                    present = true;
                    break;
            }

            if (string.Equals(parameter as string, "invert", StringComparison.Ordinal))
            {
                present = !present;
            }

            return present ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>Логическое «не» в обе стороны — для пары переключателей, связанных с одним свойством.</summary>
    public sealed class NotConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return !(value is bool flag && flag);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return !(value is bool flag && flag);
        }
    }
}
