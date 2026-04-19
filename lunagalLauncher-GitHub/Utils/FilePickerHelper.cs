using System;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace lunagalLauncher.Utils
{
    /// <summary>
    /// 文件选择器辅助类
    /// File picker helper class using Win32 API
    /// </summary>
    public static class FilePickerHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class OpenFileName
        {
            public int structSize = 0;
            public IntPtr dlgOwner = IntPtr.Zero;
            public IntPtr instance = IntPtr.Zero;
            public string filter = null!;
            public string customFilter = null!;
            public int maxCustFilter = 0;
            public int filterIndex = 0;
            public string file = null!;
            public int maxFile = 0;
            public string fileTitle = null!;
            public int maxFileTitle = 0;
            public string initialDir = null!;
            public string title = null!;
            public int flags = 0;
            public short fileOffset = 0;
            public short fileExtension = 0;
            public string defExt = null!;
            public IntPtr custData = IntPtr.Zero;
            public IntPtr hook = IntPtr.Zero;
            public string templateName = null!;
            public IntPtr reservedPtr = IntPtr.Zero;
            public int reservedInt = 0;
            public int flagsEx = 0;
        }

        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

        /// <summary>
        /// 打开文件选择对话框（应用程序）
        /// Opens file picker dialog for applications
        /// </summary>
        /// <returns>选择的文件路径，如果取消则返回 null / Selected file path, or null if cancelled</returns>
        public static string? PickApplicationFile()
        {
            try
            {
                var ofn = new OpenFileName();
                ofn.structSize = Marshal.SizeOf(ofn);
                ofn.filter = "可执行文件\0*.exe;*.bat;*.cmd\0所有文件\0*.*\0\0";
                ofn.file = new string(new char[256]);
                ofn.maxFile = ofn.file.Length;
                ofn.fileTitle = new string(new char[64]);
                ofn.maxFileTitle = ofn.fileTitle.Length;
                ofn.initialDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                ofn.title = "选择应用程序";
                ofn.flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000200 | 0x00000008; // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_NOCHANGEDIR

                if (GetOpenFileName(ofn))
                {
                    return ofn.file;
                }

                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "打开文件选择对话框失败: {Message}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 打开文件选择对话框（图片）
        /// Opens file picker dialog for images
        /// </summary>
        /// <returns>选择的文件路径，如果取消则返回 null / Selected file path, or null if cancelled</returns>
        public static string? PickImageFile()
        {
            try
            {
                var ofn = new OpenFileName();
                ofn.structSize = Marshal.SizeOf(ofn);
                ofn.filter = "图片文件\0*.png;*.jpg;*.jpeg;*.bmp;*.ico\0所有文件\0*.*\0\0";
                ofn.file = new string(new char[256]);
                ofn.maxFile = ofn.file.Length;
                ofn.fileTitle = new string(new char[64]);
                ofn.maxFileTitle = ofn.fileTitle.Length;
                ofn.initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                ofn.title = "选择图标文件";
                ofn.flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000200 | 0x00000008; // OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_NOCHANGEDIR

                if (GetOpenFileName(ofn))
                {
                    return ofn.file;
                }

                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "打开文件选择对话框失败: {Message}", ex.Message);
                return null;
            }
        }
    }
}




