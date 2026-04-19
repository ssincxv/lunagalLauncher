using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

namespace lunagalLauncher.Utils
{
    /// <summary>
    /// 图标提取工具类
    /// Icon extraction utility class
    /// </summary>
    public static class IconExtractor
    {
        // Windows API 导入
        // Windows API imports
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// 从可执行文件提取图标并转换为 BitmapImage
        /// Extracts icon from executable and converts to BitmapImage
        /// </summary>
        /// <param name="exePath">可执行文件路径 / Executable file path</param>
        /// <returns>BitmapImage 或 null / BitmapImage or null</returns>
        public static BitmapImage? ExtractIconFromExe(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                Log.Warning("无法提取图标：文件不存在 - {Path}", exePath);
                return null;
            }

            IntPtr hIcon = IntPtr.Zero;
            try
            {
                // 提取图标
                // Extract icon
                hIcon = ExtractIcon(IntPtr.Zero, exePath, 0);

                if (hIcon == IntPtr.Zero || hIcon == new IntPtr(1))
                {
                    Log.Warning("无法从文件提取图标: {Path}", exePath);
                    return null;
                }

                // 转换为 Bitmap
                // Convert to Bitmap
                using (Icon icon = Icon.FromHandle(hIcon))
                using (Bitmap bitmap = icon.ToBitmap())
                {
                    // 转换为 BitmapImage
                    // Convert to BitmapImage
                    return ConvertBitmapToBitmapImage(bitmap);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "提取图标失败: {Path} - {Message}", exePath, ex.Message);
                return null;
            }
            finally
            {
                // 释放图标句柄
                // Release icon handle
                if (hIcon != IntPtr.Zero && hIcon != new IntPtr(1))
                {
                    DestroyIcon(hIcon);
                }
            }
        }

        /// <summary>
        /// 从图片文件加载 BitmapImage
        /// Loads BitmapImage from image file
        /// </summary>
        /// <param name="imagePath">图片文件路径 / Image file path</param>
        /// <returns>BitmapImage 或 null / BitmapImage or null</returns>
        public static BitmapImage? LoadImageFromFile(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                Log.Warning("无法加载图片：文件不存在 - {Path}", imagePath);
                return null;
            }

            try
            {
                var bitmapImage = new BitmapImage();
                using (var stream = File.OpenRead(imagePath))
                {
                    var memoryStream = new MemoryStream();
                    stream.CopyTo(memoryStream);
                    memoryStream.Position = 0;

                    bitmapImage.SetSource(memoryStream.AsRandomAccessStream());
                }
                return bitmapImage;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载图片失败: {Path} - {Message}", imagePath, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 将 Bitmap 转换为 BitmapImage
        /// Converts Bitmap to BitmapImage
        /// </summary>
        /// <param name="bitmap">Bitmap 对象 / Bitmap object</param>
        /// <returns>BitmapImage / BitmapImage</returns>
        private static BitmapImage ConvertBitmapToBitmapImage(Bitmap bitmap)
        {
            using (var memoryStream = new MemoryStream())
            {
                // 保存为 PNG 格式
                // Save as PNG format
                bitmap.Save(memoryStream, ImageFormat.Png);
                memoryStream.Position = 0;

                var bitmapImage = new BitmapImage();
                bitmapImage.SetSource(memoryStream.AsRandomAccessStream());
                return bitmapImage;
            }
        }

        /// <summary>
        /// 检查文件是否为图片格式
        /// Checks if file is an image format
        /// </summary>
        /// <param name="filePath">文件路径 / File path</param>
        /// <returns>是否为图片 / Whether it's an image</returns>
        public static bool IsImageFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            return extension == ".png" || extension == ".jpg" || extension == ".jpeg" ||
                   extension == ".bmp" || extension == ".gif" || extension == ".ico";
        }
    }
}




