using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace HermesChat;

/// <summary>Инверсия булева значения в видимость: нужен для шаблона ComboBox,
/// где редактируемая и нередактируемая части показываются попеременно.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is bool boolean && boolean;
        return flag ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is Visibility visibility && visibility == Visibility.Collapsed;
    }
}