using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using lunagalLauncher.Core;
using lunagalLauncher.Data;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.Storage.Pickers;
using lunagalLauncher.Utils;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using System.Numerics;

namespace lunagalLauncher.Views
{
    /// <summary>
    /// Win32 文件对话框辅助类
    /// Win32 File Dialog Helper - provides fallback when WinUI FileOpenPicker fails
    /// </summary>
    internal static class Win32FileDialog
    {
        /// <summary>
        /// OPENFILENAME 结构体
        /// Structure for GetOpenFileName API
    /// </summary>
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAME
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public string lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        /// <summary>
        /// Win32 GetOpenFileName API
        /// Native Windows file open dialog
        /// </summary>
        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetOpenFileName(ref OPENFILENAME ofn);

        // 常量定义 / Constants
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_EXPLORER = 0x00080000;
        private const int OFN_HIDEREADONLY = 0x00000004;

        /// <summary>
        /// 显示文件选择对话框
        /// Shows file open dialog using Win32 API
        /// </summary>
        /// <param name="hwnd">父窗口句柄 / Parent window handle</param>
        /// <param name="filter">文件过滤器 / File filter (e.g., "Executable Files|*.exe")</param>
        /// <param name="title">对话框标题 / Dialog title</param>
        /// <returns>选择的文件路径，如果取消则返回 null / Selected file path or null if cancelled</returns>
        public static string? ShowOpenFileDialog(IntPtr hwnd, string filter, string title)
        {
            try
            {
                // 转换过滤器格式：从 "Executable Files|*.exe" 到 "Executable Files\0*.exe\0\0"
                // Convert filter format from "Name|Pattern" to Win32 format
                string win32Filter = filter.Replace('|', '\0') + "\0";

                var ofn = new OPENFILENAME
                {
                    lStructSize = Marshal.SizeOf(typeof(OPENFILENAME)),
                    hwndOwner = hwnd,
                    lpstrFilter = win32Filter,
                    nFilterIndex = 1,
                    lpstrFile = new string('\0', 260), // MAX_PATH
                    nMaxFile = 260,
                    lpstrFileTitle = new string('\0', 260),
                    nMaxFileTitle = 260,
                    lpstrTitle = title,
                    Flags = OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_EXPLORER | OFN_HIDEREADONLY
                };

                // 调用 Win32 API
                // Call Win32 API
                if (GetOpenFileName(ref ofn))
                {
                    // 返回选择的文件路径
                    // Return selected file path
                    return ofn.lpstrFile.Split('\0')[0];
                }

                return null; // 用户取消 / User cancelled
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Win32 文件对话框失败: {Message}", ex.Message);
                return null;
            }
        }

        private const int OFN_OVERWRITEPROMPT = 0x00000002;

        /// <summary>
        /// Win32 GetSaveFileName API
        /// </summary>
        [DllImport("comdlg32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetSaveFileName(ref OPENFILENAME ofn);

        /// <summary>
        /// 另存为对话框（WinUI FileSavePicker 失败时的备选）
        /// </summary>
        public static string? ShowSaveFileDialog(IntPtr hwnd, string filter, string title, string suggestedFileName = "mouse-mapping.json")
        {
            try
            {
                string win32Filter = filter.Replace('|', '\0') + "\0";
                var fileBuf = suggestedFileName + new string('\0', System.Math.Max(0, 260 - suggestedFileName.Length));
                var ofn = new OPENFILENAME
                {
                    lStructSize = Marshal.SizeOf(typeof(OPENFILENAME)),
                    hwndOwner = hwnd,
                    lpstrFilter = win32Filter,
                    nFilterIndex = 1,
                    lpstrFile = fileBuf,
                    nMaxFile = 260,
                    lpstrFileTitle = new string('\0', 260),
                    nMaxFileTitle = 260,
                    lpstrTitle = title,
                    lpstrDefExt = "json",
                    Flags = OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST | OFN_EXPLORER
                };

                if (GetSaveFileName(ref ofn))
                    return ofn.lpstrFile.Split('\0')[0];
                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Win32 另存为对话框失败: {Message}", ex.Message);
                return null;
            }
        }
    }

    /// <summary>
    /// llama 服务管理页面
    /// llama service management page with full parameter configuration
    /// </summary>
    public sealed partial class LlamaServicePage : Page
    {
        /// <summary>
        /// llama 服务管理器
        /// llama service manager
        /// </summary>
        private readonly LlamaServiceManager _serviceManager;

        /// <summary>
        /// GPU 检测器
        /// GPU detector
        /// </summary>
        private readonly GpuDetector _gpuDetector;

        /// <summary>
        /// 模型扫描器
        /// Model scanner
        /// </summary>
        private readonly ModelScanner _modelScanner;

        /// <summary>
        /// 页面是否已卸载
        /// Whether the page has been unloaded
        /// </summary>
        private bool _isUnloaded = false;

        /// <summary>
        /// 预设加载中标志 —— 用于屏蔽 LoadPresetByName 过程中由 TextBox/Slider
        /// 属性变更触发的 SaveConfiguration() 调用，防止它们把
        /// config.SelectedPreset 回写为空字符串、或覆盖掉预设刚刚写进
        /// AppConfig 的字段。
        /// Guards against SaveConfiguration() being invoked during LoadPresetByName.
        /// While this flag is set, the various TextChanged/ValueChanged handlers
        /// (wired in XAML) must treat the change as a programmatic preset
        /// application, not a user edit — so they don't clobber SelectedPreset.
        /// 
        /// 注意：该标志不与 _isInitializing 共用，因为后者贯穿整个页面启动期，
        /// 语义不同。
        /// Intentionally NOT reused from _isInitializing: that flag guards the
        /// whole page-startup window, which has different semantics.
        /// </summary>
        private bool _isApplyingPreset = false;

        /// <summary>
        /// 是否正在初始化配置（防止初始化时触发保存）
        /// Whether configuration is being initialized (prevent saving during initialization)
        /// </summary>
        private bool _isInitializing = true;

        // Flyout 相关字段已移除，现在使用 CustomDropdown

        /// <summary>
        /// 构造函数
        /// Constructor - initializes the llama service page
        /// </summary>
        public LlamaServicePage()
        {
            Log.Information("正在初始化 llama 服务管理页面...");
            this.InitializeComponent();

            // 使用全局单例实例 —— 与 MainPage「一键开启」共享同一个进程 / 状态 / 事件流，
            // 避免出现 "页面切换后丢失运行中服务" 或 "一键开启无法停止 UI 启动的服务" 的问题。
            // Reuse the global singleton so MainPage's one-click launcher and
            // this management page observe the *same* running process and
            // status events. Previously `new LlamaServiceManager()` here meant
            // the two views tracked independent (and thus inconsistent) state,
            // and MainPage had no handle at all to start/wait for the service
            // before launching LunaTranslator — which is precisely why
            // LunaTranslator 无法对接上 llama 服务.
            _serviceManager = LlamaServiceManager.Instance;
            _gpuDetector = new GpuDetector();
            _modelScanner = new ModelScanner();

            // 事件订阅放到 Loaded（见 LlamaServicePage_Loaded），而不是构造函数里。
            // 原因：MainPage 缓存了 Page 实例 → 用户每次"导航离开"都会触发 Unloaded，
            //       但"导航回来"并不会重跑构造函数。如果只在构造函数里 += 事件、
            //       仅在 Unloaded 里 -=，那么第二次切回本页时 StatusChanged /
            //       ParamsOutputDataReceived 就永远不会再触发 → 参数输出栏不刷新。
            // Event subscription is deferred to Loaded (see LlamaServicePage_Loaded)
            // instead of happening here. MainPage caches page instances — the
            // ctor only runs once, but Unloaded fires on every navigation-away,
            // so ctor-time subscription would be severed for good on the 2nd
            // return visit and the params output TextBox would go silent.
            this.Unloaded += LlamaServicePage_Unloaded;

            // 刷新 GPU 列表（不自动选择，等待加载配置）
            // Refresh GPU list (don't auto-select, wait for config loading)
            // RefreshGpuList(autoSelect: false);

            // 刷新模型列表
            // Refresh model list
            // RefreshModelList();

            // 加载预设列表
            // Load presets
            // LoadPresets();

            // 刷新主机地址列表
            // Refresh host list
            // RefreshHostList();

            // 刷新日志格式列表
            // Refresh log format list
            // RefreshLogFormatList();

            // 加载配置（在刷新列表之后）
            // Load configuration (after refreshing lists)
            // LoadConfiguration();

            // CustomDropdown 已自动处理下拉功能，无需手动初始化 Flyout

            // 🔧 修复：页面加载时强制同步状态
            // Fix: Force sync status when page loads
            _ = Task.Run(async () =>
            {
                await Task.Delay(500); // 等待页面完全加载
                await SyncServiceStatusAsync();
            });

            // 更新 UI 状态（从单例获取当前状态）
            // Update UI status (get current status from singleton)
            UpdateUIStatus(_serviceManager.Status);

            // 初始化 CheckBoxDragSelectBehavior（拖拽多选功能）
            // Initialize CheckBoxDragSelectBehavior for drag-to-select functionality
            CheckBoxDragSelectBehavior.Attach(ModelPathItemsControl);
            CheckBoxDragSelectBehavior.Attach(PresetItemsControl);
            Log.Information("✅ CheckBoxDragSelectBehavior 已初始化（2个下拉列表）");

            // 延迟初始化端口阴影动画（等待 XAML 加载完成）
            this.Loaded += LlamaServicePage_Loaded;

            Log.Information("llama 服务管理页面初始化完成");

            // 寮傛鍔犺浇鏁版嵁锛屼笉闃诲UI
            _ = LoadDataAsync();

        }


        /// <summary>
        /// 寮傛鍔犺浇椤甸潰鏁版嵁
        /// </summary>
        private async Task LoadDataAsync()
        {
            try
            {
                // 寤惰繜涓€灏忔鏃堕棿锛岃椤甸潰鍏堟覆鏌?                await Task.Delay(10);

                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    // 鍒锋柊 GPU 鍒楄〃锛堜笉鑷姩閫夋嫨锛岀瓑寰呴厤缃姞杞斤級
                    RefreshGpuList(autoSelect: false);

                    // 鍒锋柊妯″瀷鍒楄〃
                    RefreshModelList();

                    // 鍔犺浇棰勮鍒楄〃
                    LoadPresets();

                    // 鍒锋柊鐩戝惉鍦板潃鍒楄〃
                    RefreshHostList();

                    // 鍒锋柊鏃ュ織鏍煎紡鍒楄〃
                    RefreshLogFormatList();

                    // 鍔犺浇閰嶇疆锛堝湪鍒锋柊鍒楄〃涔嬪悗锛?                    LoadConfiguration();

                    Log.Information("椤甸潰鏁版嵁鍔犺浇瀹屾垚");
                });

                // 鍚屾鏈嶅姟鐘舵€?                await Task.Delay(100);
                await SyncServiceStatusAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "寮傛鍔犺浇鏁版嵁澶辫触: {Message}", ex.Message);
            }
        }
        /// <summary>
        /// 手动订阅端口按钮的 Pointer 事件（使用 handledEventsToo）
        /// </summary>
        private void AttachPortButtonEvents()
        {
            try
            {
                // 手动订阅事件，设置 handledEventsToo = true
                PortUpButton.AddHandler(UIElement.PointerPressedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(PortUpButton_PointerPressed_Manual), true);
                PortUpButton.AddHandler(UIElement.PointerReleasedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(PortUpButton_PointerReleased_Manual), true);
                
                PortDownButton.AddHandler(UIElement.PointerPressedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(PortDownButton_PointerPressed_Manual), true);
                PortDownButton.AddHandler(UIElement.PointerReleasedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(PortDownButton_PointerReleased_Manual), true);
                
                // GPU层数按钮
                GpuLayersUpButton.AddHandler(UIElement.PointerPressedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(GpuLayersUpButton_PointerPressed_Manual), true);
                GpuLayersUpButton.AddHandler(UIElement.PointerReleasedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(GpuLayersUpButton_PointerReleased_Manual), true);
                
                GpuLayersDownButton.AddHandler(UIElement.PointerPressedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(GpuLayersDownButton_PointerPressed_Manual), true);
                GpuLayersDownButton.AddHandler(UIElement.PointerReleasedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(GpuLayersDownButton_PointerReleased_Manual), true);
                
                // 上下文长度按钮
                ContextLengthUpButton.AddHandler(UIElement.PointerPressedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(ContextLengthUpButton_PointerPressed_Manual), true);
                ContextLengthUpButton.AddHandler(UIElement.PointerReleasedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(ContextLengthUpButton_PointerReleased_Manual), true);
                
                ContextLengthDownButton.AddHandler(UIElement.PointerPressedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(ContextLengthDownButton_PointerPressed_Manual), true);
                ContextLengthDownButton.AddHandler(UIElement.PointerReleasedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(ContextLengthDownButton_PointerReleased_Manual), true);
                
                // 并行线程数按钮
                ParallelThreadsUpButton.AddHandler(UIElement.PointerPressedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(ParallelThreadsUpButton_PointerPressed_Manual), true);
                ParallelThreadsUpButton.AddHandler(UIElement.PointerReleasedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(ParallelThreadsUpButton_PointerReleased_Manual), true);
                
                ParallelThreadsDownButton.AddHandler(UIElement.PointerPressedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(ParallelThreadsDownButton_PointerPressed_Manual), true);
                ParallelThreadsDownButton.AddHandler(UIElement.PointerReleasedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(ParallelThreadsDownButton_PointerReleased_Manual), true);
                
                Log.Information("✅ 所有按钮 Pointer 事件已手动订阅（handledEventsToo=true）");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "订阅端口按钮事件失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 端口上按钮按下事件（手动订阅版本）
        /// </summary>
        private void PortUpButton_PointerPressed_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ PortUpButton_PointerPressed_Manual 事件触发！");
            
            if (sender is Button button)
            {
                AnimatePortButton(button, true, true);
            }
        }

        /// <summary>
        /// 端口上按钮释放事件（手动订阅版本）
        /// </summary>
        private void PortUpButton_PointerReleased_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ PortUpButton_PointerReleased_Manual 事件触发！");
            
            if (sender is Button button)
            {
                AnimatePortButton(button, false, true);
            }
        }

        /// <summary>
        /// 端口下按钮按下事件（手动订阅版本）
        /// </summary>
        private void PortDownButton_PointerPressed_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ PortDownButton_PointerPressed_Manual 事件触发！");
            
