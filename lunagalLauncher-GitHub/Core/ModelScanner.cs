using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace lunagalLauncher.Core
{
    /// <summary>
    /// 模型排序方式枚举
    /// Model sorting method enumeration
    /// </summary>
    public enum ModelSortMethod
    {
        /// <summary>按修改时间排序 / Sort by modification time</summary>
        ModifiedTime,
        /// <summary>按文件名排序 / Sort by file name</summary>
        FileName,
        /// <summary>按文件大小排序 / Sort by file size</summary>
        FileSize
    }

    /// <summary>
    /// 模型文件信息类
    /// Model file information class
    /// </summary>
    public class ModelInfo
    {
        /// <summary>模型文件完整路径 / Full path to model file</summary>
        public string FullPath { get; set; } = string.Empty;

        /// <summary>模型文件名 / Model file name</summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>文件大小 (字节) / File size in bytes</summary>
        public long FileSizeBytes { get; set; }

        /// <summary>文件大小 (可读格式) / File size in human-readable format</summary>
        public string FileSizeFormatted => FormatFileSize(FileSizeBytes);

        /// <summary>最后修改时间 / Last modified time</summary>
        public DateTime LastModified { get; set; }

        /// <summary>
        /// 格式化文件大小
        /// Formats file size to human-readable string
        /// </summary>
        private static string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        /// <summary>
        /// 获取显示名称
        /// Gets display name with file size
        /// </summary>
        public string DisplayName => $"{FileName} ({FileSizeFormatted})";
    }

    /// <summary>
    /// GGUF 模型扫描器
    /// GGUF model scanner - scans directories for .gguf model files
    /// </summary>
    public class ModelScanner
    {
        /// <summary>
        /// 搜索路径列表
        /// List of search paths
        /// </summary>
        public List<string> SearchPaths { get; set; } = new List<string>();

        /// <summary>
        /// 构造函数
        /// Constructor
        /// </summary>
        public ModelScanner()
        {
            // 默认添加当前目录
            // Add current directory by default
            SearchPaths.Add(AppDomain.CurrentDomain.BaseDirectory);
        }

        /// <summary>
        /// 扫描所有搜索路径中的 GGUF 模型
        /// Scans all search paths for GGUF models
        /// </summary>
        /// <param name="sortMethod">排序方式 / Sort method</param>
        /// <returns>模型信息列表 / List of model information</returns>
        public List<ModelInfo> ScanModels(ModelSortMethod sortMethod = ModelSortMethod.ModifiedTime)
        {
            Log.Information("开始扫描 GGUF 模型文件...");
            var models = new List<ModelInfo>();

            foreach (var path in SearchPaths)
            {
                if (!Directory.Exists(path))
                {
                    Log.Debug("路径不存在，跳过: {Path}", path);
                    continue;
                }

                Log.Debug("正在扫描路径: {Path}", path);

                try
                {
                    // 递归搜索所有 .gguf 文件
                    // Recursively search for all .gguf files
                    var files = Directory.GetFiles(path, "*.gguf", SearchOption.AllDirectories);

                    foreach (var file in files)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(file);
                            var model = new ModelInfo
                            {
                                FullPath = file,
                                FileName = fileInfo.Name,
                                FileSizeBytes = fileInfo.Length,
                                LastModified = fileInfo.LastWriteTime
                            };

                            models.Add(model);
                            Log.Debug("找到模型文件: {FileName} ({Size})", model.FileName, model.FileSizeFormatted);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "读取文件信息失败: {File}", file);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "扫描路径失败: {Path}", path);
                }
            }

            // 排序模型列表
            // Sort model list
            models = SortModels(models, sortMethod);

            Log.Information("模型扫描完成，共找到 {Count} 个 GGUF 文件", models.Count);
            return models;
        }

        /// <summary>
        /// 排序模型列表
        /// Sorts model list according to specified method
        /// </summary>
        /// <param name="models">模型列表 / Model list</param>
        /// <param name="sortMethod">排序方式 / Sort method</param>
        /// <returns>排序后的模型列表 / Sorted model list</returns>
        private List<ModelInfo> SortModels(List<ModelInfo> models, ModelSortMethod sortMethod)
        {
            return sortMethod switch
            {
                ModelSortMethod.ModifiedTime => models.OrderByDescending(m => m.LastModified).ToList(),
                ModelSortMethod.FileName => models.OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase).ToList(),
                ModelSortMethod.FileSize => models.OrderByDescending(m => m.FileSizeBytes).ToList(),
                _ => models
            };
        }

        /// <summary>
        /// 添加搜索路径
        /// Adds a search path
        /// </summary>
        /// <param name="path">路径 / Path</param>
        public void AddSearchPath(string path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !SearchPaths.Contains(path))
            {
                SearchPaths.Add(path);
                Log.Information("添加模型搜索路径: {Path}", path);
            }
        }

        /// <summary>
        /// 移除搜索路径
        /// Removes a search path
        /// </summary>
        /// <param name="path">路径 / Path</param>
        public void RemoveSearchPath(string path)
        {
            if (SearchPaths.Contains(path))
            {
                SearchPaths.Remove(path);
                Log.Information("移除模型搜索路径: {Path}", path);
            }
        }

        /// <summary>
        /// 清空搜索路径
        /// Clears all search paths
        /// </summary>
        public void ClearSearchPaths()
        {
            SearchPaths.Clear();
            Log.Information("已清空所有模型搜索路径");
        }
    }
}





