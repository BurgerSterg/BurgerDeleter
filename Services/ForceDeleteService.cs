using System.Diagnostics;
using System.IO;

namespace BurgerDeleter.Services
{
    public static class ForceDeleteService
    {
        public static async Task ForceDeleteAsync(string path)
        {
            await Task.Run(() =>
            {
                KillProcessesUsingPath(path);
                GrantFullControl(path);

                if (Directory.Exists(path))
                    DeleteDirectory(path);
                else if (File.Exists(path))
                    DeleteFile(path);
            });
        }

        // ===== STEP 1: Kill processes with open handles inside the target path =====

        private static void KillProcessesUsingPath(string targetPath)
        {
            var normalized = targetPath.TrimEnd(Path.DirectorySeparatorChar,
                                                 Path.AltDirectorySeparatorChar)
                                        .ToLowerInvariant();

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    var module = proc.MainModule?.FileName;
                    if (module == null) continue;

                    if (module.ToLowerInvariant().StartsWith(normalized,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        proc.Kill();
                        proc.WaitForExit(3000);
                    }
                }
                catch { /* process may have exited or access denied -- skip */ }
            }
        }

        // ===== STEP 2: Grant full control via icacls =====

        private static void GrantFullControl(string path)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName        = "icacls",
                    Arguments       = $"\"{path}\" /grant Everyone:F /T /C /Q",
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                    Verb            = "runas"
                };

                using var proc = Process.Start(psi);
                proc?.WaitForExit(15_000);
            }
            catch { }
        }

        // ===== STEP 3a: Delete directory =====

        private static void DeleteDirectory(string path)
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (UnauthorizedAccessException)
            {
                FallbackDeleteDirectory(path);
            }
            catch (IOException)
            {
                FallbackDeleteDirectory(path);
            }
        }

        private static void FallbackDeleteDirectory(string path)
        {
            var psi = new ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/c rd /s /q \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow  = true,
                Verb            = "runas"
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(60_000);
        }

        // ===== STEP 3b: Delete file =====

        private static void DeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (UnauthorizedAccessException)
            {
                FallbackDeleteFile(path);
            }
            catch (IOException)
            {
                FallbackDeleteFile(path);
            }
        }

        private static void FallbackDeleteFile(string path)
        {
            var psi = new ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = $"/c del /f /q \"{path}\"",
                UseShellExecute = false,
                CreateNoWindow  = true,
                Verb            = "runas"
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(30_000);
        }
    }
}
