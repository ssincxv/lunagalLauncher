using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using Serilog;

namespace lunagalLauncher.Core
{
    /// <summary>
    /// GPU 类型枚举
    /// GPU type enumeration for identifying different GPU vendors
    /// </summary>
    public enum GpuType
    {
        /// <summary>NVIDIA GPU</summary>
        NVIDIA,
        /// <summary>AMD GPU</summary>
        AMD,
        /// <summary>Intel GPU</summary>
        Intel,
        /// <summary>未知 GPU / Unknown GPU</summary>
        Unknown
    }

    /// <summary>
    /// GPU 信息类
    /// GPU information class containing details about detected GPU
    /// </summary>
    public class GpuInfo
    {
        /// <summary>GPU 名称 / GPU name</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>GPU 类型 / GPU type</summary>
        public GpuType Type { get; set; }

        /// <summary>GPU 索引 / GPU index</summary>
        public int Index { get; set; }

        /// <summary>显存大小 (MB) / VRAM size in MB</summary>
        public long VramMB { get; set; }

        /// <summary>
        /// 获取显示名称
        /// Gets display name with index and VRAM info
        /// </summary>
        public string DisplayName => $"[{Index}] {Name} ({VramMB} MB)";
    }

    /// <summary>
    /// GPU 检测管理器
    /// GPU detection manager - detects and manages available GPUs
    /// </summary>
    public class GpuDetector
    {
        /// <summary>
        /// 检测到的所有 GPU 列表
        /// List of all detected GPUs
        /// </summary>
        public List<GpuInfo> AllGpus { get; private set; } = new List<GpuInfo>();

        /// <summary>
        /// NVIDIA GPU 列表
        /// List of NVIDIA GPUs
        /// </summary>
        public List<GpuInfo> NvidiaGpus => AllGpus.Where(g => g.Type == GpuType.NVIDIA).ToList();

        /// <summary>
        /// AMD GPU 列表
        /// List of AMD GPUs
        /// </summary>
        public List<GpuInfo> AmdGpus => AllGpus.Where(g => g.Type == GpuType.AMD).ToList();

        /// <summary>
        /// 构造函数 - 自动检测 GPU
        /// Constructor - automatically detects GPUs
        /// </summary>
        public GpuDetector()
        {
            DetectGpus();
        }

        /// <summary>
        /// 检测系统中的所有 GPU
        /// Detects all GPUs in the system
        /// </summary>
        public void DetectGpus()
        {
            Log.Information("开始检测系统 GPU...");
            AllGpus.Clear();

            // 检测 NVIDIA GPU
            // Detect NVIDIA GPUs using nvidia-smi
            DetectNvidiaGpus();

            // 检测 AMD GPU
            // Detect AMD GPUs using WMI
            DetectAmdGpus();

            Log.Information("GPU 检测完成，共检测到 {Count} 个 GPU", AllGpus.Count);
        }

