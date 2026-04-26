namespace lunagalLauncher
{
    /// <summary>
    /// WinUI 3 入口（csproj 中 <c>DISABLE_XAML_GENERATED_MAIN</c> 时由本类提供 <c>Main</c>）。
    /// </summary>
    public static class Program
    {
        [System.STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--file-picker")
                return Helpers.FilePickerHelper.Run(args);

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
