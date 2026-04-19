using System;
using System.Collections.Generic;

namespace lunagalLauncher.Data
{
    /// <summary>
    /// 应用程序配置项
    /// Represents a single application configuration entry
    /// </summary>
    public class AppConfig
    {
        /// <summary>
        /// 应用程序唯一标识符
        /// Unique identifier for the application
        /// </summary>
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// 应用程序显示名称
        /// Display name of the application
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 应用程序可执行文件路径
        /// Path to the application executable
        /// </summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>
        /// 是否启用该应用程序
        /// Whether this application is enabled for launch
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 是否为内置应用程序（不可删除）
        /// Whether this is a built-in application (cannot be deleted)
        /// </summary>
        public bool IsBuiltIn { get; set; } = false;

        /// <summary>
        /// 自定义图标路径
        /// Custom icon path (if set, overrides auto-extracted icon)
        /// </summary>
        public string CustomIconPath { get; set; } = string.Empty;
    }

    /// <summary>
    /// llama 服务配置
    /// Configuration for llama service
    /// </summary>
    public class LlamaServiceConfig
    {
        /// <summary>
        /// 是否启用 llama 服务
        /// Whether llama service is enabled
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 是否为内置服务（不可删除）
        /// Whether this is a built-in service (cannot be deleted)
        /// </summary>
        public bool IsBuiltIn { get; set; } = true;

        /// <summary>
        /// llama-server 可执行文件路径
        /// Path to llama-server executable
        /// </summary>
        public string ServicePath { get; set; } = string.Empty;

        /// <summary>
        /// 模型文件路径
        /// Path to GGUF model file
        /// </summary>
        public string ModelPath { get; set; } = string.Empty;

        /// <summary>
        /// 模型路径历史记录（最近使用的模型）
        /// Model path history (recently used models)
        /// </summary>
        public List<string> ModelPathHistory { get; set; } = new List<string>();

        /// <summary>
        /// 主机地址 (--host)
        /// Host address (--host)
        /// </summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>
        /// 主机地址历史记录（最近使用的主机地址）
        /// Host address history (recently used hosts)
        /// </summary>
        public List<string> HostHistory { get; set; } = new List<string>();

        /// <summary>
        /// 端口号 (--port)
        /// Port number (--port)
        /// </summary>
        public int Port { get; set; } = 8080;

        /// <summary>
        /// GPU 层数 (-ngl)
        /// Number of GPU layers (-ngl)
        /// </summary>
        public int GpuLayers { get; set; } = 99;

        /// <summary>
        /// 上下文长度 (-c)
        /// Context length (-c)
        /// </summary>
        public int ContextLength { get; set; } = 2048;

        /// <summary>
        /// 并行工作线程数 (-np)
        /// Number of parallel threads (-np)
        /// </summary>
        public int ParallelThreads { get; set; } = 1;

        /// <summary>
        /// 是否启用 Flash Attention (-fa)
        /// Enable Flash Attention (-fa)
        /// </summary>
        public bool FlashAttention { get; set; } = true;

        /// <summary>
        /// 是否禁用内存映射 (--no-mmap)
        /// Disable memory mapping (--no-mmap)
        /// </summary>
        public bool NoMmap { get; set; } = true;

        /// <summary>
        /// 日志格式 (--log-format)
        /// Log format (--log-format): none, text, json
        /// </summary>
        public string LogFormat { get; set; } = "none";

        /// <summary>
        /// 是否启用单 GPU 模式
        /// Enable single GPU mode
        /// </summary>
        public bool SingleGpuMode { get; set; } = true;

        /// <summary>
        /// 选择的 GPU 索引
        /// Selected GPU index
        /// </summary>
        public int SelectedGpuIndex { get; set; } = 0;

        /// <summary>
        /// 选择的 GPU 名称（用于恢复配置）
        /// Selected GPU name (for configuration restoration)
        /// </summary>
        public string SelectedGpuName { get; set; } = string.Empty;

        /// <summary>
        /// 选择的预设名称（用于恢复配置）
        /// Selected preset name (for configuration restoration)
        /// </summary>
        public string SelectedPreset { get; set; } = string.Empty;

        /// <summary>
        /// 手动指定的 GPU 索引（覆盖自动检测）
        /// Manually specified GPU index (overrides auto-detection)
        /// </summary>
        public string ManualGpuIndex { get; set; } = string.Empty;

        /// <summary>
        /// 自定义追加命令
        /// Custom command to append
        /// </summary>
        public string CustomCommandAppend { get; set; } = string.Empty;

        /// <summary>
        /// 自定义完整命令（覆盖所有 UI 设置）
        /// Custom full command (overrides all UI settings)
        /// </summary>
        public string CustomCommand { get; set; } = string.Empty;