            if (sender is Button button)
            {
                AnimatePortButton(button, true, false);
            }
        }

        /// <summary>
        /// 端口下按钮释放事件（手动订阅版本）
        /// </summary>
        private void PortDownButton_PointerReleased_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ PortDownButton_PointerReleased_Manual 事件触发！");
            
            if (sender is Button button)
            {
                AnimatePortButton(button, false, false);
            }
        }

        // ==================== GPU层数按钮事件 ====================
        
        /// <summary>
        /// GPU层数上按钮按下事件
        /// </summary>
        private void GpuLayersUpButton_PointerPressed_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                AnimatePortButton(button, true, true);
            }
        }

        /// <summary>
        /// GPU层数上按钮释放事件
        /// </summary>
        private void GpuLayersUpButton_PointerReleased_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                AnimatePortButton(button, false, true);
            }
        }

        /// <summary>
        /// GPU层数下按钮按下事件
        /// </summary>
        private void GpuLayersDownButton_PointerPressed_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                AnimatePortButton(button, true, false);
            }
        }

        /// <summary>
        /// GPU层数下按钮释放事件
        /// </summary>
        private void GpuLayersDownButton_PointerReleased_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                AnimatePortButton(button, false, false);
            }
        }

        // ==================== 上下文长度按钮事件 ====================
        
        /// <summary>
        /// 上下文长度上按钮按下事件
        /// </summary>
        private void ContextLengthUpButton_PointerPressed_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                AnimatePortButton(button, true, true);
            }
        }

        /// <summary>
        /// 上下文长度上按钮释放事件
        /// </summary>
        private void ContextLengthUpButton_PointerReleased_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                AnimatePortButton(button, false, true);
            }
        }

        /// <summary>
        /// 上下文长度下按钮按下事件
        /// </summary>
        private void ContextLengthDownButton_PointerPressed_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                AnimatePortButton(button, true, false);
            }
        }

        /// <summary>
        /// 上下文长度下按钮释放事件
        /// </summary>
        private void ContextLengthDownButton_PointerReleased_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                AnimatePortButton(button, false, false);
            }
        }

        // ==================== 并行线程数按钮事件 ====================
        
        /// <summary>
        /// 并行线程数上按钮按下事件
        /// </summary>
        private void ParallelThreadsUpButton_PointerPressed_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                AnimatePortButton(button, true, true);
            }
        }

        /// <summary>
        /// 并行线程数上按钮释放事件
        /// </summary>
        private void ParallelThreadsUpButton_PointerReleased_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                AnimatePortButton(button, false, true);
            }
        }

        /// <summary>
        /// 并行线程数下按钮按下事件
        /// </summary>
        private void ParallelThreadsDownButton_PointerPressed_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                AnimatePortButton(button, true, false);
            }
        }

        /// <summary>
        /// 并行线程数下按钮释放事件
        /// </summary>
        private void ParallelThreadsDownButton_PointerReleased_Manual(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                AnimatePortButton(button, false, false);
            }
        }

        /// <summary>
        /// 页面加载完成事件 - 初始化端口阴影动画
        /// </summary>
        private void LlamaServicePage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // 重新挂接到 LlamaServiceManager 的事件流。
                // 使用 "先 -= 再 +=" 的幂等写法，保证即便本方法被调用多次
                // (WinUI Frame 缓存下每次导航回来 Loaded 都会再触发一次) 也
                // 不会出现重复订阅导致事件被调用 N 次。
                // Re-attach to the service event stream idempotently. Loaded
                // fires on every navigation-back under page caching; using
                // "-= then +=" prevents duplicate invocations while still
                // restoring the subscription we tore down in Unloaded.
                _serviceManager.StatusChanged -= OnServiceStatusChanged;
                _serviceManager.StatusChanged += OnServiceStatusChanged;
                _serviceManager.ParamsOutputDataReceived -= OnParamsOutputDataReceived;
                _serviceManager.ParamsOutputDataReceived += OnParamsOutputDataReceived;

                // 重置 "已卸载" 标志：OnParamsOutputDataReceived 会检查该标志
                // 并跳过 UI 写入，如果我们从另一页回到本页却仍然是 true 就会
                // 让参数输出栏保持空白。
                // Reset the "is-unloaded" flag because OnParamsOutputDataReceived
                // skips UI writes while this is true; otherwise the output pane
                // stays blank forever after the first navigate-away.
                _isUnloaded = false;

                InitializePortShadowAnimation();
                AttachPortButtonEvents();
                
                // 初始化GPU列表和日志格式列表
                // 不自动选择GPU，让 RestoreSavedConfiguration 来恢复用户保存的配置
                RefreshGpuList(autoSelect: false);
                RefreshLogFormatList();
                
                // 恢复保存的配置
                RestoreSavedConfiguration();
                
                // 同步一次当前状态到 UI (从单例拿) —— 刚从别的页回来时 _serviceManager
                // 可能已经 Running，但本页在 Unloaded 时 UI 被重置 / 未更新过。
                // Push current singleton status into the UI — we may be returning
                // to this page while the service is already running.
                UpdateUIStatus(_serviceManager.Status);

                // 配置恢复完成，允许保存
                _isInitializing = false;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "初始化页面失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 初始化端口输入框的阴影动画
        /// </summary>
        private void InitializePortShadowAnimation()
        {
            try
            {
                // 查找端口Border容器
                _portBorderElement = this.FindName("PortBorderElement") as Border;
                
                if (_portBorderElement != null)
                {
                    // 清空所有输入框 ThemeShadow 的 Receivers，
                    // 防止按下按钮时阴影投射到参数信息卡片上导致变暗/变亮
                    // 实际阴影效果由底部阴影Border（Composition API）提供，不依赖 ThemeShadow 投影
                    if (_portBorderElement.Shadow is ThemeShadow)
                    {
                        _portBorderElement.Shadow = null;
                        Log.Information("✅ PortBorderElement ThemeShadow 已彻底移除");
                    }
                    
                    var gpuBorder = this.FindName("GpuLayersBorderElement") as Border;
                    if (gpuBorder?.Shadow is ThemeShadow)
                    {
                        gpuBorder.Shadow = null;
                        Log.Information("✅ GpuLayersBorderElement ThemeShadow 已彻底移除");
                    }
                    
                    var ctxBorder = this.FindName("ContextLengthBorderElement") as Border;
                    if (ctxBorder?.Shadow is ThemeShadow)
                    {
                        ctxBorder.Shadow = null;
                        Log.Information("✅ ContextLengthBorderElement ThemeShadow 已彻底移除");
                    }
                    
                    var parallelBorder = this.FindName("ParallelThreadsBorderElement") as Border;
                    if (parallelBorder?.Shadow is ThemeShadow)
                    {
                        parallelBorder.Shadow = null;
                        Log.Information("✅ ParallelThreadsBorderElement ThemeShadow 已彻底移除");
                    }
                    
                    // 同样处理所有 CustomDropdown 控件和 TextBox 的 BorderElement ThemeShadow
                    ParamsOutputBox.ApplyTemplate();
                    ServicePathTextBox.ApplyTemplate();
                    ManualGpuIndexTextBox.ApplyTemplate();
                    CustomAppendTextBox.ApplyTemplate();
                    CustomCommandTextBox.ApplyTemplate();
                    
                    var allBorders = FindVisualChildren<Border>(this)
                        .Where(b => b.Name == "BorderElement" && b.Shadow is ThemeShadow);
                    foreach (var b in allBorders)
                    {
                        // 直接将 Shadow 设置为 null，彻底移除阴影，防止投射到父元素
                        b.Shadow = null;
                    }
                    Log.Information("✅ 所有输入框 BorderElement ThemeShadow 已彻底移除");
                }
                
                // 查找端口阴影 Border（通过可视化树查找）
                _portShadowBorder = FindVisualChildren<Border>(this)
                    .FirstOrDefault(b => b.Parent is Grid grid && 
                                    grid.Children.Contains(PortTextBox) && 
                                    b != PortTextBox.Parent && 
                                    b != _portBorderElement);

                if (_portShadowBorder == null)
                {
                    Log.Warning("未找到端口阴影 Border");
                    return;
                }

                _portShadowVisual = ElementCompositionPreview.GetElementVisual(_portShadowBorder);
                var compositor = _portShadowVisual.Compositor;

                // 创建 DropShadow
                _portDropShadow = compositor.CreateDropShadow();
                _portDropShadow.BlurRadius = 0f;
                _portDropShadow.Offset = new Vector3(0, 0, 0);
                _portDropShadow.Opacity = 1f;
                _portDropShadow.Color = Windows.UI.Color.FromArgb(255, 0, 0, 0);

                // 创建 SpriteVisual 并应用阴影
                var shadowVisual = compositor.CreateSpriteVisual();
                shadowVisual.Shadow = _portDropShadow;
                shadowVisual.Size = new Vector2((float)_portShadowBorder.ActualWidth, (float)_portShadowBorder.ActualHeight);

                ElementCompositionPreview.SetElementChildVisual(_portShadowBorder, shadowVisual);

                // 监听尺寸变化
                _portShadowBorder.SizeChanged += (s, args) =>
                {
                    if (shadowVisual != null)
                    {
                        shadowVisual.Size = new Vector2((float)args.NewSize.Width, (float)args.NewSize.Height);
                    }
                };

                Log.Information("✅ 端口阴影动画已初始化");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "初始化端口阴影动画失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 同步服务状态（检查进程实际状态）
        /// Synchronizes service status by checking actual process state
        /// </summary>
        private async Task SyncServiceStatusAsync()
        {
            try
            {
                Log.Information("🔧 开始同步服务状态...");
                
                // 获取当前状态
                var currentStatus = _serviceManager.Status;
                Log.Information("当前状态: {Status}", currentStatus);
                
                // 如果状态显示运行中，验证进程是否真的在运行
                if (currentStatus == LlamaServiceStatus.Running)
                {
                    // 通过检查 llama-server 进程来验证
                    var processes = System.Diagnostics.Process.GetProcessesByName("llama-server");
                    bool processRunning = processes.Length > 0;
                    
                    Log.Information("llama-server 进程数量: {Count}", processes.Length);
                    
                    if (!processRunning)
                    {
                        Log.Warning("⚠️ 状态不同步：Status=Running 但没有找到 llama-server 进程");
                        Log.Information("尝试停止服务以同步状态...");
                        
                        // 调用停止服务来强制同步状态
                        await _serviceManager.StopServiceAsync(force: true);
                        
                        Log.Information("✅ 状态同步完成");
                    }
                    else
                    {
                        Log.Information("✅ 状态同步正常：服务运行中");
                    }
                }
                else
                {
                    Log.Information("✅ 状态同步正常：服务未运行");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "同步服务状态失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 页面卸载事件处理
        /// Page unloaded event handler - cleanup resources
        /// </summary>
        private void LlamaServicePage_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                Log.Information("llama 服务管理页面正在卸载...");
                _isUnloaded = true;

                // 清理 FlyoutPressBehavior
                // _modelPathBehavior?.Detach();
                // _presetBehavior?.Detach();
                // _hostBehavior?.Detach();

                // 取消订阅事件，防止内存泄漏和访问冲突
                // Unsubscribe from events to prevent memory leaks and access violations
                if (_serviceManager != null)
                {
                    _serviceManager.StatusChanged -= OnServiceStatusChanged;
                    _serviceManager.ParamsOutputDataReceived -= OnParamsOutputDataReceived;
                }

                // 注意：不能在这里 -= this.Unloaded。MainPage 缓存了 Page 实例，
                // 用户再次导航回来后还会经历 Loaded → Unloaded 生命周期，若这里
                // 解除 Unloaded 的订阅，下一次就不会再走 Unloaded 进行事件解绑，
                // 从而导致来回切页后 ParamsOutputDataReceived 被重复订阅 N 次。
                // Intentionally NOT detaching `this.Unloaded -= LlamaServicePage_Unloaded`:
                // page instances are cached by MainPage, so Loaded/Unloaded will
                // alternate on every navigation in/out. Detaching here would
                // disable subsequent cleanups → Loaded's "-= then +=" would
                // still work but we'd leak event subscribers over time.

                Log.Information("llama 服务管理页面已卸载");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "页面卸载时发生错误: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 加载配置
        /// Loads configuration from app config
        /// </summary>
        private void LoadConfiguration()
        {
            try
            {
                var config = App.AppConfig.LaunchSettings.LlamaService;

                // 加载基本配置
                // Load basic configuration
                ServicePathTextBox.Text = config.ServicePath;
                
                // 不在这里设置 ComboBox 的值，等待 Loaded 事件
                // Don't set ComboBox values here, wait for Loaded event
                
                PortTextBox.Text = config.Port.ToString();
                GpuLayersSlider.Value = config.GpuLayers;
                GpuLayersTextBox.Text = config.GpuLayers.ToString();
                ContextLengthSlider.Value = config.ContextLength;
                ContextLengthTextBox.Text = config.ContextLength.ToString();
                ParallelThreadsSlider.Value = config.ParallelThreads;
                ParallelThreadsTextBox.Text = config.ParallelThreads.ToString();
                FlashAttentionCheckBox.IsChecked = config.FlashAttention;
                NoMmapCheckBox.IsChecked = config.NoMmap;
                LogFormatComboBox.Text = config.LogFormat;
                SingleGpuCheckBox.IsChecked = config.SingleGpuMode;
                ManualGpuIndexTextBox.Text = config.ManualGpuIndex;
                CustomAppendTextBox.Text = config.CustomCommandAppend;
                CustomCommandTextBox.Text = config.CustomCommand;

                // 加载模型搜索路径
                // Load model search paths
                foreach (var path in config.ModelSearchPaths)
                {
                    _modelScanner.AddSearchPath(path);
                }

                Log.Information("配置已加载");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载配置失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 保存配置
        /// Saves configuration to app config
        /// </summary>
        private void SaveConfiguration()
        {
            // 如果正在初始化，不保存配置
            if (_isInitializing)
            {
                return;
            }

            // 正在加载预设 → SaveConfiguration 会清空 SelectedPreset，并且 TextBox
            // 尚未全部就位时读出的值可能是半新半旧的。直接跳过，由 LoadPresetByName
            // 结尾统一做一次 "完整 & 带 SelectedPreset" 的落盘。
            // Skip while a preset is being applied: SaveConfiguration would wipe
            // SelectedPreset and read partially-updated TextBox values.
            // LoadPresetByName performs a single canonical save at its tail.
            if (_isApplyingPreset)
            {
                return;
            }
            
            try
            {
                var config = App.AppConfig.LaunchSettings.LlamaService;

                // 保存基本配置
                // Save basic configuration
                config.ServicePath = ServicePathTextBox.Text;
                config.ModelPath = ModelPathComboBox.Text;
                config.Host = HostComboBox.Text;
                config.Port = int.TryParse(PortTextBox.Text, out int port) ? port : 8080;
                
                // 从文本框读取GPU层数
                if (int.TryParse(GpuLayersTextBox.Text, out int gpuLayers))
                {
                    config.GpuLayers = gpuLayers;
                }
                else
                {
                    config.GpuLayers = (int)GpuLayersSlider.Value;
                }
                
                // 从文本框读取上下文长度
                if (int.TryParse(ContextLengthTextBox.Text, out int contextLength))
                {
                    config.ContextLength = contextLength;
                }
                else
                {
                    config.ContextLength = (int)ContextLengthSlider.Value;
                }
                
                // 从文本框读取并行线程数
                if (int.TryParse(ParallelThreadsTextBox.Text, out int parallelThreads))
                {
                    config.ParallelThreads = parallelThreads;
                }
                else
                {
                    config.ParallelThreads = (int)ParallelThreadsSlider.Value;
                }
                config.FlashAttention = FlashAttentionCheckBox.IsChecked ?? true;
                config.NoMmap = NoMmapCheckBox.IsChecked ?? true;
                config.LogFormat = string.IsNullOrWhiteSpace(LogFormatComboBox.Text) ? "text" : LogFormatComboBox.Text;
                config.SingleGpuMode = SingleGpuCheckBox.IsChecked ?? true;
                
                // 使用GPU名称而不是索引
                config.SelectedGpuIndex = GetGpuIndexByName(GpuComboBox.Text);
                
                config.ManualGpuIndex = ManualGpuIndexTextBox.Text;
                config.CustomCommandAppend = CustomAppendTextBox.Text;
                config.CustomCommand = CustomCommandTextBox.Text;
                
                // 清除预设选择（用户手动修改了配置）
                // Clear preset selection (user manually modified configuration)
                // 注意：LoadPresetByName 会重新设置 SelectedPreset
                config.SelectedPreset = string.Empty;

                // 保存模型路径历史记录
                // Save model path history
                if (!string.IsNullOrWhiteSpace(config.ModelPath))
                {
                    // 移除重复项
                    // Remove duplicates
                    config.ModelPathHistory.RemoveAll(p => p == config.ModelPath);
                    
                    // 添加到列表开头
                    // Add to beginning of list
                    config.ModelPathHistory.Insert(0, config.ModelPath);
                    
                    // 限制历史记录数量为 10 个
                    // Limit history to 10 items
                    if (config.ModelPathHistory.Count > 10)
                    {
                        config.ModelPathHistory.RemoveRange(10, config.ModelPathHistory.Count - 10);
                    }
                }

                // 保存到文件
                // Save to file
                App.ConfigManager.SaveConfig(App.AppConfig);
                Log.Information("配置已保存");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存配置失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 恢复保存的配置
        /// Restores saved configuration values to UI controls
        /// </summary>
        private void RestoreSavedConfiguration()
        {
            try
            {
                var config = App.AppConfig.LaunchSettings.LlamaService;

                // 恢复 llama-server 可执行文件路径。
                // 之前的实现遗漏了这一句 —— 重启后 ServicePathTextBox 为空，用户一动
                // UI 就触发 SaveConfiguration() 把空字符串写回 config.ServicePath，
                // 把之前浏览选择的路径抹掉，于是每次都要重选。
                // Previously this line was missing, which wiped out the user's
                // saved service path on next launch: the empty TextBox was
                // written back to config.ServicePath by any subsequent
                // SaveConfiguration() call, overwriting the persisted value.
                if (!string.IsNullOrWhiteSpace(config.ServicePath))
                {
                    ServicePathTextBox.Text = config.ServicePath;
                    Log.Information("已恢复 llama-server 路径: {Path}", config.ServicePath);
                }
                
                // 恢复模型路径
                if (!string.IsNullOrWhiteSpace(config.ModelPath))
                {
                    ModelPathComboBox.Text = config.ModelPath;
                    Log.Information("已恢复模型路径: {Path}", config.ModelPath);
                }
                
                // 恢复主机地址
                if (!string.IsNullOrWhiteSpace(config.Host))
                {
                    HostComboBox.Text = config.Host;
                    Log.Information("已恢复主机地址: {Host}", config.Host);
                }
                
                // 恢复日志格式
                if (!string.IsNullOrWhiteSpace(config.LogFormat))
                {
                    LogFormatComboBox.Text = config.LogFormat;
                    Log.Information("已恢复日志格式: {Format}", config.LogFormat);
                }
                
                // 恢复端口
                PortTextBox.Text = config.Port.ToString();
                
                // 恢复 GPU 层数
                GpuLayersTextBox.Text = config.GpuLayers.ToString();
                GpuLayersSlider.Value = config.GpuLayers;
                
                // 恢复上下文长度
                ContextLengthTextBox.Text = config.ContextLength.ToString();
                ContextLengthSlider.Value = config.ContextLength;
                
                // 恢复并行线程数
                ParallelThreadsTextBox.Text = config.ParallelThreads.ToString();
                ParallelThreadsSlider.Value = config.ParallelThreads;
                
                // 恢复复选框
                FlashAttentionCheckBox.IsChecked = config.FlashAttention;
                NoMmapCheckBox.IsChecked = config.NoMmap;
                SingleGpuCheckBox.IsChecked = config.SingleGpuMode;
                
                // 恢复其他文本框
                ManualGpuIndexTextBox.Text = config.ManualGpuIndex;
                CustomAppendTextBox.Text = config.CustomCommandAppend;
                CustomCommandTextBox.Text = config.CustomCommand;
                
                // 恢复 GPU 选择
                if (config.SelectedGpuIndex >= 0 && config.SelectedGpuIndex < _gpuDetector.AllGpus.Count)
                {
                    var gpu = _gpuDetector.AllGpus[config.SelectedGpuIndex];
                    GpuComboBox.Text = gpu.DisplayName;
                    Log.Information("已恢复 GPU: {Name}", gpu.DisplayName);
                }
                else if (!string.IsNullOrWhiteSpace(GpuComboBox.Text))
                {
                    // 如果索引无效，但ComboBox有文本，保持当前文本
                    Log.Information("已恢复 GPU 文本: {Name}", GpuComboBox.Text);
                }
                
                // 恢复上次选择的预设
                if (!string.IsNullOrWhiteSpace(config.SelectedPreset))
                {
                    LoadPresetByName(config.SelectedPreset);
                    Log.Information("已恢复预设: {Name}", config.SelectedPreset);
                }
                
                Log.Information("配置恢复完成");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "恢复配置失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 刷新 GPU 列表
        /// Refreshes GPU list - 动态检测真实 GPU 信息
        /// </summary>
        /// <param name="autoSelect">是否自动选择推荐的GPU</param>
        private void RefreshGpuList(bool autoSelect = true)
        {
            try
            {
                // 重新检测 GPU（实时更新）
                // Re-detect GPUs (real-time update)
                _gpuDetector.DetectGpus();
                
                // 在 UI 线程上执行
                // Execute on UI thread
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (_isUnloaded) return;
                    
                    var gpuList = new List<string>();

                    if (_gpuDetector.AllGpus.Count == 0)
                    {
                        // 如果没有检测到 GPU，添加提示信息
                        // If no GPU detected, add hint
                        gpuList.Add("未检测到 GPU");
                        Log.Warning("未检测到任何 GPU");
                    }
                    else
                    {
                        foreach (var gpu in _gpuDetector.AllGpus)
                        {
                            gpuList.Add(gpu.DisplayName);
                        }

                        // 只有在 autoSelect 为 true 时才自动选择推荐的 GPU
                        // Only auto-select recommended GPU when autoSelect is true
                        if (autoSelect)
                        {
                            var recommendedGpu = _gpuDetector.GetRecommendedGpu();
                            if (recommendedGpu != null)
                            {
                                GpuComboBox.Text = recommendedGpu.DisplayName;
                                Log.Information("推荐使用 GPU: {Name}", recommendedGpu.Name);
                            }
                            else if (_gpuDetector.AllGpus.Count > 0)
                            {
                                GpuComboBox.Text = _gpuDetector.AllGpus[0].DisplayName;
                            }
                        }

                        Log.Information("GPU 列表已刷新，共 {Count} 个 GPU", _gpuDetector.AllGpus.Count);
                    }
                    
                    // 更新 GpuItemsControl
                    GpuItemsControl.ItemsSource = gpuList;
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "刷新 GPU 列表失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 刷新模型列表
        /// Refreshes model list
        /// </summary>
        private void RefreshModelList()
        {
            try
            {
                // 在 UI 线程上执行
                // Execute on UI thread
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (_isUnloaded) return;

                    // 清空现有列表
                    var modelPaths = new ObservableCollection<string>();

                    // 添加常见的模型搜索路径
                    // Add common model search paths
                    AddCommonModelPaths();

                    // 获取排序方式
                    // Get sort method
                    var sortMethod = App.AppConfig.LaunchSettings.LlamaService.ModelSortMethod switch
                    {
                        "FileName" => ModelSortMethod.FileName,
                        "FileSize" => ModelSortMethod.FileSize,
                        _ => ModelSortMethod.ModifiedTime
                    };

                    // 扫描模型
                    // Scan models
                    var models = _modelScanner.ScanModels(sortMethod);

                    // 先添加历史记录中的模型（如果存在）
                    // First add models from history (if they exist)
                    var config = App.AppConfig.LaunchSettings.LlamaService;
                    var addedPaths = new HashSet<string>();
                    
                    foreach (var historyPath in config.ModelPathHistory)
                    {
                        if (!string.IsNullOrWhiteSpace(historyPath) && File.Exists(historyPath))
                        {
                            modelPaths.Add(historyPath);
                            addedPaths.Add(historyPath);
                        }
                    }

                    // 然后添加扫描到的模型（避免重复）
                    // Then add scanned models (avoid duplicates)
                    foreach (var model in models)
                    {
                        if (!addedPaths.Contains(model.FullPath))
                        {
                            modelPaths.Add(model.FullPath);
                            addedPaths.Add(model.FullPath);
                        }
                    }

                    // 更新 ItemsControl
                    ModelPathItemsControl.ItemsSource = modelPaths;
                    
                    Log.Information("模型列表已刷新，历史记录: {HistoryCount}, 扫描到: {ScanCount}, 总计: {Total}", 
                        config.ModelPathHistory.Count, models.Count, modelPaths.Count);
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "刷新模型列表失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 添加常见的模型搜索路径
        /// Adds common model search paths
        /// </summary>
        private void AddCommonModelPaths()
        {
            try
            {
                // 清空现有路径
                // Clear existing paths
                _modelScanner.ClearSearchPaths();

                // 添加配置中的路径
                // Add paths from configuration
                var config = App.AppConfig.LaunchSettings.LlamaService;
                foreach (var path in config.ModelSearchPaths)
                {
                    if (Directory.Exists(path))
                    {
                        _modelScanner.AddSearchPath(path);
                    }
                }

                // 添加常见路径
                // Add common paths
                var commonPaths = new List<string>
                {
                    // 当前目录
                    AppDomain.CurrentDomain.BaseDirectory,
                    
                    // 用户文档目录
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "models"),
                    
                    // 用户下载目录
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                    
                    // 桌面
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    
                    // C:\models (常见的模型存放位置)
                    @"C:\models",
                    
                    // D:\models
                    @"D:\models",
                };

                foreach (var path in commonPaths)
                {
                    if (Directory.Exists(path))
                    {
                        _modelScanner.AddSearchPath(path);
                        Log.Debug("添加模型搜索路径: {Path}", path);
                    }
                }

                Log.Information("已添加 {Count} 个模型搜索路径", _modelScanner.SearchPaths.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "添加常见模型路径失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 加载预设列表
        /// Loads preset list
        /// </summary>
        private void LoadPresets()
        {
            try
            {
                // 在 UI 线程上执行
                // Execute on UI thread
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (_isUnloaded) return;

                    var presetNames = new ObservableCollection<string>();
                    var presets = App.AppConfig.LaunchSettings.LlamaService.Presets;
                    
                    if (presets.Count == 0)
                    {
                        // 如果没有预设，添加提示信息
                        // If no presets, add hint
                        PresetComboBox.PlaceholderText = "暂无预设，输入名称后点击保存";
                        Log.Information("暂无配置预设");
                    }
                    else
                    {
                        foreach (var preset in presets)
                        {
                            presetNames.Add(preset.Name);
                        }
                        
                        // 先清空再赋值，强制刷新 UI
                        PresetItemsControl.ItemsSource = null;
                        PresetItemsControl.ItemsSource = presetNames;
                        
                        Log.Information("预设列表已加载，共 {Count} 个预设", presets.Count);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载预设列表失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 服务可执行文件路径文本框内容变化事件
        /// Handles manual edits of the llama-server executable path.
        /// 
        /// 目的 (Purpose):
        /// BrowseServiceButton_Click 只覆盖 "用户点浏览选择文件" 这个入口，用户直接
        /// 在 TextBox 里输入 / 粘贴路径时之前没有任何 save 被触发，下次关闭程序
        /// (走 Window_Closed → SaveConfig) 时 AppConfig.ServicePath 依旧是旧值 / 空。
        /// 通过监听 TextChanged 即时把新路径写回 AppConfig，配合 Window_Closed 的
        /// 最终 SaveConfig 就能保证重启后路径不丢。
        /// Mirrors the pasted / typed path into the in-memory AppConfig so the
        /// Window_Closed → SaveConfig path persists it. Without this hook the
        /// direct-type case silently discarded the user's path on exit.
        /// </summary>
        private void ServicePathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 初始化阶段 (RestoreSavedConfiguration 正在从磁盘 → TextBox 写值) 不要
            // 反向触发保存，否则会发生 "空 → 空" 的冗余写。
            // Suppress during init so we don't ping-pong between restore and save.
            if (_isInitializing) return;

            try
            {
                App.AppConfig.LaunchSettings.LlamaService.ServicePath = ServicePathTextBox.Text ?? string.Empty;
                // 这里不立即写磁盘 —— 频繁 TextChanged 会造成大量 IO。
                // 最终保存交给 Window_Closed / 显式动作(StartButton 等)。
                // Defer the disk write to Window_Closed / explicit user actions
                // to avoid rewriting the JSON file on every keystroke.
            }
            catch (Exception ex)
            {
                Log.Error(ex, "同步 ServicePath 到 AppConfig 失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 浏览服务文件按钮点击事件
        /// Browse service file button click event
        /// </summary>
        private async void BrowseServiceButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = await ShowFilePickerAsync("可执行文件 (*.exe)|*.exe", "选择 llama-server");
            if (!string.IsNullOrEmpty(selectedPath))
            {
                ServicePathTextBox.Text = selectedPath;

                // 用户明确挑选了文件 → 这是显式动作，必须立即落盘。
                // 过去这里只调用 SaveConfiguration()，但它在 _isInitializing==true 时
                // 会静默 early-return（发生在 Loaded 尚未完成 / 抛异常的情况下），
                // 导致选完路径重启程序后路径丢失。
                // 这里强制取消初始化标志、并直写 config，保证落盘不再依赖时序。
                // User performed an explicit action → must persist unconditionally.
                // Previously this code path silently no-opped whenever
                // _isInitializing was still true (e.g. when Loaded hadn't run
                // to completion), leaving the chosen path lost on next startup.
                _isInitializing = false;
                App.AppConfig.LaunchSettings.LlamaService.ServicePath = selectedPath;
                App.ConfigManager.SaveConfig(App.AppConfig);
                Log.Information("已保存 ServicePath: {Path}", selectedPath);

                // 继续走常规保存，以便 UI 上的其它字段（Host/Port/...）也一起被持久化
                // Follow up with the normal save so Host/Port/... also land on disk.
                SaveConfiguration();
            }
        }

        /// <summary>
        /// 浏览模型文件按钮点击事件
        /// Browse model file button click event
        /// </summary>
        private async void BrowseModelButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedPath = await ShowFilePickerAsync("GGUF模型 (*.gguf)|*.gguf", "选择 GGUF模型");
            if (!string.IsNullOrEmpty(selectedPath))
            {
                // 直接设置文本
                // Set text directly
                ModelPathComboBox.Text = selectedPath;

                // 同 BrowseServiceButton_Click：模型路径的显式选择也必须立即落盘，
                // 否则重启后会回到旧值，让用户以为 "选了没生效"。
                // Same fix as BrowseServiceButton_Click: persist the explicit
                // user choice immediately, bypassing _isInitializing guard.
                _isInitializing = false;
                App.AppConfig.LaunchSettings.LlamaService.ModelPath = selectedPath;
                App.ConfigManager.SaveConfig(App.AppConfig);
                Log.Information("已保存 ModelPath: {Path}", selectedPath);
                
                // 保存配置
                // Save configuration
                SaveConfiguration();
                
                // 刷新模型列表
                RefreshModelList();
                
                Log.Information("用户选择了模型文件: {Path}", selectedPath);
            }
        }

        /// <summary>
        /// 显示文件选择器（支持 Win32 备选方案）
        /// Shows file picker with Win32 fallback
        /// </summary>
        private async Task<string?> ShowFilePickerAsync(string filter, string title)
        {
            try
            {
                // 获取窗口句柄
                // Get window handle
                var app = (App)App.Current;
                if (app?.window == null)
                {
                    Log.Error("无法获取应用程序窗口实例");
                    return null;
                }

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(app.window);

                // 尝试使用 WinUI FileOpenPicker
                // Try WinUI FileOpenPicker first
                try
                {
                    var picker = new FileOpenPicker();
                    var extensions = filter.Split('|')[1].Split(';');
                    foreach (var ext in extensions)
                    {
                        picker.FileTypeFilter.Add(ext.Replace("*", ""));
                    }
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                        return file.Path;
                    }
                }
                catch (COMException)
                    {
                    // WinUI FileOpenPicker 失败，使用 Win32 备选方案
                    // WinUI FileOpenPicker failed, use Win32 fallback
                    Log.Warning("WinUI FileOpenPicker 失败，切换到 Win32 文件对话框");
                    return Win32FileDialog.ShowOpenFileDialog(hwnd, filter, title);
                }

                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "文件选择失败: {Message}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 启动服务按钮点击事件
        /// Start service button click event
        /// </summary>
        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log.Information("用户点击「启动服务」按钮");

                // 保存当前配置
                // Save current configuration
                SaveConfiguration();

                // 获取配置
                // Get configuration
                var config = App.AppConfig.LaunchSettings.LlamaService;

                // 验证配置
                // Validate configuration
                if (string.IsNullOrWhiteSpace(config.ServicePath))
                {
                    await ShowErrorDialogAsync("错误", "请先选择 llama-server");
                    return;
                }

                if (string.IsNullOrWhiteSpace(config.ModelPath))
                {
                    await ShowErrorDialogAsync("错误", "请先选择 GGUF模型");
                    return;
                }

                // 启动服务
                // Start service
                bool success = await _serviceManager.StartServiceAsync(config, _gpuDetector);

                if (success)
                {
                    // 强制更新 UI 状态
                    // Force update UI status
                    UpdateUIStatus(_serviceManager.Status);
                    
                    await ShowSuccessDialogAsync("成功", "llama 服务已成功启动");
                }
                else
                {
                    await ShowErrorDialogAsync("错误", "llama 服务启动失败，请查看日志");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动服务失败: {Message}", ex.Message);
                await ShowErrorDialogAsync("错误", $"启动服务时发生错误：\n{ex.Message}");
            }
        }

        /// <summary>
        /// 停止服务按钮点击事件
        /// Stop service button click event
        /// </summary>
        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log.Information("用户点击「停止服务」按钮");

                bool success = await _serviceManager.StopServiceAsync();

                if (success)
                {
                    // 强制更新 UI 状态
                    // Force update UI status
                    UpdateUIStatus(_serviceManager.Status);
                    
                    await ShowSuccessDialogAsync("成功", "llama 服务已停止");
                }
                else
                {
                    await ShowErrorDialogAsync("错误", "llama 服务停止失败");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止服务失败: {Message}", ex.Message);
                await ShowErrorDialogAsync("错误", $"停止服务时发生错误：\n{ex.Message}");
            }
        }

        /// <summary>
        /// 重启服务按钮点击事件
        /// Restart service button click event
        /// </summary>
        private async void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log.Information("用户点击「重启服务」按钮");

                // 停止服务
                // Stop service
                await _serviceManager.StopServiceAsync();

                // 等待一秒
                // Wait for a second
                await Task.Delay(1000);

                // 启动服务
                // Start service
                SaveConfiguration();
                var config = App.AppConfig.LaunchSettings.LlamaService;
                bool success = await _serviceManager.StartServiceAsync(config, _gpuDetector);

                if (success)
                {
                    await ShowSuccessDialogAsync("成功", "llama 服务已重启");
                }
                else
                {
                    await ShowErrorDialogAsync("错误", "llama 服务重启失败");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "重启服务失败: {Message}", ex.Message);
                await ShowErrorDialogAsync("错误", $"重启服务时发生错误：\n{ex.Message}");
            }
        }

        /// <summary>
        /// 保存预设按钮点击事件
        /// Save preset button click event
        /// </summary>
        private async void SavePresetButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string presetName = PresetComboBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(presetName))
                {
                    await ShowErrorDialogAsync("错误", "请输入预设名称");
                    return;
                }

                // 创建预设
                // Create preset
                var preset = new LlamaServicePreset
                {
                    Name = presetName,
                    ModelPath = ModelPathComboBox.Text,
                    Host = HostComboBox.Text,
                    Port = int.TryParse(PortTextBox.Text, out int port) ? port : 8080,
                    GpuLayers = int.TryParse(GpuLayersTextBox.Text, out int gpuLayers) ? gpuLayers : (int)GpuLayersSlider.Value,
                    ContextLength = int.TryParse(ContextLengthTextBox.Text, out int contextLength) ? contextLength : (int)ContextLengthSlider.Value,
                    ParallelThreads = int.TryParse(ParallelThreadsTextBox.Text, out int parallelThreads) ? parallelThreads : (int)ParallelThreadsSlider.Value,
                    FlashAttention = FlashAttentionCheckBox.IsChecked ?? true,
                    NoMmap = NoMmapCheckBox.IsChecked ?? true,
                    LogFormat = string.IsNullOrWhiteSpace(LogFormatComboBox.Text) ? "text" : LogFormatComboBox.Text,
                    SingleGpuMode = SingleGpuCheckBox.IsChecked ?? true,
                    GpuIndex = GetGpuIndexByName(GpuComboBox.Text),
                    ManualGpuIndex = ManualGpuIndexTextBox.Text,
                    CustomCommandAppend = CustomAppendTextBox.Text,
                    CustomCommand = CustomCommandTextBox.Text
                };

                // 保存预设
                // Save preset
                var presets = App.AppConfig.LaunchSettings.LlamaService.Presets;
                var existingPreset = presets.Find(p => p.Name == presetName);
                if (existingPreset != null)
                {
                    presets.Remove(existingPreset);
                }
                presets.Add(preset);

                // 保存配置
                // Save configuration
                App.ConfigManager.SaveConfig(App.AppConfig);

                // 刷新预设列表
                // Refresh preset list
                LoadPresets();
                PresetComboBox.Text = presetName;

                await ShowSuccessDialogAsync("成功", $"预设 \"{presetName}\" 已保存");
                Log.Information("预设已保存: {Name}", presetName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存预设失败: {Message}", ex.Message);
                await ShowErrorDialogAsync("错误", $"保存预设失败：\n{ex.Message}");
            }
        }

        // 拖动多选相关字段
        private bool _isDragging = false;
        private HashSet<CheckBox> _draggedCheckBoxes = new HashSet<CheckBox>();
        private bool _dragToCheck = false; // 拖动方向：true=勾选，false=取消勾选
        private bool _justFinishedDragging = false; // 刚完成拖动，用于阻止 Tapped
        private bool _isDoubleClicking = false; // 正在双击，用于阻止 PointerPressed
        
        // 延迟处理相关字段
        private System.Threading.Timer? _tapDelayTimer;
        private CheckBox? _lastTappedCheckBox;
        private bool _lastCheckBoxState = false; // 保存点击时的初始状态

        // 端口阴影动画相关字段
        private Border? _portShadowBorder;
        private Visual? _portShadowVisual;
        private Microsoft.UI.Composition.DropShadow? _portDropShadow;
        
        // 端口输入框容器（用于ThemeShadow）
        private Border? _portBorderElement;

        /// <summary>
        /// 模型路径项单击事件 - 立即切换勾选状态
        /// Model path item tapped event - immediately toggle checkbox state
        /// </summary>
        private void ModelPathItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                // 立即标记事件已处理，防止冒泡
                e.Handled = true;
                
                // 如果刚完成拖动，忽略 Tapped 事件
                if (_justFinishedDragging)
                {
                    _justFinishedDragging = false;
                    Log.Information("🖱️ 忽略 Tapped（刚完成拖动）");
                    return;
                }
                
                if (sender is not Border border)
                {
                    return;
                }
                
                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null || checkBox.Tag is not string path)
                {
                    return;
                }

                // 获取点击位置
                var position = e.GetPosition(border);
                
                // CheckBox 通常在左侧，宽度约 40-50px
                // 如果点击位置 X < 50，认为是点击多选框；否则认为是点击文本
                bool clickedOnCheckBox = position.X < 50;
                
                if (clickedOnCheckBox)
                {
                    // 保存点击前的状态（用于双击恢复）
                    _lastCheckBoxState = checkBox.IsChecked == true;
                    _lastTappedCheckBox = checkBox;
                    
                    // 单击多选框：立即切换勾选状态
                    checkBox.IsChecked = !checkBox.IsChecked;
                    Log.Information("🖱️ 单击多选框: {Path}, 原状态: {OldState}, 新状态: {NewState}", path, _lastCheckBoxState, checkBox.IsChecked);
                }
                else
                {
                    // 单击文本：不做任何操作（或者你可以添加其他逻辑）
                    Log.Information("🖱️ 单击文本: {Path}（无操作）", path);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理单击事件失败: {Message}", ex.Message);
            }
        }

        private void ModelPathItem_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            try
            {
                // 标记正在双击
                _isDoubleClicking = true;
                
                // 取消拖动状态
                if (_isDragging)
                {
                    _isDragging = false;
                    _draggedCheckBoxes.Clear();
                    Log.Information("🖱️ 双击时取消拖动状态");
                }
                
                if (sender is not Border border)
                {
                    _isDoubleClicking = false;
                    return;
                }
                
                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null || checkBox.Tag is not string path)
                {
                    _isDoubleClicking = false;
                    return;
                }

                // 获取点击位置
                var position = e.GetPosition(border);
                
                // CheckBox 通常在左侧，宽度约 40-50px
                // 如果点击位置 X < 50，认为是点击多选框；否则认为是点击文本
                bool clickedOnCheckBox = position.X < 50;
                
                if (clickedOnCheckBox)
                {
                    // 双击多选框：不做任何操作
                    // Tapped 事件已经处理了两次点击（第一次和第二次都触发了 Tapped）
                    Log.Information("🖱️ 双击多选框: {Path}（Tapped 已处理）", path);
                }
                else
                {
                    // 双击选项文本：选取该选项
                    Log.Information("🖱️ 双击选项文本，选取: {Path}", path);
                    
                    // 清空所有复选框
                    var checkBoxes = FindVisualChildren<CheckBox>(ModelPathItemsControl);
                    foreach (var cb in checkBoxes)
                    {
                        cb.IsChecked = false;
                    }
                    
                    // 设置文本框并关闭下拉框
                    ModelPathComboBox.Text = path;
                    SaveConfiguration();
                    ModelPathComboBox.IsOpen = false;
                    
                    Log.Information("✅ 已选取模型路径: {Path}", path);
                }

                // 延迟重置双击标志，确保后续的 PointerPressed 不会执行
                _ = Task.Delay(100).ContinueWith(_ => 
                {
                    DispatcherQueue.TryEnqueue(() => _isDoubleClicking = false);
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理双击事件失败: {Message}", ex.Message);
                _isDoubleClicking = false;
            }
        }

        private void ModelPathItem_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                // 如果正在双击，忽略 PointerPressed
                if (_isDoubleClicking)
                {
                    Log.Information("🖱️ 忽略 PointerPressed（正在双击）");
                    e.Handled = true;
                    return;
                }
                
                if (sender is not Border border) return;
                
                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null) return;

                // 开始拖动
                _isDragging = true;
                _draggedCheckBoxes.Clear();
                
                // 确定拖动方向：如果当前未勾选，则拖动为勾选；如果已勾选，则拖动为取消勾选
                _dragToCheck = checkBox.IsChecked != true;
                
                // 设置起始项的状态
                checkBox.IsChecked = _dragToCheck;
                _draggedCheckBoxes.Add(checkBox);
                Log.Information("🖱️ 开始拖动，方向: {Direction}, 起始项: {Path}", _dragToCheck ? "勾选" : "取消勾选", checkBox.Tag);

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理 PointerPressed 失败: {Message}", ex.Message);
            }
        }

        private void ModelPathItem_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (!_isDragging) return;
                if (sender is not Border border) return;
                
                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null || _draggedCheckBoxes.Contains(checkBox)) return;

                // 使用统一的拖动方向设置状态
                checkBox.IsChecked = _dragToCheck;
                _draggedCheckBoxes.Add(checkBox);
                Log.Information("🖱️ 拖动经过，设置项: {Path}, 状态: {State}", checkBox.Tag, _dragToCheck);

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理 PointerMoved 失败: {Message}", ex.Message);
            }
        }

        private void ModelPathItem_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (_isDragging)
                {
                    _isDragging = false;
                    _draggedCheckBoxes.Clear();
                    _justFinishedDragging = true; // 标记刚完成拖动
                    Log.Information("🖱️ 拖动结束");
                }

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理 PointerReleased 失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 更新模型路径文本框
        /// Updates model path textbox with selected items
        /// </summary>
        private void UpdateModelPathComboBox()
        {
            var selectedPaths = new List<string>();
            var checkBoxes = FindVisualChildren<CheckBox>(ModelPathItemsControl);
            foreach (var checkBox in checkBoxes)
            {
                if (checkBox.IsChecked == true && checkBox.Tag is string path)
                {
                    selectedPaths.Add(path);
                }
            }
            ModelPathComboBox.Text = string.Join("; ", selectedPaths);
            SaveConfiguration();
        }

        /// <summary>
        /// 模型路径全选按钮点击事件
        /// Model path select all button click event
        /// </summary>
        private void ModelPathSelectAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var checkBoxes = FindVisualChildren<CheckBox>(ModelPathItemsControl);
                foreach (var checkBox in checkBoxes)
                {
                    checkBox.IsChecked = true;
                }
                Log.Information("模型路径已全选");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "全选模型路径失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 模型路径清空按钮点击事件
        /// Model path clear all button click event
        /// </summary>
        private void ModelPathClearAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var checkBoxes = FindVisualChildren<CheckBox>(ModelPathItemsControl);
                foreach (var checkBox in checkBoxes)
                {
                    checkBox.IsChecked = false;
                }
                Log.Information("模型路径已清空");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "清空模型路径失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 模型路径删除按钮点击事件
        /// Model path delete button click event - removes from history only, does not delete files
        /// </summary>
        private async void ModelPathDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedPaths = new List<string>();
                var checkBoxes = FindVisualChildren<CheckBox>(ModelPathItemsControl);
                foreach (var checkBox in checkBoxes)
                {
                    if (checkBox.IsChecked == true && checkBox.Tag is string path)
                    {
                        selectedPaths.Add(path);
                    }
                }

                if (selectedPaths.Count == 0)
                {
                    await ShowErrorDialogAsync("提示", "请先选择要删除的历史记录");
                    return;
                }

                var dialog = new ContentDialog
                {
                    Title = "确认删除",
                    Content = $"确定要从历史记录中删除 {selectedPaths.Count} 个选中的项目吗？\n\n注意：这只会删除历史记录，不会删除实际文件。",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    var config = App.AppConfig.LaunchSettings.LlamaService;
                    int removedCount = 0;
                    
                    foreach (var path in selectedPaths)
                    {
                        if (config.ModelPathHistory.Contains(path))
                        {
                            config.ModelPathHistory.Remove(path);
                            removedCount++;
                            Log.Information("已从历史记录中删除模型路径: {Path}", path);
                        }
                    }

                    // 如果当前输入框中的路径被删除了，清空输入框
                    if (selectedPaths.Contains(ModelPathComboBox.Text))
                    {
                        ModelPathComboBox.Text = "";
                        Log.Information("当前模型路径已被删除，已清空输入框");
                    }

                    // 保存配置
                    App.ConfigManager.SaveConfig(App.AppConfig);
                    
                    // 刷新模型列表
                    RefreshModelList();
                    
                    // 关闭下拉栏
                    ModelPathComboBox.IsOpen = false;
                    
                    await ShowSuccessDialogAsync("完成", $"成功从历史记录中删除 {removedCount} 个项目");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "删除历史记录失败: {Message}", ex.Message);
                await ShowErrorDialogAsync("错误", $"删除失败：\n{ex.Message}");
            }
        }

        /// <summary>
        /// 预设项单击事件 - 立即切换勾选状态
        /// Preset item tapped event - immediately toggle checkbox state
        /// </summary>
        private void PresetItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                // 立即标记事件已处理，防止冒泡
                e.Handled = true;
                
                // 如果刚完成拖动，忽略 Tapped 事件
                if (_justFinishedDragging)
                {
                    _justFinishedDragging = false;
                    Log.Information("🖱️ 忽略预设 Tapped（刚完成拖动）");
                    return;
                }
                
                if (sender is not Border border)
                {
                    return;
                }
                
                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null || checkBox.Tag is not string presetName)
                {
                    return;
                }

                // 获取点击位置
                var position = e.GetPosition(border);
                
                // CheckBox 通常在左侧，宽度约 40-50px
                bool clickedOnCheckBox = position.X < 50;
                
                if (clickedOnCheckBox)
                {
                    // 保存点击前的状态（用于双击恢复）
                    _lastCheckBoxState = checkBox.IsChecked == true;
                    _lastTappedCheckBox = checkBox;
                    
                    // 单击多选框：立即切换勾选状态
                    checkBox.IsChecked = !checkBox.IsChecked;
                    Log.Information("🖱️ 单击预设多选框: {Name}, 原状态: {OldState}, 新状态: {NewState}", presetName, _lastCheckBoxState, checkBox.IsChecked);
                }
                else
                {
                    // 单击文本：不做任何操作
                    Log.Information("🖱️ 单击预设文本: {Name}（无操作）", presetName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理预设单击事件失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 预设项双击事件 - 区分多选框和文本
        /// Preset item double tapped event - distinguish checkbox and text
        /// </summary>
        private void PresetItem_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            try
            {
                // 标记正在双击
                _isDoubleClicking = true;
                
                // 取消拖动状态
                if (_isDragging)
                {
                    _isDragging = false;
                    _draggedCheckBoxes.Clear();
                    Log.Information("🖱️ 双击预设时取消拖动状态");
                }
                
                if (sender is not Border border)
                {
                    _isDoubleClicking = false;
                    return;
                }
                
                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null || checkBox.Tag is not string presetName)
                {
                    _isDoubleClicking = false;
                    return;
                }

                // 获取点击位置
                var position = e.GetPosition(border);
                
                // CheckBox 通常在左侧，宽度约 40-50px
                bool clickedOnCheckBox = position.X < 50;
                
                if (clickedOnCheckBox)
                {
                    // 双击多选框：不做任何操作
                    // Tapped 事件已经处理了两次点击
                    Log.Information("🖱️ 双击预设多选框: {Name}（Tapped 已处理）", presetName);
                }
                else
                {
                    // 双击选项文本：加载预设
                    Log.Information("🖱️ 双击预设文本，加载: {Name}", presetName);
                    
                    // 清空所有复选框
                    var checkBoxes = FindVisualChildren<CheckBox>(PresetItemsControl);
                    foreach (var cb in checkBoxes)
                    {
                        cb.IsChecked = false;
                    }
                    
                    // 加载预设
                    LoadPresetByName(presetName);
                    
                    Log.Information("✅ 已加载预设: {Name}", presetName);
                }

                // 延迟重置双击标志
                _ = Task.Delay(100).ContinueWith(_ => 
                {
                    DispatcherQueue.TryEnqueue(() => _isDoubleClicking = false);
                });

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "双击加载预设失败: {Message}", ex.Message);
                _isDoubleClicking = false;
            }
        }

        private void PresetItem_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                // 如果正在双击，忽略 PointerPressed
                if (_isDoubleClicking)
                {
                    Log.Information("🖱️ 忽略预设 PointerPressed（正在双击）");
                    e.Handled = true;
                    return;
                }
                
                if (sender is not Border border) return;
                
                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null) return;

                // 取消 Tapped 的延迟任务（因为要开始拖动了）
                _tapDelayTimer?.Dispose();
                _tapDelayTimer = null;

                // 开始拖动
                _isDragging = true;
                _draggedCheckBoxes.Clear();
                
                // 确定拖动方向：如果当前未勾选，则拖动为勾选；如果已勾选，则拖动为取消勾选
                _dragToCheck = checkBox.IsChecked != true;
                
                // 设置起始项的状态
                checkBox.IsChecked = _dragToCheck;
                _draggedCheckBoxes.Add(checkBox);
                Log.Information("🖱️ 开始拖动预设，方向: {Direction}, 起始项: {Name}", _dragToCheck ? "勾选" : "取消勾选", checkBox.Tag);

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理预设 PointerPressed 失败: {Message}", ex.Message);
            }
        }

        private void PresetItem_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (!_isDragging) return;
                if (sender is not Border border) return;
                
                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null || _draggedCheckBoxes.Contains(checkBox)) return;

                // 使用统一的拖动方向设置状态
                checkBox.IsChecked = _dragToCheck;
                _draggedCheckBoxes.Add(checkBox);
                Log.Information("🖱️ 拖动预设经过，设置项: {Name}, 状态: {State}", checkBox.Tag, _dragToCheck);

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理预设 PointerMoved 失败: {Message}", ex.Message);
            }
        }

        private void PresetItem_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (_isDragging)
                {
                    _isDragging = false;
                    _draggedCheckBoxes.Clear();
                    _justFinishedDragging = true; // 标记刚完成拖动
                    Log.Information("🖱️ 预设拖动结束");
                }

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理预设 PointerReleased 失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 根据预设名称加载预设
        /// Load preset by name
        /// </summary>
        private void LoadPresetByName(string presetName)
        {
            // 整段在 _isApplyingPreset 守护下执行：沿途由 XAML 绑定的
            // ValueChanged/TextChanged 会尝试调用 SaveConfiguration()，在此期间
            // 那些调用会被早退，不会把 SelectedPreset 回写为空，也不会把
            // TextBox 的"半更新"中间态写入磁盘。
            // Wrap the whole method in _isApplyingPreset so intermediate
            // ValueChanged/TextChanged handlers (wired in XAML) can't call
            // SaveConfiguration() and wipe SelectedPreset or persist a
            // half-updated state to disk.
            _isApplyingPreset = true;
            try
            {
                var preset = App.AppConfig.LaunchSettings.LlamaService.Presets.Find(p => p.Name == presetName);
                if (preset == null)
                {
                    Log.Warning("未找到名为 {Name} 的预设", presetName);
                    return;
                }

                // 清空所有复选框
                var checkBoxes = FindVisualChildren<CheckBox>(PresetItemsControl);
                foreach (var cb in checkBoxes)
                {
                    cb.IsChecked = false;
                }
                
                // ---- Step 1: 把预设值刷到 UI (这些赋值会触发被 _isApplyingPreset 屏蔽的事件) ----
                // Push preset values to the UI controls. The event handlers
                // that normally call SaveConfiguration() early-return due to
                // the flag, so no partial/intermediate disk writes occur.
                ModelPathComboBox.Text = preset.ModelPath;
                HostComboBox.Text = preset.Host;
                PortTextBox.Text = preset.Port.ToString();
                GpuLayersSlider.Value = preset.GpuLayers;
                GpuLayersTextBox.Text = preset.GpuLayers.ToString();
                ContextLengthSlider.Value = preset.ContextLength;
                ContextLengthTextBox.Text = preset.ContextLength.ToString();
                ParallelThreadsSlider.Value = preset.ParallelThreads;
                ParallelThreadsTextBox.Text = preset.ParallelThreads.ToString();
                FlashAttentionCheckBox.IsChecked = preset.FlashAttention;
                NoMmapCheckBox.IsChecked = preset.NoMmap;
                LogFormatComboBox.Text = preset.LogFormat;
                SingleGpuCheckBox.IsChecked = preset.SingleGpuMode;
                
                // 根据索引设置GPU名称
                if (preset.GpuIndex >= 0 && GpuItemsControl.ItemsSource is List<string> gpuList && preset.GpuIndex < gpuList.Count)
                {
                    GpuComboBox.Text = gpuList[preset.GpuIndex];
                }
                
                ManualGpuIndexTextBox.Text = preset.ManualGpuIndex;
                CustomAppendTextBox.Text = preset.CustomCommandAppend;
                CustomCommandTextBox.Text = preset.CustomCommand;

                PresetComboBox.Text = presetName;
                
                // 关闭下拉框
                PresetComboBox.IsOpen = false;

                // ---- Step 2: 把预设值直接写进 AppConfig (不经过 SaveConfiguration，因为它此刻被屏蔽) ----
                // Directly sync preset values into AppConfig so that the single
                // canonical SaveConfig() below doesn't need SaveConfiguration's
                // TextBox-read path (which would also clobber SelectedPreset).
                var config = App.AppConfig.LaunchSettings.LlamaService;
                config.ModelPath = preset.ModelPath;
                config.Host = preset.Host;
                config.Port = preset.Port;
                config.GpuLayers = preset.GpuLayers;
                config.ContextLength = preset.ContextLength;
                config.ParallelThreads = preset.ParallelThreads;
                config.FlashAttention = preset.FlashAttention;
                config.NoMmap = preset.NoMmap;
                config.LogFormat = preset.LogFormat;
                config.SingleGpuMode = preset.SingleGpuMode;
                config.SelectedGpuIndex = preset.GpuIndex;
                config.ManualGpuIndex = preset.ManualGpuIndex;
                config.CustomCommandAppend = preset.CustomCommandAppend;
                config.CustomCommand = preset.CustomCommand;
                // SelectedPreset —— 这是本次修复的关键：关闭程序后下次启动时
                // RestoreSavedConfiguration 依据它调用 LoadPresetByName 还原预设。
                // Key field: RestoreSavedConfiguration reads this on next
                // launch and replays LoadPresetByName to re-apply the preset.
                config.SelectedPreset = presetName;

                // ---- Step 3: 一次性落盘 ----
                // Single authoritative save with the preset fully applied.
                App.ConfigManager.SaveConfig(App.AppConfig);
                Log.Information("✅ 已加载预设并保存到磁盘: {Name} (SelectedPreset={SelectedPreset})",
                    presetName, config.SelectedPreset);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载预设失败: {Message}", ex.Message);
            }
            finally
            {
                // 用 DispatcherQueue.TryEnqueue 延迟重置标志 —— WinUI 3 中
                // Slider.ValueChanged 等事件可能被延后到下一轮 UI 消息循环触发；
                // 如果我们在 finally 里立刻把 _isApplyingPreset 置回 false，
                // 那些"迟到的"事件仍会调用 SaveConfiguration 并把 SelectedPreset
                // 再次清成空字符串 —— 功亏一篑。
                // Defer the flag reset by one dispatcher tick: some
                // ValueChanged / TextChanged handlers in WinUI 3 can fire on
                // the next message-loop turn after the property assignment.
                // Resetting synchronously would let those late handlers call
                // SaveConfiguration() and silently wipe SelectedPreset again.
                if (DispatcherQueue != null)
                {
                    DispatcherQueue.TryEnqueue(() => _isApplyingPreset = false);
                }
                else
                {
                    _isApplyingPreset = false;
                }
            }
        }

        /// <summary>
        /// 更新预设文本框
        /// Updates preset textbox with selected items
        /// </summary>
        private void UpdatePresetComboBox()
        {
            var selectedPresets = new List<string>();
            var checkBoxes = FindVisualChildren<CheckBox>(PresetItemsControl);
            foreach (var checkBox in checkBoxes)
            {
                if (checkBox.IsChecked == true && checkBox.Tag is string preset)
                {
                    selectedPresets.Add(preset);
                }
            }
            PresetComboBox.Text = string.Join("; ", selectedPresets);
        }

        /// <summary>
        /// 预设全选按钮点击事件
        /// Preset select all button click event
        /// </summary>
        private void PresetSelectAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var checkBoxes = FindVisualChildren<CheckBox>(PresetItemsControl);
                foreach (var checkBox in checkBoxes)
                {
                    checkBox.IsChecked = true;
                }
                Log.Information("预设已全选");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "全选预设失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 预设清空按钮点击事件
        /// Preset clear all button click event
        /// </summary>
        private void PresetClearAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var checkBoxes = FindVisualChildren<CheckBox>(PresetItemsControl);
                foreach (var checkBox in checkBoxes)
                {
                    checkBox.IsChecked = false;
                }
                Log.Information("预设已清空");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "清空预设失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 预设删除按钮点击事件
        /// Preset delete button click event
        /// </summary>
        private async void PresetDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedPresets = new List<string>();
                var checkBoxes = FindVisualChildren<CheckBox>(PresetItemsControl);
                foreach (var checkBox in checkBoxes)
                {
                    if (checkBox.IsChecked == true && checkBox.Tag is string preset)
                    {
                        selectedPresets.Add(preset);
                    }
                }

                if (selectedPresets.Count == 0)
                {
                    await ShowErrorDialogAsync("提示", "请先选择要删除的预设");
                    return;
                }

                var dialog = new ContentDialog
                {
                    Title = "确认删除",
                    Content = $"确定要删除 {selectedPresets.Count} 个选中的预设吗？",
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    var presets = App.AppConfig.LaunchSettings.LlamaService.Presets;
                    foreach (var presetName in selectedPresets)
                    {
                        var preset = presets.Find(p => p.Name == presetName);
                        if (preset != null)
                        {
                            presets.Remove(preset);
                            Log.Information("已删除预设: {Name}", presetName);
                        }
                    }

                    // 如果当前输入框中的预设被删除了，清空输入框
                    if (selectedPresets.Contains(PresetComboBox.Text))
                    {
                        PresetComboBox.Text = "";
                        Log.Information("当前预设已被删除，已清空输入框");
                    }

                    App.ConfigManager.SaveConfig(App.AppConfig);
                    LoadPresets();
                    
                    // 关闭下拉栏
                    PresetComboBox.IsOpen = false;
                    
                    await ShowSuccessDialogAsync("完成", $"成功删除 {selectedPresets.Count} 个预设");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "删除预设失败: {Message}", ex.Message);
                await ShowErrorDialogAsync("错误", $"删除失败：\n{ex.Message}");
            }
        }

        /// <summary>
        /// 主机地址 RadioButton 选中事件
        /// Host RadioButton checked event
        /// </summary>
        private void HostRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is RadioButton radioButton && radioButton.Tag is string host)
                {
                    HostComboBox.Text = host;
                    Log.Information("用户选择了主机地址: {Host}", host);
                    SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "选择主机地址失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 在容器中查找 CheckBox 控件
        /// Finds CheckBox control in container
        /// </summary>
        private CheckBox? FindCheckBoxInContainer(object container)
        {
            if (container is CheckBox checkBox)
            {
                return checkBox;
            }

            if (container is Microsoft.UI.Xaml.Controls.ContentPresenter presenter)
            {
                return presenter.Content as CheckBox;
            }

            return null;
        }

        /// <summary>
        /// 查找可视化树中的所有指定类型的子元素
        /// Finds all children of specified type in visual tree
        /// </summary>
        private List<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            var children = new List<T>();
            if (parent == null) return children;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    children.Add(typedChild);
                }

                children.AddRange(FindVisualChildren<T>(child));
            }

            return children;
        }

        /// <summary>
        /// 预设项点击事件
        /// Preset item click event
        /// </summary>
        private void PresetItem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Content is string presetName)
                {
                    var preset = App.AppConfig.LaunchSettings.LlamaService.Presets.Find(p => p.Name == presetName);
                if (preset != null)
                {
                    // 加载预设
                    // Load preset
                        ModelPathComboBox.Text = preset.ModelPath;
                    HostComboBox.Text = preset.Host;
                    PortTextBox.Text = preset.Port.ToString();
                    GpuLayersSlider.Value = preset.GpuLayers;
                    GpuLayersTextBox.Text = preset.GpuLayers.ToString();
                    ContextLengthSlider.Value = preset.ContextLength;
                    ContextLengthTextBox.Text = preset.ContextLength.ToString();
                    ParallelThreadsSlider.Value = preset.ParallelThreads;
                    ParallelThreadsTextBox.Text = preset.ParallelThreads.ToString();
                    FlashAttentionCheckBox.IsChecked = preset.FlashAttention;
                    NoMmapCheckBox.IsChecked = preset.NoMmap;
                    LogFormatComboBox.Text = preset.LogFormat;
                    SingleGpuCheckBox.IsChecked = preset.SingleGpuMode;
                    
                    // 根据索引设置GPU名称
                    if (preset.GpuIndex >= 0 && GpuItemsControl.ItemsSource is List<string> gpuList && preset.GpuIndex < gpuList.Count)
                    {
                        GpuComboBox.Text = gpuList[preset.GpuIndex];
                    }
                    
                    ManualGpuIndexTextBox.Text = preset.ManualGpuIndex;
                    CustomAppendTextBox.Text = preset.CustomCommandAppend;
                    CustomCommandTextBox.Text = preset.CustomCommand;

                        PresetComboBox.Text = presetName;
                        var flyout = (Flyout)this.Resources["PresetFlyout"];
                        flyout.Hide();

                    Log.Information("预设已加载: {Name}", presetName);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载预设失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 服务状态改变事件处理
        /// Service status changed event handler
        /// </summary>
        private void OnServiceStatusChanged(object? sender, LlamaServiceStatus status)
        {
            // 如果页面已卸载，不处理事件
            // If page is unloaded, don't handle event
            if (_isUnloaded)
            {
                return;
            }

            try
        {
            // 在 UI 线程上更新状态
            // Update status on UI thread
                DispatcherQueue?.TryEnqueue(() =>
                {
                    // 再次检查页面是否已卸载
                    // Check again if page is unloaded
                    if (!_isUnloaded)
            {
                UpdateUIStatus(status);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "更新服务状态时发生错误: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 更新 UI 状态
        /// Updates UI status based on service status
        /// </summary>
        private void UpdateUIStatus(LlamaServiceStatus status)
        {
            string statusText = "";
            string statusColor = "";

            switch (status)
            {
                case LlamaServiceStatus.NotStarted:
                    statusText = "未启动";
                    statusColor = "Gray";
                    StatusText.Text = statusText;
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Gray);
                    StartButton.IsEnabled = true;
                    StopButton.IsEnabled = false;
                    RestartButton.IsEnabled = false;
                    break;

                case LlamaServiceStatus.Starting:
                    statusText = "正在启动";
                    statusColor = "Orange";
                    StatusText.Text = statusText;
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Orange);
                    StartButton.IsEnabled = false;
                    StopButton.IsEnabled = false;
                    RestartButton.IsEnabled = false;
                    break;

                case LlamaServiceStatus.Running:
                    statusText = "运行中";
                    statusColor = "Green";
                    StatusText.Text = statusText;
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Green);
                    StartButton.IsEnabled = false;
                    StopButton.IsEnabled = true;
                    RestartButton.IsEnabled = true;
                    
                    // 更新 API 地址到日志页面
                    // Update API address to log page
                    var config = App.AppConfig.LaunchSettings.LlamaService;
                    string apiAddress = $"http://{config.Host}:{config.Port}";
                    LlamaLogPageHelper.UpdateApiAddress(apiAddress);
                    break;

                case LlamaServiceStatus.Stopping:
                    statusText = "正在停止";
                    statusColor = "Orange";
                    StatusText.Text = statusText;
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Orange);
                    StartButton.IsEnabled = false;
                    StopButton.IsEnabled = false;
                    RestartButton.IsEnabled = false;
                    break;

                case LlamaServiceStatus.Stopped:
                    statusText = "已停止";
                    statusColor = "Gray";
                    StatusText.Text = statusText;
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Gray);
                    StartButton.IsEnabled = true;
                    StopButton.IsEnabled = false;
                    RestartButton.IsEnabled = false;
                    break;

                case LlamaServiceStatus.Error:
                    statusText = "错误";
                    statusColor = "Red";
                    StatusText.Text = statusText;
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Red);
                    StartButton.IsEnabled = true;
                    StopButton.IsEnabled = false;
                    RestartButton.IsEnabled = false;
                    break;
            }

            // 使用 Helper 更新日志页面状态（更可靠）
            // Use Helper to update log page status (more reliable)
            Log.Information("🔔 准备调用 LlamaLogPageHelper.UpdateServiceStatus: {StatusText}, {StatusColor}", statusText, statusColor);
            Log.Information("🔔 LlamaLogPageHelper.IsRegistered: {IsRegistered}", LlamaLogPageHelper.IsRegistered);
            
            LlamaLogPageHelper.UpdateServiceStatus(statusText, statusColor);
            
            Log.Information("🔔 LlamaLogPageHelper.UpdateServiceStatus 调用完成");
            Log.Debug("UI 状态已更新: {Status}", status);
        }

        /// <summary>
        /// 显示错误对话框
        /// Shows error dialog
        /// </summary>
        private async Task ShowErrorDialogAsync(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }

        /// <summary>
        /// 显示成功对话框
        /// Shows success dialog
        /// </summary>
        private async Task ShowSuccessDialogAsync(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "确定",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }

        /// <summary>
        /// 手动 GPU 索引文本改变事件 - 自动保存配置
        /// Manual GPU index text changed event - auto save configuration
        /// </summary>
        private void ManualGpuIndexTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (_isUnloaded) return;
                SaveConfiguration();
                Log.Debug("手动 GPU 索引已更改并保存: {Index}", ManualGpuIndexTextBox.Text);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存手动 GPU 索引配置失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 刷新 GPU 按钮点击事件
        /// Refresh GPU button click event
        /// </summary>
        private void RefreshGpuButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log.Information("用户点击「刷新 GPU」按钮");
                RefreshGpuList();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "刷新 GPU 列表失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 自定义追加命令文本改变事件 - 自动保存配置
        /// Custom append command text changed event - auto save configuration
        /// </summary>
        private void CustomAppendTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (_isUnloaded) return;
                SaveConfiguration();
                Log.Debug("自定义追加命令已更改并保存: {Command}", CustomAppendTextBox.Text);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存自定义追加命令配置失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 自定义命令文本改变事件 - 自动保存配置
        /// Custom command text changed event - auto save configuration
        /// </summary>
        private void CustomCommandTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (_isUnloaded) return;
                SaveConfiguration();
                Log.Debug("自定义命令已更改并保存: {Command}", CustomCommandTextBox.Text);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存自定义命令配置失败: {Message}", ex.Message);
            }
        }

        #region Host 主机地址相关事件处理

        /// <summary>
        /// 主机地址项单击事件 - 延迟300ms执行，避免与双击冲突
        /// Host item tapped event - delayed 300ms to avoid conflict with double tap
        /// </summary>
        private void HostItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                // 立即标记事件已处理，防止冒泡
                e.Handled = true;
                
                // 如果刚完成拖动，忽略 Tapped 事件
                if (_justFinishedDragging)
                {
                    _justFinishedDragging = false;
                    Log.Information("🖱️ 忽略主机 Tapped（刚完成拖动）");
                    return;
                }
                
                if (sender is not Border border)
                {
                    return;
                }
                
                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null || checkBox.Tag is not string hostAddress)
                {
                    return;
                }

                // 获取点击位置
                var position = e.GetPosition(border);
                
                // 判断是否点击在多选框区域（左侧约50px）
                if (position.X < 50)
                {
                    // 点击多选框：立即切换状态
                    _lastCheckBoxState = checkBox.IsChecked == true; // 保存初始状态
                    checkBox.IsChecked = !checkBox.IsChecked;
                    Log.Information("🖱️ 单击主机多选框，切换状态: {Host}, 新状态: {State}", hostAddress, checkBox.IsChecked);
                }
                // 点击文本区域不做任何操作（由 DoubleTapped 处理）
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理主机地址单击事件失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 主机地址项双击事件 - 直接确认选择
        /// Host item double tapped event - directly confirm selection
        /// </summary>
        private void HostItem_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            try
            {
                // 取消拖动状态
                if (_isDragging)
                {
                    _isDragging = false;
                    _draggedCheckBoxes.Clear();
                    Log.Information("🖱️ 双击主机时取消拖动状态");
                }
                
                if (sender is not Border border)
                {
                    return;
                }
                
                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null || checkBox.Tag is not string hostAddress)
                {
                    return;
                }

                // 获取点击位置
                var position = e.GetPosition(border);
                
                // 判断是否点击在文本区域（右侧，X >= 50）
                if (position.X >= 50)
                {
                    // 双击选项文本：选取该主机地址
                    Log.Information("🖱️ 双击主机文本，选取: {Host}", hostAddress);
                    
                    // 清空所有复选框
                    var checkBoxes = FindVisualChildren<CheckBox>(HostItemsControl);
                    foreach (var cb in checkBoxes)
                    {
                        cb.IsChecked = false;
                    }
                    
                    // 设置主机地址并关闭下拉框
                    HostComboBox.Text = hostAddress;
                    SaveConfiguration();
                    HostComboBox.IsOpen = false;
                    
                    Log.Information("✅ 已选取主机地址: {Host}", hostAddress);
                }
                // 双击多选框区域（X < 50）不做任何操作，让两次 Tapped 自然执行

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "双击选择主机地址失败: {Message}", ex.Message);
            }
        }

        private void HostItem_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (sender is not Border border) return;
                
                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null) return;

                // 开始拖动
                _isDragging = true;
                _draggedCheckBoxes.Clear();
                
                // 确定拖动方向：如果当前未勾选，则拖动为勾选；如果已勾选，则拖动为取消勾选
                _dragToCheck = checkBox.IsChecked != true;
                
                // 设置起始项的状态
                checkBox.IsChecked = _dragToCheck;
                _draggedCheckBoxes.Add(checkBox);
                Log.Information("🖱️ 开始拖动主机，方向: {Direction}, 起始项: {Host}", _dragToCheck ? "勾选" : "取消勾选", checkBox.Tag);

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理主机 PointerPressed 失败: {Message}", ex.Message);
            }
        }

        private void HostItem_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (!_isDragging) return;
                if (sender is not Border border) return;
                
                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null || _draggedCheckBoxes.Contains(checkBox)) return;

                // 使用统一的拖动方向设置状态
                checkBox.IsChecked = _dragToCheck;
                _draggedCheckBoxes.Add(checkBox);
                Log.Information("🖱️ 拖动主机经过，设置项: {Host}, 状态: {State}", checkBox.Tag, _dragToCheck);

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理主机 PointerMoved 失败: {Message}", ex.Message);
            }
        }

        private void HostItem_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (_isDragging)
                {
                    _isDragging = false;
                    _draggedCheckBoxes.Clear();
                    _justFinishedDragging = true; // 标记刚完成拖动
                    Log.Information("🖱️ 主机拖动结束");
                }

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理主机 PointerReleased 失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 更新主机地址文本框
        /// Updates host textbox with selected items
        /// </summary>
        private void UpdateHostComboBox()
        {
            var selectedHosts = new List<string>();
            var checkBoxes = FindVisualChildren<CheckBox>(HostItemsControl);
            foreach (var checkBox in checkBoxes)
            {
                if (checkBox.IsChecked == true && checkBox.Tag is string host)
                {
                    selectedHosts.Add(host);
                }
            }
            HostComboBox.Text = string.Join("; ", selectedHosts);
        }

        /// <summary>
        /// 主机地址全选按钮点击事件
        /// Host select all button click event
        /// </summary>
        private void HostSelectAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var checkBoxes = FindVisualChildren<CheckBox>(HostItemsControl);
                foreach (var checkBox in checkBoxes)
                {
                    checkBox.IsChecked = true;
                }
                Log.Information("主机地址已全选");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "全选主机地址失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 主机地址清空按钮点击事件
        /// Host clear all button click event
        /// </summary>
        private void HostClearAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var checkBoxes = FindVisualChildren<CheckBox>(HostItemsControl);
                foreach (var checkBox in checkBoxes)
                {
                    checkBox.IsChecked = false;
                }
                Log.Information("主机地址已清空");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "清空主机地址失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 主机地址删除按钮点击事件
        /// Host delete button click event
        /// </summary>
        private async void HostDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedHosts = new List<string>();
                var checkBoxes = FindVisualChildren<CheckBox>(HostItemsControl);
                foreach (var checkBox in checkBoxes)
                {
                    if (checkBox.IsChecked == true && checkBox.Tag is string host)
                    {
                        selectedHosts.Add(host);
                    }
                }

                if (selectedHosts.Count == 0)
                {
                    await ShowErrorDialogAsync("提示", "请先选择要删除的主机地址");
                    return;
                }

                // 检查是否包含默认地址
                var defaultHosts = new List<string> { "127.0.0.1", "0.0.0.0", "localhost" };
                var defaultHostsSelected = selectedHosts.Where(h => defaultHosts.Contains(h)).ToList();
                var customHostsSelected = selectedHosts.Where(h => !defaultHosts.Contains(h)).ToList();

                if (defaultHostsSelected.Count > 0 && customHostsSelected.Count == 0)
                {
                    // 关闭下拉栏（特例：删除默认地址失败也要关闭）
                    HostComboBox.IsOpen = false;
                    
                    await ShowErrorDialogAsync("提示", "默认主机地址（127.0.0.1、0.0.0.0、localhost）无法删除");
                    return;
                }

                string message = customHostsSelected.Count > 0
                    ? $"确定要删除 {customHostsSelected.Count} 个选中的主机地址吗？"
                    : "选中的都是默认地址，无法删除";

                if (defaultHostsSelected.Count > 0)
                {
                    message += $"\n\n注意：默认地址（{string.Join("、", defaultHostsSelected)}）将被忽略";
                }

                var dialog = new ContentDialog
                {
                    Title = "确认删除",
                    Content = message,
                    PrimaryButtonText = "删除",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = this.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary && customHostsSelected.Count > 0)
                {
                    var config = App.AppConfig.LaunchSettings.LlamaService;
                    
                    // 检查是否有 HostHistory 属性
                    var hostHistoryProperty = config.GetType().GetProperty("HostHistory");
                    if (hostHistoryProperty == null || hostHistoryProperty.GetValue(config) is not List<string> hostHistory)
                    {
                        // 主机地址历史记录不存在，无法删除
                        await ShowErrorDialogAsync("提示", "主机地址历史记录不存在");
                        return;
                    }
                    
                    int removedCount = 0;
                    foreach (var host in customHostsSelected)
                    {
                        if (hostHistory.Contains(host))
                        {
                            hostHistory.Remove(host);
                            removedCount++;
                            Log.Information("已从历史记录中删除主机地址: {Host}", host);
                        }
                    }

                    // 如果当前输入框中的地址被删除了，清空输入框
                    if (customHostsSelected.Contains(HostComboBox.Text))
                    {
                        HostComboBox.Text = "127.0.0.1"; // 恢复为默认值
                        Log.Information("当前主机地址已被删除，恢复为默认值: 127.0.0.1");
                    }

                    // 保存配置
                    App.ConfigManager.SaveConfig(App.AppConfig);
                    
                    // 刷新主机地址列表（重要：必须刷新才能看到删除效果）
                    RefreshHostList();
                    
                    // 关闭下拉栏
                    HostComboBox.IsOpen = false;
                    
                    await ShowSuccessDialogAsync("完成", $"成功删除 {removedCount} 个主机地址");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "删除主机地址失败: {Message}", ex.Message);
                await ShowErrorDialogAsync("错误", $"删除失败：\n{ex.Message}");
            }
        }

        /// <summary>
        /// 保存主机地址按钮点击事件
        /// Save host button click event
        /// </summary>
        private async void SaveHostButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string hostAddress = HostComboBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(hostAddress))
                {
                    await ShowErrorDialogAsync("错误", "请输入主机地址");
                    return;
                }

                var config = App.AppConfig.LaunchSettings.LlamaService;
                
                // 获取或创建 HostHistory 属性
                var hostHistoryProperty = config.GetType().GetProperty("HostHistory");
                List<string> hostHistory;
                
                if (hostHistoryProperty == null)
                {
                    // 如果属性不存在，需要在 AppConfig 中添加该属性
                    await ShowErrorDialogAsync("错误", "配置文件不支持主机地址历史记录功能");
                    return;
                }
                
                if (hostHistoryProperty.GetValue(config) is not List<string> existingHistory)
                {
                    // 创建新的历史记录列表
                    hostHistory = new List<string>();
                    hostHistoryProperty.SetValue(config, hostHistory);
                }
                else
                {
                    hostHistory = existingHistory;
                }
                
                // 检查是否已存在
                if (hostHistory.Contains(hostAddress))
                {
                    await ShowErrorDialogAsync("提示", $"主机地址 \"{hostAddress}\" 已存在于历史记录中");
                    return;
                }
                
                // 添加到历史记录
                hostHistory.Add(hostAddress);
                
                // 保存配置
                App.ConfigManager.SaveConfig(App.AppConfig);
                
                // 刷新主机地址列表
                RefreshHostList();
                
                await ShowSuccessDialogAsync("成功", $"主机地址 \"{hostAddress}\" 已保存到历史记录");
                Log.Information("主机地址已保存: {Host}", hostAddress);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存主机地址失败: {Message}", ex.Message);
                await ShowErrorDialogAsync("错误", $"保存失败：\n{ex.Message}");
            }
        }

        /// <summary>
        /// 刷新主机地址列表
        /// Refresh host list
        /// </summary>
        private void RefreshHostList()
        {
            try
            {
                var config = App.AppConfig.LaunchSettings.LlamaService;
                
                // 默认主机地址列表
                var defaultHosts = new List<string>
                {
                    "127.0.0.1",
                    "0.0.0.0",
                    "localhost"
                };
                
                // 如果配置中有 HostHistory，合并到列表中
                if (config.GetType().GetProperty("HostHistory")?.GetValue(config) is List<string> hostHistory)
                {
                    foreach (var host in hostHistory)
                    {
                        if (!defaultHosts.Contains(host))
                        {
                            defaultHosts.Add(host);
                        }
                    }
                }
                
                // 强制刷新UI
                HostItemsControl.ItemsSource = null;
                HostItemsControl.ItemsSource = defaultHosts;
                Log.Information("主机地址列表已刷新，共 {Count} 项", defaultHosts.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "刷新主机地址列表失败: {Message}", ex.Message);
            }
        }

        #endregion

        #region LogFormat 日志格式相关事件处理

        /// <summary>
        /// 日志格式项单击事件 - 智能处理：未勾选则勾选，已勾选则确认
        /// Log format item tapped event - smart handling: check if unchecked, confirm if checked
        /// </summary>
        private void LogFormatItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                // 从 Border 中获取数据
                if (sender is Border border && border.DataContext is string logFormat)
                {
                    Log.Information("🖱️ 点击选择日志格式: {LogFormat}", logFormat);
                    
                    // 设置日志格式
                    LogFormatComboBox.Text = logFormat;
                    
                    // 保存配置
                    SaveConfiguration();
                    
                    // 关闭下拉栏
                    LogFormatComboBox.IsOpen = false;
                    
                    Log.Information("✅ 已确认选择日志格式: {LogFormat}", logFormat);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理日志格式单击事件失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 日志格式项双击事件 - 直接确认选择
        /// Log format item double tapped event - directly confirm selection
        /// </summary>
        private void LogFormatItem_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            try
            {
                Log.Information("🖱️ 日志格式 DoubleTapped 事件触发");
                
                // 从 Border 中获取数据
                if (sender is Border border && border.DataContext is string logFormat)
                {
                    Log.Information("🖱️ 双击确认选择: {LogFormat}", logFormat);
                    
                    // 设置日志格式
                    LogFormatComboBox.Text = logFormat;
                    
                    // 保存配置
                    SaveConfiguration();
                    
                    // 关闭下拉栏
                    LogFormatComboBox.IsOpen = false;
                    
                    Log.Information("✅ 双击已确认选择日志格式: {LogFormat}", logFormat);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "双击选择日志格式失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 刷新日志格式列表
        /// Refresh log format list
        /// </summary>
        private void RefreshLogFormatList()
        {
            try
            {
                // 默认日志格式列表
                var logFormats = new List<string>
                {
                    "none",
                    "text",
                    "json"
                };
                
                LogFormatItemsControl.ItemsSource = logFormats;
                Log.Information("日志格式列表已刷新，共 {Count} 项", logFormats.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "刷新日志格式列表失败: {Message}", ex.Message);
            }
        }

        #endregion

        #region Port 端口相关事件处理

        /// <summary>
        /// 播放端口输入框阴影动画
        /// </summary>
        private void AnimatePortShadow(bool fadeIn)
        {
            if (_portShadowBorder == null || _portDropShadow == null || _portShadowVisual == null)
            {
                return;
            }

            try
            {
                var compositor = _portDropShadow.Compositor;

                // 创建 Border Opacity 动画
                var borderOpacityAnimation = compositor.CreateScalarKeyFrameAnimation();
                borderOpacityAnimation.Duration = TimeSpan.FromMilliseconds(300);

                // 创建 DropShadow BlurRadius 动画
                var blurAnimation = compositor.CreateScalarKeyFrameAnimation();
                blurAnimation.Duration = TimeSpan.FromMilliseconds(300);

                if (fadeIn)
                {
                    // 淡入：Border Opacity 从 0 增加到 0.15
                    borderOpacityAnimation.InsertKeyFrame(0.0f, 0f);
                    borderOpacityAnimation.InsertKeyFrame(1.0f, 0.15f);

                    // 淡入：BlurRadius 从 0 增加到 12
                    blurAnimation.InsertKeyFrame(0.0f, 0f);
                    blurAnimation.InsertKeyFrame(1.0f, 12f);

                    Log.Debug("端口阴影淡入动画：Opacity 0→0.15, BlurRadius 0→12");
                }
                else
                {
                    // 淡出：Border Opacity 从 0.15 恢复到 0
                    borderOpacityAnimation.InsertKeyFrame(0.0f, 0.15f);
                    borderOpacityAnimation.InsertKeyFrame(1.0f, 0f);

                    // 淡出：BlurRadius 从 12 恢复到 0
                    blurAnimation.InsertKeyFrame(0.0f, 12f);
                    blurAnimation.InsertKeyFrame(1.0f, 0f);

                    Log.Debug("端口阴影淡出动画：Opacity 0.15→0, BlurRadius 12→0");
                }

                // 启动动画
                _portShadowVisual.StartAnimation("Opacity", borderOpacityAnimation);
                _portDropShadow.StartAnimation("BlurRadius", blurAnimation);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "端口阴影动画失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 播放端口按钮按压动画
        /// </summary>
        /// <param name="button">按钮对象</param>
        /// <param name="isPressed">是否按下</param>
        /// <param name="isUpButton">是否是上按钮（上按钮动画方向相反）</param>
        private void AnimatePortButton(ButtonBase button, bool isPressed, bool isUpButton)
        {
            if (button == null)
            {
                Log.Warning("AnimatePortButton: button 为 null");
                return;
            }

            try
            {
                // 获取 RenderTransform（XAML中已预设）
                if (button.RenderTransform is not CompositeTransform transform)
                {
                    Log.Warning("AnimatePortButton: RenderTransform 不是 CompositeTransform 类型，按钮名称: {Name}", button.Name);
                    return;
                }
                
                Log.Information("🎬 开始播放端口按钮动画: {ButtonName}, 按下={IsPressed}, 上按钮={IsUpButton}", 
                    button.Name, isPressed, isUpButton);
                
                // 创建 Storyboard 动画
                var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                
                if (isPressed)
                {
                    // 按下：下按钮向下移动 1.5px，上按钮向上移动 -1.5px
                    var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                    {
                        Duration = new Duration(TimeSpan.FromMilliseconds(100)),
                        To = isUpButton ? -1.5 : 1.5,
                        EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase()
                    };
                    
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, transform);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "TranslateY");
                    
                    storyboard.Children.Add(animation);
                    storyboard.Begin();
                    
                    Log.Information("✅ 按下动画已启动: 移动到 {To}px", animation.To);
                    
                    // 同时播放阴影淡入动画
                    AnimatePortShadow(true);
                }
                else
                {
                    // 释放：回到原位（使用关键帧动画实现弹跳效果）
                    var keyFrameAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimationUsingKeyFrames
                    {
                        Duration = new Duration(TimeSpan.FromMilliseconds(200))
                    };
                    
                    // 中间帧：下按钮向上 -0.8px，上按钮向下 0.8px（反弹效果）
                    var keyFrame1 = new Microsoft.UI.Xaml.Media.Animation.EasingDoubleKeyFrame
                    {
                        KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
                        Value = isUpButton ? 0.8 : -0.8
                    };
                    
                    // 结束帧：复位到 0
                    var keyFrame2 = new Microsoft.UI.Xaml.Media.Animation.EasingDoubleKeyFrame
                    {
                        KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)),
                        Value = 0
                    };
                    
                    keyFrameAnimation.KeyFrames.Add(keyFrame1);
                    keyFrameAnimation.KeyFrames.Add(keyFrame2);
                    
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(keyFrameAnimation, transform);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(keyFrameAnimation, "TranslateY");
                    
                    storyboard.Children.Add(keyFrameAnimation);
                    storyboard.Begin();
                    
                    Log.Information("✅ 释放动画已启动: 反弹到 {Bounce}px 然后复位", keyFrame1.Value);
                    
                    // 同时播放阴影淡出动画
                    AnimatePortShadow(false);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "端口按钮按压动画失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 端口上按钮按下事件
        /// </summary>
        private void PortUpButton_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ PortUpButton_PointerPressed 事件触发！");
            
            if (sender is Button button)
            {
                AnimatePortButton(button, true, true);
            }
        }

        /// <summary>
        /// 端口上按钮释放事件
        /// </summary>
        private void PortUpButton_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ PortUpButton_PointerReleased 事件触发！");
            
            if (sender is Button button)
            {
                AnimatePortButton(button, false, true);
            }
        }

        /// <summary>
        /// 端口下按钮按下事件
        /// </summary>
        private void PortDownButton_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ PortDownButton_PointerPressed 事件触发！");
            
            if (sender is Button button)
            {
                AnimatePortButton(button, true, false);
            }
        }

        /// <summary>
        /// 端口下按钮释放事件
        /// </summary>
        private void PortDownButton_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ PortDownButton_PointerReleased 事件触发！");
            
            if (sender is Button button)
            {
                AnimatePortButton(button, false, false);
            }
        }

        /// <summary>
        /// 端口上按钮点击事件 - 数值加1
        /// Port up button click event - increment value by 1
        /// </summary>
        private void PortUpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (int.TryParse(PortTextBox.Text, out int currentPort))
                {
                    if (currentPort < 65535)
                    {
                        PortTextBox.Text = (currentPort + 1).ToString();
                        SaveConfiguration();
                        Log.Debug("端口数值已增加: {Port}", currentPort + 1);
                    }
                }
                else
                {
                    // 如果解析失败，设置为默认值
                    PortTextBox.Text = "8080";
                    SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "端口数值增加失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 端口下按钮点击事件 - 数值减1
        /// Port down button click event - decrement value by 1
        /// </summary>
        private void PortDownButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (int.TryParse(PortTextBox.Text, out int currentPort))
                {
                    if (currentPort > 1)
                    {
                        PortTextBox.Text = (currentPort - 1).ToString();
                        SaveConfiguration();
                        Log.Debug("端口数值已减少: {Port}", currentPort - 1);
                    }
                }
                else
                {
                    // 如果解析失败，设置为默认值
                    PortTextBox.Text = "8080";
                    SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "端口数值减少失败: {Message}", ex.Message);
            }
        }

        #endregion

        #region GpuLayers GPU层数相关事件处理

        /// <summary>
        /// GPU层数滑块值变化事件 - 同步到文本框
        /// </summary>
        private void GpuLayersSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            try
            {
                if (GpuLayersTextBox != null)
                {
                    GpuLayersTextBox.Text = ((int)e.NewValue).ToString();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GPU层数滑块值变化失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// GPU层数文本框文本变化事件 - 同步到滑块
        /// </summary>
        private void GpuLayersTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (int.TryParse(GpuLayersTextBox.Text, out int value))
                {
                    if (value >= 0 && value <= 200)
                    {
                        GpuLayersSlider.Value = value;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GPU层数文本框文本变化失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// GPU层数上按钮按下事件
        /// </summary>
        private void GpuLayersUpButton_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ GpuLayersUpButton_PointerPressed 事件触发！");
            
            if (sender is Button button)
            {
                AnimatePortButton(button, true, true);
            }
        }

        /// <summary>
        /// GPU层数上按钮释放事件
        /// </summary>
        private void GpuLayersUpButton_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ GpuLayersUpButton_PointerReleased 事件触发！");
            
            if (sender is Button button)
            {
                AnimatePortButton(button, false, true);
            }
        }

        /// <summary>
        /// GPU层数下按钮按下事件
        /// </summary>
        private void GpuLayersDownButton_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ GpuLayersDownButton_PointerPressed 事件触发！");
            
            if (sender is Button button)
            {
                AnimatePortButton(button, true, false);
            }
        }

        /// <summary>
        /// GPU层数下按钮释放事件
        /// </summary>
        private void GpuLayersDownButton_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ GpuLayersDownButton_PointerReleased 事件触发！");
            
            if (sender is Button button)
            {
                AnimatePortButton(button, false, false);
            }
        }

        /// <summary>
        /// GPU层数上按钮点击事件 - 数值加1
        /// </summary>
        private void GpuLayersUpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (int.TryParse(GpuLayersTextBox.Text, out int currentValue))
                {
                    if (currentValue < 200)
                    {
                        GpuLayersTextBox.Text = (currentValue + 1).ToString();
                        SaveConfiguration();
                        Log.Debug("GPU层数已增加: {Value}", currentValue + 1);
                    }
                }
                else
                {
                    GpuLayersTextBox.Text = "99";
                    SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GPU层数增加失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// GPU层数下按钮点击事件 - 数值减1
        /// </summary>
        private void GpuLayersDownButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (int.TryParse(GpuLayersTextBox.Text, out int currentValue))
                {
                    if (currentValue > 0)
                    {
                        GpuLayersTextBox.Text = (currentValue - 1).ToString();
                        SaveConfiguration();
                        Log.Debug("GPU层数已减少: {Value}", currentValue - 1);
                    }
                }
                else
                {
                    GpuLayersTextBox.Text = "99";
                    SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GPU层数减少失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 上下文长度滑块变化事件
        /// </summary>
        private void ContextLengthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            try
            {
                if (ContextLengthTextBox != null)
                {
                    ContextLengthTextBox.Text = ((int)e.NewValue).ToString();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "上下文长度滑块变化处理失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 上下文长度文本框变化事件
        /// </summary>
        private void ContextLengthTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (int.TryParse(ContextLengthTextBox.Text, out int value))
                {
                    if (value >= 256 && value <= 131072)
                    {
                        ContextLengthSlider.Value = value;
                        SaveConfiguration();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "上下文长度文本框变化处理失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 上下文长度上按钮点击事件
        /// </summary>
        private void ContextLengthUpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (int.TryParse(ContextLengthTextBox.Text, out int currentValue))
                {
                    if (currentValue < 131072)
                    {
                        int newValue = currentValue + 256;
                        if (newValue > 131072) newValue = 131072;
                        ContextLengthTextBox.Text = newValue.ToString();
                        SaveConfiguration();
                        Log.Debug("上下文长度已增加: {Value}", newValue);
                    }
                }
                else
                {
                    ContextLengthTextBox.Text = "2048";
                    SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "上下文长度增加失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 上下文长度下按钮点击事件
        /// </summary>
        private void ContextLengthDownButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (int.TryParse(ContextLengthTextBox.Text, out int currentValue))
                {
                    if (currentValue > 256)
                    {
                        int newValue = currentValue - 256;
                        if (newValue < 256) newValue = 256;
                        ContextLengthTextBox.Text = newValue.ToString();
                        SaveConfiguration();
                        Log.Debug("上下文长度已减少: {Value}", newValue);
                    }
                }
                else
                {
                    ContextLengthTextBox.Text = "2048";
                    SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "上下文长度减少失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 并行线程数文本框变化事件
        /// </summary>
        private void ParallelThreadsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                if (int.TryParse(ParallelThreadsTextBox.Text, out int value))
                {
                    if (value >= 1 && value <= 32)
                    {
                        ParallelThreadsSlider.Value = value;
                        SaveConfiguration();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "并行线程数文本框变化处理失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 并行线程数上按钮点击事件
        /// </summary>
        private void ParallelThreadsUpButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (int.TryParse(ParallelThreadsTextBox.Text, out int currentValue))
                {
                    if (currentValue < 32)
                    {
                        ParallelThreadsTextBox.Text = (currentValue + 1).ToString();
                        SaveConfiguration();
                        Log.Debug("并行线程数已增加: {Value}", currentValue + 1);
                    }
                }
                else
                {
                    ParallelThreadsTextBox.Text = "1";
                    SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "并行线程数增加失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 并行线程数下按钮点击事件
        /// </summary>
        private void ParallelThreadsDownButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (int.TryParse(ParallelThreadsTextBox.Text, out int currentValue))
                {
                    if (currentValue > 1)
                    {
                        ParallelThreadsTextBox.Text = (currentValue - 1).ToString();
                        SaveConfiguration();
                        Log.Debug("并行线程数已减少: {Value}", currentValue - 1);
                    }
                }
                else
                {
                    ParallelThreadsTextBox.Text = "1";
                    SaveConfiguration();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "并行线程数减少失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 并行线程数滑块变化事件
        /// </summary>
        private void ParallelThreadsSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            try
            {
                if (ParallelThreadsTextBox != null)
                {
                    ParallelThreadsTextBox.Text = ((int)e.NewValue).ToString();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "并行线程数滑块变化处理失败: {Message}", ex.Message);
            }
        }

        #endregion

        #region Gpu GPU选择相关事件处理

        /// <summary>
        /// GPU项单击事件 - 选择GPU
        /// </summary>
        private void GpuItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                if (sender is Border border && border.DataContext is string gpuName)
                {
                    Log.Information("🖱️ 点击选择GPU: {Gpu}", gpuName);
                    
                    // 设置GPU
                    GpuComboBox.Text = gpuName;
                    
                    // 保存配置
                    SaveConfiguration();
                    
                    // 关闭下拉栏
                    GpuComboBox.IsOpen = false;
                    
                    Log.Information("✅ 已确认选择GPU: {Gpu}", gpuName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理GPU单击事件失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// GPU项双击事件 - 直接确认选择
        /// </summary>
        private void GpuItem_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            try
            {
                Log.Information("🖱️ GPU DoubleTapped 事件触发");
                
                if (sender is Border border && border.DataContext is string gpuName)
                {
                    Log.Information("🖱️ 双击确认选择: {Gpu}", gpuName);
                    
                    // 设置GPU
                    GpuComboBox.Text = gpuName;
                    
                    // 保存配置
                    SaveConfiguration();
                    
                    // 关闭下拉栏
                    GpuComboBox.IsOpen = false;
                    
                    Log.Information("✅ 双击已确认选择GPU: {Gpu}", gpuName);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "双击选择GPU失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 根据GPU名称获取索引
        /// </summary>
        private int GetGpuIndexByName(string gpuName)
        {
            try
            {
                if (string.IsNullOrEmpty(gpuName))
                    return -1;
                
                var gpuList = GpuItemsControl.ItemsSource as List<string>;
                if (gpuList != null)
                {
                    return gpuList.IndexOf(gpuName);
                }
                
                return -1;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "获取GPU索引失败: {Message}", ex.Message);
                return -1;
            }
        }

        #endregion

        #region 页面交互事件处理

        /// <summary>
        /// 页面点击事件 - 点击输入框外部时让输入框失去焦点
        /// </summary>
        private void Page_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                // 获取点击的元素
                var tappedElement = e.OriginalSource as FrameworkElement;
                
                // 检查是否点击了输入框或其内部元素
                bool isPortTextBox = IsElementOrParent(tappedElement, PortTextBox);
                bool isGpuLayersTextBox = IsElementOrParent(tappedElement, GpuLayersTextBox);
                
                // 如果点击的不是输入框，则让输入框失去焦点
                if (!isPortTextBox && !isGpuLayersTextBox)
                {
                    // 将焦点设置到页面上，这样输入框就会失去焦点
                    this.Focus(FocusState.Programmatic);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理页面点击事件失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 检查元素是否是目标元素或其子元素
        /// </summary>
        private bool IsElementOrParent(FrameworkElement? element, FrameworkElement target)
        {
            while (element != null)
            {
                if (element == target)
                    return true;
                element = element.Parent as FrameworkElement;
            }
            return false;
        }

        /// <summary>
        /// 列表项鼠标进入事件 - 显示悬停效果
        /// </summary>
        private void ItemBorder_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (sender is Border border)
                {
                    // 使用与下拉栏背景相同色调的深灰色 (#E5E5E5)
                    border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 229, 229, 229));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理鼠标进入事件失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 列表项鼠标离开事件 - 恢复默认背景
        /// </summary>
        private void ItemBorder_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (sender is Border border)
                {
                    // 恢复透明背景
                    border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理鼠标离开事件失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 参数信息输出栏内部的 ScrollViewer 缓存。
        /// Cached reference to the inner ScrollViewer of <see cref="ParamsOutputBox"/>.
        /// 该 TextBox 使用了自定义 ControlTemplate（见 LlamaServicePage.xaml:166），
        /// 其滚动行为由模板内部名为 "ContentElement" 的 ScrollViewer 承载。
        /// 仅设置 SelectionStart 不足以让视觉滚动条跟随 Text.Length 移动 —— 必须
        /// 显式调用 ScrollViewer.ChangeView() 才能稳定滚到底部。
        /// The TextBox uses a custom ControlTemplate whose scrolling is owned
        /// by the template-part ScrollViewer named "ContentElement"; relying
        /// on SelectionStart alone doesn't reliably move the visual viewport,
        /// so we locate that ScrollViewer once and call ChangeView explicitly.
        /// </summary>
        private Microsoft.UI.Xaml.Controls.ScrollViewer? _paramsOutputScrollViewer;

        /// <summary>
        /// 尝试定位参数输出 TextBox 内部的 ScrollViewer（延迟到首次有数据到达时，
        /// 因为模板在 TextBox 第一次进入视觉树后才展开）
        /// Lazily locates the inner ScrollViewer. The template only expands
        /// once the TextBox is in the visual tree, so this is best deferred
        /// to the first actual output line instead of Loaded.
        /// </summary>
        private Microsoft.UI.Xaml.Controls.ScrollViewer? ResolveParamsOutputScrollViewer()
        {
            if (_paramsOutputScrollViewer != null) return _paramsOutputScrollViewer;
            if (ParamsOutputBox == null) return null;

            // 优先找模板里命名的 "ContentElement"（见 LlamaServicePage.xaml 的 ControlTemplate）；
            // 找不到时退回到第一个子 ScrollViewer，保证模板改名后仍能工作。
            // Prefer the named template part; fall back to the first child
            // ScrollViewer to stay resilient against future template renames.
            var named = (Microsoft.UI.Xaml.Controls.ScrollViewer?)ParamsOutputBox
                .FindName("ContentElement");
            _paramsOutputScrollViewer = named
                ?? FindVisualChildren<Microsoft.UI.Xaml.Controls.ScrollViewer>(ParamsOutputBox).FirstOrDefault();
            return _paramsOutputScrollViewer;
        }

        /// <summary>
        /// 参数输出数据接收事件处理器
        /// Handles parameter output data received from llama service
        /// </summary>
        private void OnParamsOutputDataReceived(object? sender, string data)
        {
            try
            {
                // 在 UI 线程上更新 TextBox
                // Update TextBox on UI thread
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isUnloaded || ParamsOutputBox == null) return;

                    // 追加新行
                    // Append new line.
                    ParamsOutputBox.Text += data + Environment.NewLine;

                    // 限制文本长度，避免内存占用过大（保留最后 10000 行）
                    // Trim to last 10k lines to bound memory; do this BEFORE
                    // scrolling so the final offset reflects the trimmed text.
                    var lines = ParamsOutputBox.Text.Split(Environment.NewLine);
                    if (lines.Length > 10000)
                    {
                        ParamsOutputBox.Text = string.Join(Environment.NewLine,
                            lines.Skip(lines.Length - 10000));
                    }

                    // 光标移到末尾（确保不会把光标卡在旧位置）
                    // Move the caret to the end so the user sees the tail end
                    // even if they click into the TextBox.
                    ParamsOutputBox.SelectionStart = ParamsOutputBox.Text.Length;
                    ParamsOutputBox.SelectionLength = 0;

                    // 关键：真正滚动视觉到底部 —— 通过 ChangeView 强制设置
                    // VerticalOffset 到 ScrollableHeight。延迟到下一帧执行，
                    // 以等待 TextBox 布局对新追加的文本做出测量更新。
                    // Force the visual viewport to the bottom via ChangeView
                    // after the layout pass that accounts for the newly
                    // appended text; without the LayoutUpdated gate, the call
                    // can race with measure/arrange and scroll to a stale
                    // ScrollableHeight.
                    var sv = ResolveParamsOutputScrollViewer();
                    if (sv != null)
                    {
                        ParamsOutputBox.UpdateLayout();
                        sv.ChangeView(null, sv.ScrollableHeight, null, disableAnimation: true);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理参数输出数据失败: {Message}", ex.Message);
            }
        }

        #endregion
    }
}


