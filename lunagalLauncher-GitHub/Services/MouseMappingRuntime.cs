using System;
using System.IO;
using lunagalLauncher.Data;
using Microsoft.UI.Dispatching;
using Serilog;

namespace lunagalLauncher.Services
{
    /// <summary>
    /// 鼠标映射运行时：应用配置、可选配置文件热重载
    /// </summary>
    public static class MouseMappingRuntime
    {
        private static FileSystemWatcher? _watcher;
        private static DispatcherQueue? _dispatcher;
        private static DispatcherQueueTimer? _debounceTimer;

        /// <summary>
        /// 主页面 Loaded 后调用（注册 UI 线程队列并安装钩子）
        /// </summary>
        public static void InitializeUi(DispatcherQueue dispatcherQueue)
        {
            _dispatcher = dispatcherQueue;
            MouseMappingEngine.SetDispatcherQueue(dispatcherQueue);
            ApplyFromCurrentConfig();
        }

        public static void ApplyFromCurrentConfig()
        {
            MouseMappingEngine.Apply(App.AppConfig.MouseMapping);
            if (!App.AppConfig.MouseMapping.HotReload)
            {
                _watcher?.Dispose();
                _watcher = null;
            }
            else if (_watcher == null)
                TryStartFileWatcher();
        }

        private static void TryStartFileWatcher()
        {
            try
            {
                if (!App.AppConfig.MouseMapping.HotReload)
                {
                    Log.Information("鼠标映射：热重载已关闭，不监视配置文件");
                    return;
                }

                if (_watcher != null)
                    return;

                _debounceTimer = null;

                var path = App.ConfigManager.ConfigFilePath;
                if (string.IsNullOrEmpty(path)) return;
                string dir = Path.GetDirectoryName(path)!;
                string file = Path.GetFileName(path);

                _watcher = new FileSystemWatcher(dir, file)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                };
                _watcher.Changed += OnConfigFileChanged;
                _watcher.EnableRaisingEvents = true;
                Log.Information("鼠标映射：已监视配置文件变更（热重载）");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "鼠标映射：无法监视配置文件");
            }
        }

        private static void OnConfigFileChanged(object sender, FileSystemEventArgs e)
        {
            if (_dispatcher == null) return;
            _ = _dispatcher.TryEnqueue(() =>
            {
                if (_debounceTimer != null)
                {
                    _debounceTimer.Stop();
                    _debounceTimer = null;
                }
                _debounceTimer = _dispatcher.CreateTimer();
                _debounceTimer.Interval = TimeSpan.FromMilliseconds(450);
                _debounceTimer.IsRepeating = false;
                _debounceTimer.Tick += (_, _) =>
                {
                    try
                    {
                        if (!App.AppConfig.MouseMapping.HotReload) return;
                        var reloaded = App.ConfigManager.LoadConfig();
                        App.AppConfig = reloaded;
                        MouseMappingEngine.Apply(reloaded.MouseMapping);
                        Log.Information("鼠标映射：已从磁盘热重载");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "鼠标映射：热重载失败");
                    }
                };
                _debounceTimer.Start();
            });
        }
    }
}
