using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace GitStatusOverlay
{
    /// <summary>Runs git as a child process. Call only from background threads.</summary>
    internal static class GitRunner
    {
        private static string _gitExe;

        public static string GitExe
        {
            get
            {
                return _gitExe ??= FindGit() ?? "";
            }
        }

        public static bool Available => GitExe.Length > 0;

        private static string FindGit()
        {
            var names = Environment.OSVersion.Platform == PlatformID.Win32NT
                ? new[] { "git.exe", "git.cmd" }
                : new[] { "git" };
            var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                foreach (var name in names)
                {
                    try
                    {
                        var candidate = Path.Combine(dir.Trim().Trim('"'), name);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch (ArgumentException)
                    {
                        // malformed PATH entry: skip it
                    }
                }
            }
            return null;
        }

        /// <summary>Runs git and returns stdout, or null when git failed or timed out.</summary>
        public static string Run(string workDir, string args, int timeoutMs = 30000)
        {
            if (!Available) return null;
            var psi = new ProcessStartInfo
            {
                FileName = GitExe,
                Arguments = "-c core.quotepath=off -c color.ui=never -c core.safecrlf=false --no-optional-locks " + args,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Only stdout is redirected and read synchronously: Mono's async pipe readers
                // (BeginErrorReadLine / ReadToEndAsync) can deadlock a domain reload.
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            psi.EnvironmentVariables["LC_ALL"] = "C";
            try
            {
                using var p = Process.Start(psi);
                if (p == null) return null;
                bool killed = false;
                // Watchdog: killing git closes its stdout, so ReadToEnd below always returns.
                using var watchdog = new Timer(_ => { killed = true; TryKill(p); }, null, timeoutMs, Timeout.Infinite);
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return !killed && p.ExitCode == 0 ? output : null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private static void TryKill(Process p)
        {
            try
            {
                if (!p.HasExited) p.Kill();
            }
            catch (InvalidOperationException)
            {
                // already exited or disposed
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // access denied while exiting
            }
        }

        /// <summary>Quotes a path for the git command line (Windows argument rules).</summary>
        public static string Quote(string path)
        {
            return "\"" + path.Replace("\"", "\\\"") + "\"";
        }
    }
}