        /// <summary>
        /// 模型搜索路径列表
        /// List of model search paths
        /// </summary>
        public List<string> ModelSearchPaths { get; set; } = new List<string>();

        /// <summary>
        /// 模型排序方式
        /// Model sorting method: ModifiedTime, FileName, FileSize
        /// </summary>
        public string ModelSortMethod { get; set; } = "ModifiedTime";

        /// <summary>
        /// 配置预设列表
        /// List of configuration presets
        /// </summary>
        public List<LlamaServicePreset> Presets { get; set; } = new List<LlamaServicePreset>();
    }

    /// <summary>
    /// llama 服务配置预设
    /// llama service configuration preset
    /// </summary>
    public class LlamaServicePreset
    {
        /// <summary>
        /// 预设名称
        /// Preset name
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 模型路径
        /// Model path
        /// </summary>
        public string ModelPath { get; set; } = string.Empty;

        /// <summary>
        /// 主机地址
        /// Host address
        /// </summary>
        public string Host { get; set; } = "127.0.0.1";

        /// <summary>
        /// 端口号
        /// Port number
        /// </summary>
        public int Port { get; set; } = 8080;

        /// <summary>
        /// GPU 层数
        /// GPU layers
        /// </summary>
        public int GpuLayers { get; set; } = 99;

        /// <summary>
        /// 上下文长度
        /// Context length
        /// </summary>
        public int ContextLength { get; set; } = 2048;

        /// <summary>
        /// 并行线程数
        /// Parallel threads
        /// </summary>
        public int ParallelThreads { get; set; } = 1;

        /// <summary>
        /// Flash Attention
        /// </summary>
        public bool FlashAttention { get; set; } = true;

        /// <summary>
        /// No Mmap
        /// </summary>
        public bool NoMmap { get; set; } = true;

        /// <summary>
        /// 日志格式
        /// Log format
        /// </summary>
        public string LogFormat { get; set; } = "none";

        /// <summary>
        /// 单 GPU 模式
        /// Single GPU mode
        /// </summary>
        public bool SingleGpuMode { get; set; } = true;

        /// <summary>
        /// GPU 索引
        /// GPU index
        /// </summary>
        public int GpuIndex { get; set; } = 0;

        /// <summary>
        /// 手动 GPU 索引
        /// Manual GPU index
        /// </summary>
        public string ManualGpuIndex { get; set; } = string.Empty;

        /// <summary>
        /// 自定义追加命令
        /// Custom append command
        /// </summary>
        public string CustomCommandAppend { get; set; } = string.Empty;

        /// <summary>
        /// 自定义完整命令
        /// Custom full command
        /// </summary>
        public string CustomCommand { get; set; } = string.Empty;
    }

    /// <summary>
    /// 启动设置配置
    /// Launch settings configuration
    /// </summary>
    public class LaunchSettings
    {
        /// <summary>
        /// 是否在程序启动时自动启动所有应用
        /// Whether to auto-launch all apps on program startup
        /// </summary>
        public bool AutoLaunchOnStartup { get; set; } = false;

        /// <summary>
        /// 启动模式：normal（正常）或 minimized（最小化）
        /// Launch mode: "normal" or "minimized"
        /// </summary>
        public string LaunchMode { get; set; } = "normal";

        /// <summary>
        /// 退出时是否关闭所有已启动的应用
        /// Whether to close all launched apps on exit
        /// </summary>
        public bool CloseAppsOnExit { get; set; } = false;

        /// <summary>
        /// 应用程序列表
        /// List of applications to manage
        /// </summary>
        public List<AppConfig> Apps { get; set; } = new List<AppConfig>();

        /// <summary>
        /// llama 服务配置
        /// llama service configuration
        /// </summary>
        public LlamaServiceConfig LlamaService { get; set; } = new LlamaServiceConfig();
    }

    /// <summary>
    /// 鼠标按键配置基类
    /// Base class for mouse button configuration
    /// </summary>
    public class MouseButtonConfig
    {
        /// <summary>
        /// 是否启用该按键映射
        /// Whether this button mapping is enabled
        /// </summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// 鼠标左键配置
    /// Left mouse button configuration
    /// </summary>
    public class LeftClickConfig : MouseButtonConfig
    {
        /// <summary>
        /// 短按阈值（毫秒）
        /// Short press threshold in milliseconds
        /// </summary>
        public int ShortPressThreshold { get; set; } = 200;

        /// <summary>
        /// 自动连点间隔（毫秒）
        /// Auto-click interval in milliseconds
        /// </summary>
        public int AutoClickInterval { get; set; } = 100;
    }

    /// <summary>
    /// 鼠标右键配置
    /// Right mouse button configuration
    /// </summary>
    public class RightClickConfig : MouseButtonConfig
    {
        /// <summary>
        /// 长按阈值（毫秒）
        /// Long press threshold in milliseconds
        /// </summary>
        public int LongPressThreshold { get; set; } = 500;
    }

