using System.Diagnostics;

namespace lunagalLauncher.Utils
{
    /// <summary>
    /// 通过子进程执行 <c>llama-server --version</c> 读取版本信息（与 llama.cpp 文档一致）。
    /// 部分 CUDA 构建会在真正版本行前向 stderr 输出 ggml_cuda_init 等杂讯，不能简单取「首行」。
    /// </summary>
    public static class LlamaServerVersionProbe
    {
        private const int ProcessWaitMs = 20000;
        private const int MaxLineLength = 2048;

        /// <summary>
        /// 返回适合展示的版本摘要行；失败返回 null。
        /// </summary>
        public static string? TryGetVersionFirstLine(string exePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                    return null;

                using var p = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exePath,
                        Arguments = "--version",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
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

                    return null;
                }

                // 先 stdout 再 stderr：官方版本信息多在 stdout；stderr 常为 GGML 初始化。
                var combined = (stdout + Environment.NewLine + stderr).Trim();
                if (string.IsNullOrEmpty(combined))
                    return null;

                var lines = combined
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();

                if (lines.Count == 0)
                    return null;

                foreach (var line in lines)
                {
                    if (IsPreferredVersionLine(line))
                        return Truncate(line);
                }

                foreach (var line in lines)
                {
                    if (!IsLikelyNoiseLine(line))
                        return Truncate(line);
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>GGML/CUDA 等在 --version 时仍可能打印的诊断行。</summary>
        private static bool IsLikelyNoiseLine(string line)
        {
            var l = line.TrimStart();
            if (l.StartsWith("ggml_", StringComparison.OrdinalIgnoreCase)) return true;
            if (l.StartsWith("ggml ", StringComparison.OrdinalIgnoreCase)) return true;
            if (l.Contains("ggml_cuda_init", StringComparison.OrdinalIgnoreCase)) return true;
            if (l.StartsWith("llama_model_loader", StringComparison.OrdinalIgnoreCase)) return true;
            if (l.StartsWith("register_backend", StringComparison.OrdinalIgnoreCase)) return true;
            if (l.StartsWith("load_backend", StringComparison.OrdinalIgnoreCase)) return true;
            // 形如 key: key2: value 的多段冒号日志
            if (l.StartsWith("cuda", StringComparison.OrdinalIgnoreCase) && l.Contains(':'))
                return true;
            return false;
        }

        private static bool IsPreferredVersionLine(string line)
        {
            if (IsLikelyNoiseLine(line))
                return false;
            if (line.Contains("version", StringComparison.OrdinalIgnoreCase))
                return true;
            if (line.Contains("llama.cpp", StringComparison.OrdinalIgnoreCase))
                return true;
            if (line.Contains("commit", StringComparison.OrdinalIgnoreCase) &&
                line.Contains('(', StringComparison.Ordinal))
                return true;
            return false;
        }

        private static string Truncate(string line)
        {
            if (line.Length > MaxLineLength)
                return line.Substring(0, MaxLineLength);
            return line;
        }
    }
}
