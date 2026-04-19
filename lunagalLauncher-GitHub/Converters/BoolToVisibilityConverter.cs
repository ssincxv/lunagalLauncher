using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace lunagalLauncher.Converters
{
    /// <summary>
    /// 布尔值到可见性转换器
    /// Boolean to Visibility converter
    /// </summary>
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool boolValue)
            {
                return boolValue ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is Visibility visibility)
            {
                return visibility == Visibility.Visible;
            }
            return false;
        }
    }

}



