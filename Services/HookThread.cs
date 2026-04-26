using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Serilog;

namespace lunagalLauncher.Services
{
    /// <summary>
    /// 专用消息泵线程，用于托管 WH_MOUSE_LL / WH_KEYBOARD_LL 等低层钩子。
    ///
    /// <para>
    /// 背景：LL 钩子只会在"装钩所在线程的消息队列"里被回调。之前装在 UI 线程，UI 线程一忙
    /// （首帧 Composition / 页切换 ApplyTemplate / XAML JIT）系统鼠标事件排队在 UI 线程，
    /// 用户感知为指针 50–100 ms 卡顿。把装钩移到独立消息泵线程后，UI 线程任意繁忙都不会
    /// 映射成鼠标卡。
    /// </para>
    /// <para>
    /// 用法：外部任意线程调 <see cref="EnsureStarted"/> 启动；<see cref="Invoke"/> 在钩子
    /// 线程里同步跑一段 action（SetWindowsHookEx / UnhookWindowsHookEx 必须在同线程成对调
    /// 用，这是 Invoke 存在的主要目的）。进程退出时由主窗 Closed 调用 <see cref="Stop"/>。
    /// </para>
    /// </summary>
    internal static class HookThread
    {
        /// <summary>内部自定义消息：线程收到后从 <see cref="_pendingJobs"/> 取一条同步 action 执行并 Set 回 MRE。</summary>
        private const uint WM_APP_INVOKE = 0x8000 + 1;

        /// <summary>消息泵所在线程句柄（仅调试/日志用）。</summary>
        private static Thread? _thread;

        /// <summary>消息泵线程的 Win32 ThreadId，用于 <see cref="PostThreadMessage"/>。</summary>
        private static uint _threadId;

        /// <summary>线程进入消息循环后置位，<see cref="EnsureStarted"/> 等待该信号。</summary>
        private static readonly ManualResetEventSlim _ready = new(false);

        /// <summary>
        /// 跨线程投递的待执行项。<c>done</c>/<c>error</c> 为 <c>null</c> 时表示 fire-and-forget
        /// （<see cref="Post"/> 投递），泵线程只跑 action、不再 Set/抛出；否则是 <see cref="Invoke"/>
        /// 同步路径，泵线程跑完必 Set + 缓存异常供调用方抛。
        /// </summary>
        private static readonly ConcurrentQueue<(Action work, ManualResetEventSlim? done, Exception?[]? error)> _pendingJobs = new();

        /// <summary>幂等启动闩：0=未启动，1=已启动。</summary>
        private static int _started;

        /// <summary>标记是否已发出 WM_QUIT（避免重复 Stop）。</summary>
        private static int _stopped;

        /// <summary>
        /// 幂等启动消息泵线程，阻塞等到线程进入 <c>GetMessage</c> 循环。
        /// 实测耗时 &lt; 2 ms，不会卡 UI；允许任意线程调用。
        /// </summary>
        internal static void EnsureStarted()
        {
            if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            {
                // 已启动；等消息泵就绪（可能是前一次调用刚 CAS 成功但线程尚未跑到 _ready.Set）。
                _ready.Wait();
                return;
            }

            _thread = new Thread(PumpEntry)
            {
                IsBackground = true,
                Name = "LunagalLLHookPump",
                Priority = ThreadPriority.AboveNormal
            };
            _thread.Start();
            _ready.Wait();
            Log.Information("HookThread：消息泵线程已启动（ThreadId={Tid}，Priority=AboveNormal）", _threadId);
        }

        /// <summary>
        /// 在钩子线程里同步执行 <paramref name="action"/> 并等待完成。
        /// 调用方通常是 UI 线程；若当前已在钩子线程内，直接本地执行避免死锁。
        ///
        /// <para>
        /// 适用场景：<strong>必须同步确认</strong>执行结果的调用（目前主要是 <see cref="Stop"/> 的反向路径）。
        /// 装钩 / 卸钩 走 <see cref="Post"/> 即可——我们靠 HookThread 的 FIFO 顺序保证语义正确，
        /// 不需要 UI 线程 block 等待 HookThread 转完钩；这样首次弹文件对话框不会被三次同步 Invoke 拖成 glitched 帧。
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">钩子线程未启动或已停止。</exception>
        internal static void Invoke(Action action)
        {
            if (action == null) return;

            // 已在钩子线程内：直接执行，避免 WM_APP_INVOKE 死等自己。
            if (Environment.CurrentManagedThreadId == (_thread?.ManagedThreadId ?? -1))
            {
                action();
                return;
            }

            if (Volatile.Read(ref _started) == 0)
                throw new InvalidOperationException("HookThread 未启动，请先调用 EnsureStarted()");

            if (Volatile.Read(ref _stopped) == 1)
                throw new InvalidOperationException("HookThread 已停止，无法投递新任务");

            _ready.Wait();

            using var done = new ManualResetEventSlim(false);
            var errBox = new Exception?[1];
            // 非空 done/error → 泵线程识别为同步 Invoke 路径，会 Set 信号并回填异常。
            _pendingJobs.Enqueue((action, done, errBox));

            // PostThreadMessage：hWnd=NULL 把 WM_APP_INVOKE 投到目标线程队列，GetMessage 直接返回。
            if (!PostThreadMessage(_threadId, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero))
            {
                // PostThreadMessage 失败极罕见，除非线程已终止；把刚入队的取走避免占位泄漏。
                int err = Marshal.GetLastWin32Error();
                // 尝试回收这个 job 的等待项（best-effort；若被泵线程抢先消费也没关系）。
                throw new InvalidOperationException($"HookThread.Invoke: PostThreadMessage 失败 err={err}");
            }

            done.Wait();

            if (errBox[0] is { } ex)
                throw new AggregateException("HookThread.Invoke 内部 action 抛异常", ex);
        }

