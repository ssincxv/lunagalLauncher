using System.Collections.Concurrent;
using System.Diagnostics;

namespace lunagalLauncher.Utils
{
    /// <summary>
    /// 通过 <c>llama-server --help</c> 判断 <c>-fa / --flash-attn</c> 是否采用新版 <c>on|off|auto</c> 三态语法。
    /// 缓存键含 exe 完整路径与最后写入时间，避免重复探测。
    /// </summary>
    public static class LlamaServerFlashAttnCliProbe
    {
        private const int ProcessWaitMs = 8000;

        private static readonly ConcurrentDictionary<string, bool> Cache = new();

        /// <summary>
        /// <c>true</c>：应使用 <c>-fa on</c> / <c>-fa off</c>；<c>false</c>：应使用裸 <c>-fa</c>，且 <c>--no-mmap</c> 须放在 <c>-fa</c> 之前。
        /// 探测失败时返回 <c>true</c>，与当前主流构建一致。
        /// </summary>
        public static bool UsesTriStateOnOffAuto(string? servicePath)
        {
            if (string.IsNullOrWhiteSpace(servicePath) || !File.Exists(servicePath))
            {
                return true;
            }

            string key;
            try
            {
                key = Path.GetFullPath(servicePath) + "|" +
                      File.GetLastWriteTimeUtc(servicePath).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return true;
            }

            return Cache.GetOrAdd(key, _ => ProbeHelp(servicePath));
        }

        private static bool ProbeHelp(string exePath)
        {
            try
            {
                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--help",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                    }
                };

                p.Start();
                string stdout = p.StandardOutput.ReadToEnd();
                string stderr = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(ProcessWaitMs))
                {
                    try
                    {
                        p.Kill(entireProcessTree: true);
                    }
                    catch
                    {
                        // ignored
                    }

                    return true;
                }

                var text = stdout + Environment.NewLine + stderr;
                if (string.IsNullOrWhiteSpace(text))
                {
                    return true;
                }

                if (text.Contains("on|off|auto", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (text.Contains("on/off/auto", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}
