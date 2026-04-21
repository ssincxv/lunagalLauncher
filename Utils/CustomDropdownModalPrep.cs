using lunagalLauncher.Controls;

namespace lunagalLauncher.Utils
{
    /// <summary>
    /// 在弹出系统模态对话框（comdlg32 等）之前，先让 <see cref="CustomDropdown"/> 完成收起动画，避免 Popup 与模态合成冲突。
    /// </summary>
    public static class CustomDropdownModalPrep
    {
        /// <summary>
        /// 与 <see cref="CustomDropdown"/> 收起动画上限略有余量（毫秒），与 Controls/CustomDropdown 动效一致。
        /// </summary>
        public const int CloseAnimationGraceMilliseconds = 550;

        /// <summary>
        /// 若下拉当前为打开态则关闭，并等待结束后再继续后续逻辑（例如 <c>IFileOpenDialog</c>）。
        /// </summary>
        public static async Task CloseIfOpenAndWaitForAnimationAsync(CustomDropdown dropdown)
        {
            bool wasOpen = dropdown.IsOpen;
            dropdown.IsOpen = false;
            await Task.Yield();
            if (wasOpen)
                await Task.Delay(CloseAnimationGraceMilliseconds);
        }
    }
}