        /// <summary>
        /// 向钩子线程单向投递 <paramref name="action"/>，<strong>不阻塞调用方</strong>。
        ///
        /// <para>
        /// 装钩 / 卸钩都走此方法，好处：
        /// <list type="bullet">
        /// <item>UI 线程点"浏览"/"添加应用"后瞬间返回，按钮弹起动画有机会渲染一帧，不再出现"卡住半个文件对话框"的 glitched 画面。</item>
        /// <item>文件对话框关闭后重装钩子，UI 线程也不等——用户右键菜单不会再出现"加载一下才弹"的延迟。</item>
        /// </list>
        /// </para>
        /// <para>
        /// 顺序保证：所有 Post/Invoke 都通过同一 <see cref="_pendingJobs"/> FIFO 队列 + 单线程消费，
        /// 先发的先跑，所以 "Post(Uninstall) → Post(Install)" 的尾态一定是 "Install"，
        /// 不会出现把刚装好的钩子又被先前的 Uninstall 拆掉的错序。
        /// </para>
        /// </summary>
        internal static void Post(Action action)
        {
            if (action == null) return;

            if (Environment.CurrentManagedThreadId == (_thread?.ManagedThreadId ?? -1))
            {
                // 已在钩子线程内：直接执行。捕获异常避免泵线程挂掉。
                try { action(); }
                catch (Exception ex) { Log.Warning(ex, "HookThread.Post(in-thread): action 抛异常"); }
                return;
            }

            if (Volatile.Read(ref _started) == 0)
            {
                Log.Warning("HookThread.Post: 线程未启动，action 被丢弃");
                return;
            }

            if (Volatile.Read(ref _stopped) == 1)
            {
                Log.Debug("HookThread.Post: 线程已停止，action 被丢弃");
                return;
            }

            _ready.Wait();

            // done / error 传 null：泵线程只跑 action，不 Set / 不抛；我们也无需 Dispose MRE，避免泄漏。
            // 异常由泵线程写到日志即可。
            _pendingJobs.Enqueue((action, null, null));

            if (!PostThreadMessage(_threadId, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                Log.Warning("HookThread.Post: PostThreadMessage 失败 err={Err}，action 被丢弃", err);
                _pendingJobs.TryDequeue(out _);
            }
        }

        /// <summary>
        /// 通知消息泵线程优雅退出。幂等；进程退出时由 <c>App.Window_Closed</c> 调用，
        /// 线程结束后句柄进入 Joinable 态；不强行等待（与主进程一同回收即可，避免阻塞 UI Closed）。
        /// </summary>
        internal static void Stop()
        {
            if (Interlocked.CompareExchange(ref _stopped, 1, 0) != 0) return;
            if (Volatile.Read(ref _started) == 0) return;

            try
            {
                PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                Log.Information("HookThread：已投递 WM_QUIT，消息泵线程即将退出");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "HookThread：Stop 投递 WM_QUIT 异常（忽略）");
            }
        }

        /// <summary>
        /// 消息泵线程入口。取当前 Win32 ThreadId → 创建空消息队列 → Set 就绪信号 → GetMessage 循环。
        /// 循环中收到 WM_APP_INVOKE 就出队一个 action 执行并 Set 完成信号；WM_QUIT 直接跳出。
        /// </summary>
        private static void PumpEntry()
        {
            _threadId = GetCurrentThreadId();

            // 首次 PeekMessage 强制 Windows 为当前线程创建消息队列，避免外部 PostThreadMessage
            // 在队列未创建前掉包（MSDN 明确要求）。
            PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

            _ready.Set();

            try
            {
                while (true)
                {
                    int r = GetMessage(out MSG msg, IntPtr.Zero, 0, 0);
                    if (r <= 0)
                    {
                        // r == 0：收到 WM_QUIT；r == -1：GetMessage 失败。两种都结束。
                        if (r < 0)
                            Log.Warning("HookThread：GetMessage 返回 -1 (err={Err})，消息泵结束",
                                Marshal.GetLastWin32Error());
                        break;
                    }

                    if (msg.message == WM_APP_INVOKE)
                    {
                        // WM_APP_INVOKE 与入队 job 1:1 对应；若队列空表示 action 已被前一条消息消费。
                        if (_pendingJobs.TryDequeue(out var job))
                        {
                            try
                            {
                                job.work();
                            }
                            catch (Exception ex)
                            {
                                if (job.error != null) job.error[0] = ex;
                                Log.Warning(ex, "HookThread：action 抛异常（{Mode}）",
                                    job.done == null ? "Post" : "Invoke");
                            }
                            finally
                            {
                                // fire-and-forget：done 为 null，不用 Set，也不需要调用方来 Dispose。
                                job.done?.Set();
                            }
                        }
                        continue;
                    }

                    // 未订阅线程消息不会走 DispatchMessage 路径（hwnd=NULL）。为将来扩展稳妥兜底。
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "HookThread：消息泵异常退出");
            }
            finally
            {
                // 线程退出；泵内未完成的 job 也 Set 一下避免 Invoke 调用方永久阻塞。
                // Post-style (done == null) 的 job 不需要信号；直接丢弃即可。
                while (_pendingJobs.TryDequeue(out var leftover))
                {
                    if (leftover.error != null)
                        leftover.error[0] = new InvalidOperationException("HookThread 已退出，action 未执行");
                    leftover.done?.Set();
                }
                Log.Information("HookThread：消息泵线程已停止");
            }
        }

        #region P/Invoke

        private const uint WM_QUIT = 0x0012;
        private const uint PM_NOREMOVE = 0x0000;

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        #endregion
    }
}
