using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Serilog;

namespace lunagalLauncher.Utils
{
    /// <summary>
    /// 进程检测工具类
    /// Process detection utility class
    /// </summary>
    public static class ProcessDetector
    {
        /// <summary>
        /// 检查指定的可执行文件是否正在运行
        /// Checks if the specified executable is currently running
        /// </summary>
        /// <param name="exePath">可执行文件路径 / Executable file path</param>
        /// <returns>是否正在运行 / Whether it's running</returns>
        public static bool IsProcessRunning(string exePath)
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                return false;
            }

            try
            {
                // 获取文件名（不含路径）
                // Get file name without path
                string fileName = Path.GetFileName(exePath);
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(exePath);

                // 获取所有同名进程
                // Get all processes with the same name
                var processes = Process.GetProcessesByName(fileNameWithoutExt);

                if (processes.Length == 0)
                {
                    return false;
                }

                // 规范化目标路径
                // Normalize target path
                string normalizedTargetPath = Path.GetFullPath(exePath).ToLowerInvariant();

                // 检查是否有进程的路径匹配
                // Check if any process path matches
                foreach (var process in processes)
                {
                    try
                    {
                        // 获取进程的完整路径
                        // Get full path of the process
                        string processPath = process.MainModule?.FileName ?? string.Empty;
                        
                        if (!string.IsNullOrWhiteSpace(processPath))
                        {
                            string normalizedProcessPath = Path.GetFullPath(processPath).ToLowerInvariant();
                            
                            if (normalizedProcessPath == normalizedTargetPath)
                            {
                                return true;
                            }
                        }
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // 目标进程以更高权限运行（如管理员），无法读取模块路径；按名称匹配作为兜底
                        if (string.Equals(process.ProcessName, fileNameWithoutExt, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "无法访问进程信息: {ProcessName}", process.ProcessName);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "检测进程失败: {Path} - {Message}", exePath, ex.Message);
                return false;
            }
        }


    }
}