        /// <summary>
        /// 检测 NVIDIA GPU
        /// Detects NVIDIA GPUs using nvidia-smi command
        /// </summary>
        private void DetectNvidiaGpus()
        {
            try
            {
                Log.Debug("尝试通过 nvidia-smi 检测 NVIDIA GPU...");

                // 设置环境变量
                // Set environment variable for consistent GPU ordering
                Environment.SetEnvironmentVariable("CUDA_DEVICE_ORDER", "PCI_BUS_ID");

                var startInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=index,name,memory.total --format=csv,noheader,nounits",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    Log.Warning("无法启动 nvidia-smi 进程");
                    return;
                }

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                {
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var parts = line.Split(',');
                        if (parts.Length >= 3)
                        {
                            var gpu = new GpuInfo
                            {
                                Index = int.Parse(parts[0].Trim()),
                                Name = parts[1].Trim(),
                                VramMB = long.Parse(parts[2].Trim()),
                                Type = GpuType.NVIDIA
                            };
                            AllGpus.Add(gpu);
                            Log.Information("检测到 NVIDIA GPU: {Name} (索引: {Index}, 显存: {Vram} MB)", 
                                gpu.Name, gpu.Index, gpu.VramMB);
                        }
                    }
                    Log.Information("通过 nvidia-smi 检测到 {Count} 个 NVIDIA GPU", NvidiaGpus.Count);
                }
                else
                {
                    Log.Debug("nvidia-smi 未返回有效数据");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "检测 NVIDIA GPU 时出错: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 检测 AMD GPU
        /// Detects AMD GPUs using WMI
        /// </summary>
        private void DetectAmdGpus()
        {
            try
            {
                Log.Debug("尝试通过 WMI 检测 AMD GPU...");

                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_VideoController");
                var amdGpusList = new List<GpuInfo>();
                int amdIndex = NvidiaGpus.Count; // AMD GPU 索引从 NVIDIA GPU 数量开始

                foreach (ManagementObject obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString() ?? string.Empty;
                    
                    // 检查是否为 AMD GPU
                    // Check if it's an AMD GPU
                    if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) || 
                        name.Contains("ATI", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                    {
                        // 获取显存大小
                        // Get VRAM size
                        long vramBytes = 0;
                        try
                        {
                            var adapterRam = obj["AdapterRAM"];
                            if (adapterRam != null)
                            {
                                vramBytes = Convert.ToInt64(adapterRam);
                            }
                        }
                        catch
                        {
                            vramBytes = 0;
                        }

                        var gpu = new GpuInfo
                        {
                            Name = name,
                            Type = GpuType.AMD,
                            Index = amdIndex++,
                            VramMB = vramBytes / (1024 * 1024) // 转换为 MB
                        };

                        amdGpusList.Add(gpu);
                        Log.Information("检测到 AMD GPU: {Name} (索引: {Index}, 显存: {Vram} MB)", 
                            gpu.Name, gpu.Index, gpu.VramMB);
                    }
                }

                // AMD GPU 反向添加（与 Python 代码保持一致）
                // Add AMD GPUs in reverse order (consistent with Python code)
                amdGpusList.Reverse();
                AllGpus.AddRange(amdGpusList);

                Log.Information("通过 WMI 检测到 {Count} 个 AMD GPU", AmdGpus.Count);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "检测 AMD GPU 时出错: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 设置 GPU 环境变量
        /// Sets GPU environment variables for CUDA/ROCm
        /// </summary>
        /// <param name="gpuIndex">GPU 索引 / GPU index</param>
        public void SetGpuEnvironment(int gpuIndex)
        {
            if (gpuIndex < 0 || gpuIndex >= AllGpus.Count)
            {
                Log.Warning("无效的 GPU 索引: {Index}", gpuIndex);
                return;
            }

            var gpu = AllGpus[gpuIndex];

            if (gpu.Type == GpuType.NVIDIA)
            {
                // 设置 CUDA 环境变量
                // Set CUDA environment variable
                Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", gpu.Index.ToString());
                Log.Information("设置 CUDA_VISIBLE_DEVICES = {Index}", gpu.Index);
            }
            else if (gpu.Type == GpuType.AMD)
            {
                // 设置 ROCm 环境变量
                // Set ROCm environment variable
                int hipIndex = gpu.Index - NvidiaGpus.Count;
                Environment.SetEnvironmentVariable("HIP_VISIBLE_DEVICES", hipIndex.ToString());
                Log.Information("设置 HIP_VISIBLE_DEVICES = {Index}", hipIndex);
            }
            else
            {
                Log.Warning("未知的 GPU 类型: {Type}", gpu.Type);
            }
        }

        /// <summary>
        /// 获取推荐的 GPU
        /// Gets recommended GPU (prioritizes NVIDIA, then AMD)
        /// </summary>
        /// <returns>推荐的 GPU 信息，如果没有则返回 null / Recommended GPU or null</returns>
        public GpuInfo? GetRecommendedGpu()
        {
            // 优先推荐 NVIDIA GPU
            // Prioritize NVIDIA GPUs
            if (NvidiaGpus.Count > 0)
            {
                return NvidiaGpus[0];
            }

            // 其次推荐 AMD GPU
            // Then AMD GPUs
            if (AmdGpus.Count > 0)
            {
                return AmdGpus[0];
            }

            return null;
        }
    }
}





