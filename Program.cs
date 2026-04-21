namespace lunagalLauncher
{
    /// <summary>
    /// 应用程序入口。
    ///
    /// XAML 编译器默认会生成一份 <c>Program.Main</c>（见 obj/.../App.g.i.cs），
    /// 本项目通过 csproj 中 <c>DISABLE_XAML_GENERATED_MAIN</c> 屏蔽那份自动生成，改由这里提供。
    /// 当前仅做原生的 WinUI 3 启动：InitializeComWrappers + Application.Start + new App()。
    ///
    /// （历史遗留）曾为规避 shell 扩展 AV 引入过 <c>--pick-file</c> / <c>--pick-save</c> / <c>--prewarm</c>
    /// 子进程 helper 模式，但 .NET CLR 冷启动带来 200–500ms 延迟，用户体验不好；
    /// 现已改为主进程直接调 <c>IFileOpenDialog</c>，配合进程级 LL 鼠标钩子吞右键
    /// （见 <see cref="T:lunagalLauncher.Utils.Win32FileDialog"/>），不再需要 helper 子进程。
    /// </summary>
    public static class Program
    {
        [System.STAThread]
        private static int Main(string[] args)
        {
            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            global::Microsoft.UI.Xaml.Application.Start((p) =>
            {
                var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
            return 0;
        }
    }
}