    /// <summary>
    /// 鼠标中键配置
    /// Middle mouse button configuration
    /// </summary>
    public class MiddleClickConfig : MouseButtonConfig
    {
        /// <summary>
        /// 长按阈值（毫秒）
        /// Long press threshold in milliseconds
        /// </summary>
        public int LongPressThreshold { get; set; } = 500;

        /// <summary>
        /// 自定义快捷键（例如："Shift+Z"）
        /// Custom hotkey (e.g., "Shift+Z")
        /// </summary>
        public string CustomHotkey { get; set; } = "Shift+Z";
    }

    /// <summary>
    /// 鼠标映射配置
    /// Mouse mapping configuration
    /// </summary>
    public class MouseMappingConfig
    {
        /// <summary>配置架构版本（2 = 规则列表）</summary>
        public string SchemaVersion { get; set; } = "2";

        /// <summary>总开关</summary>
        public bool GlobalEnabled { get; set; } = true;

        /// <summary>保存后从磁盘热重载（FileSystemWatcher）</summary>
        public bool HotReload { get; set; } = true;

        /// <summary>规则列表（同一按键可多规则）</summary>
        public List<MouseMappingRule> Rules { get; set; } = new List<MouseMappingRule>();

        /// <summary>
        /// 全局进程过滤：在引擎中对所有规则先施加；与单条 <see cref="MouseMappingRule.RestrictToProcessList"/> 叠加（须同时满足）。
        /// </summary>
        public bool GlobalRestrictToProcessList { get; set; }

        /// <summary>全局过滤名单（exe 名或完整路径）</summary>
        public List<string> GlobalProcessFilter { get; set; } = new List<string>();

        /// <summary>全局过滤模式（白名单 / 黑名单）</summary>
        public MouseContextWhitelistMode GlobalContextMode { get; set; } = MouseContextWhitelistMode.Exclude;

        /// <summary>全局：光标在任务栏区域时，不应用任何鼠标映射规则（原单条规则项已上移至此）。</summary>
        public bool GlobalDisableOnTaskbar { get; set; }

        /// <summary>全局：光标在屏幕边缘（约 4px）时，不应用任何鼠标映射规则。</summary>
        public bool GlobalDisableOnScreenEdges { get; set; }

        /// <summary>
        /// 左键配置（旧版兼容，迁移后仍保留于 JSON）
        /// Left click configuration (legacy)
        /// </summary>
        public LeftClickConfig LeftClick { get; set; } = new LeftClickConfig();

        /// <summary>
        /// 右键配置
        /// Right click configuration (legacy)
        /// </summary>
        public RightClickConfig RightClick { get; set; } = new RightClickConfig();

        /// <summary>
        /// 中键配置
        /// Middle click configuration (legacy)
        /// </summary>
        public MiddleClickConfig MiddleClick { get; set; } = new MiddleClickConfig();

        /// <summary>导出设置时最近使用的路径（下拉历史）</summary>
        public List<string> ExportPathHistory { get; set; } = new List<string>();

        /// <summary>导入设置时最近使用的路径（下拉历史）</summary>
        public List<string> ImportPathHistory { get; set; } = new List<string>();
    }

    /// <summary>
    /// 窗口设置配置
    /// Window settings configuration
    /// </summary>
    public class WindowSettings
    {
        /// <summary>
        /// 窗口宽度
        /// Window width
        /// </summary>
        public int Width { get; set; } = 1280;

        /// <summary>
        /// 窗口高度
        /// Window height
        /// </summary>
        public int Height { get; set; } = 800;

        /// <summary>
        /// 窗口 X 坐标
        /// Window X position
        /// </summary>
        public int X { get; set; } = -1;

        /// <summary>
        /// 窗口 Y 坐标
        /// Window Y position
        /// </summary>
        public int Y { get; set; } = -1;

        /// <summary>
        /// 是否最大化
        /// Whether window is maximized
        /// </summary>
        public bool IsMaximized { get; set; } = false;
    }

    /// <summary>
    /// 应用程序根配置
    /// Root application configuration
    /// </summary>
    public class RootConfig
    {
        /// <summary>
        /// 配置文件版本
        /// Configuration file version
        /// </summary>
        public string Version { get; set; } = "1.0";

        /// <summary>
        /// 启动设置
        /// Launch settings
        /// </summary>
        public LaunchSettings LaunchSettings { get; set; } = new LaunchSettings();

        /// <summary>
        /// 鼠标映射配置
        /// Mouse mapping configuration
        /// </summary>
        public MouseMappingConfig MouseMapping { get; set; } = new MouseMappingConfig();

        /// <summary>
        /// 窗口设置
        /// Window settings
        /// </summary>
        public WindowSettings WindowSettings { get; set; } = new WindowSettings();
    }
}
