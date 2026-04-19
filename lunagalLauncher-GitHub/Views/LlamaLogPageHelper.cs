using System;
using Serilog;

namespace lunagalLauncher.Views
{
    /// <summary>
    /// LlamaLogPage 辅助类
    /// Helper class for communicating with LlamaLogPage
    /// 用于在不同页面之间传递状态信息
    /// </summary>
    public static class LlamaLogPageHelper
    {
        /// <summary>
        /// 服务状态更新事件
        /// Event fired when service status is updated
        /// </summary>
        public static event EventHandler<(string StatusText, string StatusColor)>? ServiceStatusUpdated;

        /// <summary>
        /// API 地址更新事件
        /// Event fired when API address is updated
        /// </summary>
        public static event EventHandler<string>? ApiAddressUpdated;

        /// <summary>
        /// 是否已注册（用于调试）
        /// Whether any listeners are registered (for debugging)
        /// </summary>
        public static bool IsRegistered => ServiceStatusUpdated != null || ApiAddressUpdated != null;

        /// <summary>
        /// 更新服务状态
        /// Updates service status and notifies listeners
        /// </summary>
        /// <param name="statusText">状态文本（如"运行中"、"已停止"）</param>
        /// <param name="statusColor">状态颜色（如"Green"、"Red"）</param>
        public static void UpdateServiceStatus(string statusText, string statusColor)
        {
            try
            {
                Log.Information("📢 [LlamaLogPageHelper] 更新服务状态: {StatusText}, {StatusColor}", statusText, statusColor);
                Log.Information("📢 [LlamaLogPageHelper] 订阅者数量: {Count}", ServiceStatusUpdated?.GetInvocationList().Length ?? 0);

                ServiceStatusUpdated?.Invoke(null, (statusText, statusColor));

                Log.Information("✅ [LlamaLogPageHelper] 服务状态已更新并通知订阅者");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "更新服务状态失败");
            }
        }

        /// <summary>
        /// 更新 API 地址
        /// Updates API address and notifies listeners
        /// </summary>
        /// <param name="apiAddress">API 地址（如 "http://127.0.0.1:8080"）</param>
        public static void UpdateApiAddress(string apiAddress)
        {
            try
            {
                Log.Information("📢 [LlamaLogPageHelper] 更新 API 地址: {ApiAddress}", apiAddress);
                Log.Information("📢 [LlamaLogPageHelper] 订阅者数量: {Count}", ApiAddressUpdated?.GetInvocationList().Length ?? 0);

                ApiAddressUpdated?.Invoke(null, apiAddress);

                Log.Information("✅ [LlamaLogPageHelper] API 地址已更新并通知订阅者");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "更新 API 地址失败");
            }
        }

        /// <summary>
        /// 清除所有订阅者（用于清理）
        /// Clears all event subscribers (for cleanup)
        /// </summary>
        public static void ClearSubscribers()
        {
            try
            {
                ServiceStatusUpdated = null;
                ApiAddressUpdated = null;
                Log.Information("🧹 [LlamaLogPageHelper] 已清除所有订阅者");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "清除订阅者失败");
            }
        }
    }
}

