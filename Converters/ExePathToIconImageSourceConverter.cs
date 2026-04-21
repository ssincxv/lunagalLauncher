using System;
using System.IO;
using lunagalLauncher.Utils;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace lunagalLauncher.Converters
{
    /// <summary>
    /// 将 exe 完整路径转为 <see cref="ImageSource"/>（关联图标），供过滤名单下拉每行绑定。
    /// </summary>
    public sealed class ExePathToIconImageSourceConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not string path || string.IsNullOrWhiteSpace(path))
                return null;

            var trimmed = path.Trim();
            if (!File.Exists(trimmed))
                return null;

            try
            {
                return IconExtractor.ExtractIconFromExe(trimmed);
            }
            catch
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotSupportedException();
    }
}
