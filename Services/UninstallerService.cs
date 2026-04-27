using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace BurgerDeleter.Services
{
    public class UninstallResult
    {
        public bool Success { get; set; }
        public List<string> StepsCompleted { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }

    /// <summary>
    /// Performs a 6-layer deep uninstall of a program.
    /// Layer 1: Run standard uninstaller
    /// Layer 2: Registry sweep (HKLM + HKCU SOFTWARE + Uninstall keys)
    /// Layer 3: File system sweep (Program Files, AppData, ProgramData, Temp)
    /// Layer 4: Startup entries (registry Run keys + Startup folder)
    /// Layer 5: Scheduled tasks
    /// Layer 6: Services
    /// </summary>
    public class UninstallerService
    {
        // Registry paths to sweep for software entries
        private static readonly string[] SoftwareRegistryPaths =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };

        private static readonly string[] RunRegistryPaths =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce",
        };

        // Filesystem locations to sweep for leftover folders
        private static readonly string[] SweepPaths =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Low"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetTempPath(),
        };

        /// <summary>
        /// Runs all 6 layers of uninstall for the given program name.
        /// If directPath is set, the folder is deleted directly after the uninstaller runs.
        /// </summary>
        public async Task<UninstallResult> UninstallAsync(
            string programName,
            string? directPath = null,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var result = new UninstallResult();

            await Task.Run(() =>
            {
                // --- Layer 1: Standard uninstaller ---
                progress?.Report("Layer 1: Running standard uninstaller...");
                try
                {
                    var uninstallString = FindUninstallString(programName);
                    if (uninstallString != null)
                    {
                        RunUninstallString(uninstallString);
                        result.StepsCompleted.Add("Standard uninstaller executed");
                    }
                    else
                    {
                        result.StepsCompleted.Add("No standard uninstaller found (skipped)");
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Layer 1 error: {ex.Message}");
                }

                ct.ThrowIfCancellationRequested();

                // --- Layer 2: Registry sweep ---
                progress?.Report("Layer 2: Sweeping registry entries...");
                int regKeysRemoved = 0;
                try
                {
                    regKeysRemoved += SweepRegistryHive(Registry.LocalMachine, programName);
                    regKeysRemoved += SweepRegistryHive(Registry.CurrentUser, programName);
                    result.StepsCompleted.Add($"Registry sweep: {regKeysRemoved} key(s) removed");
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Layer 2 error: {ex.Message}");
                }

                ct.ThrowIfCancellationRequested();

                // --- Layer 3: File system sweep ---
                progress?.Report("Layer 3: Removing leftover files and folders...");
                int dirsRemoved = 0;
                try
                {
                    foreach (var basePath in SweepPaths)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!Directory.Exists(basePath)) continue;

                        foreach (var dir in Directory.EnumerateDirectories(basePath))
                        {
                            var dirName = Path.GetFileName(dir);
                            if (FuzzyMatch(dirName, programName))
                            {
                                TryDeleteDirectory(dir);
                                dirsRemoved++;
                            }
                        }
                    }

                    // Also delete the direct path if provided
                    if (directPath != null && Directory.Exists(directPath))
                    {
                        TryDeleteDirectory(directPath);
                        dirsRemoved++;
                    }

                    result.StepsCompleted.Add($"File sweep: {dirsRemoved} folder(s) removed");
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Layer 3 error: {ex.Message}");
                }

                ct.ThrowIfCancellationRequested();

                // --- Layer 4: Startup entries ---
                progress?.Report("Layer 4: Removing startup entries...");
                try
                {
                    int startupRemoved = RemoveStartupEntries(programName);
                    result.StepsCompleted.Add($"Startup entries: {startupRemoved} removed");
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Layer 4 error: {ex.Message}");
                }

                ct.ThrowIfCancellationRequested();

                // --- Layer 5: Scheduled tasks ---
                progress?.Report("Layer 5: Removing scheduled tasks...");
                try
                {
                    int tasksRemoved = RemoveScheduledTasks(programName);
                    result.StepsCompleted.Add($"Scheduled tasks: {tasksRemoved} removed");
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Layer 5 error: {ex.Message}");
                }

                ct.ThrowIfCancellationRequested();

                // --- Layer 6: Services ---
                progress?.Report("Layer 6: Removing services...");
                try
                {
                    int servicesRemoved = RemoveServices(programName);
                    result.StepsCompleted.Add($"Services: {servicesRemoved} removed");
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"Layer 6 error: {ex.Message}");
                }

                result.Success = result.Errors.Count == 0;

            }, ct);

            return result;
        }

        // ===== LAYER 1 HELPERS =====

        private static string? FindUninstallString(string programName)
        {
            var hives = new[] { Registry.LocalMachine, Registry.CurrentUser };
            foreach (var hive in hives)
            {
                foreach (var path in SoftwareRegistryPaths)
                {
                    using var key = hive.OpenSubKey(path);
                    if (key == null) continue;

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        using var subKey = key.OpenSubKey(subKeyName);
                        if (subKey == null) continue;

                        var displayName = subKey.GetValue("DisplayName") as string;
                        if (displayName != null && FuzzyMatch(displayName, programName))
                        {
                            return subKey.GetValue("UninstallString") as string;
                        }
                    }
                }
            }
            return null;
        }

        private static void RunUninstallString(string uninstallString)
        {
            // Handle msiexec and regular .exe uninstallers
            string fileName, arguments;

            if (uninstallString.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase) ||
                uninstallString.Contains("/I{") || uninstallString.Contains("/X{"))
            {
                fileName  = "msiexec.exe";
                arguments = uninstallString.Replace("MsiExec.exe", "").Trim() + " /qn";
            }
            else
            {
                // Parse "path\to\uninstaller.exe" /args
                fileName  = uninstallString.Trim().Trim('"');
                arguments = "/S /silent /quiet"; // common silent flags
                if (uninstallString.Contains(" "))
                {
                    // Has arguments already
                    int spaceIdx = uninstallString.IndexOf(' ');
                    fileName  = uninstallString[..spaceIdx].Trim('"');
                    arguments = uninstallString[(spaceIdx + 1)..] + " /S /silent";
                }
            }

            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = true,
                Verb            = "runas" // request elevation
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(60_000); // wait max 60 seconds
        }

        // ===== LAYER 2 HELPERS =====

        private static int SweepRegistryHive(RegistryKey hive, string programName)
        {
            int removed = 0;

            // Sweep SOFTWARE keys
            foreach (var path in SoftwareRegistryPaths)
            {
                using var key = hive.OpenSubKey(path, writable: true);
                if (key == null) continue;

                var toDelete = key.GetSubKeyNames()
                    .Where(name => FuzzyMatch(name, programName))
                    .ToList();

                foreach (var name in toDelete)
                {
                    try { key.DeleteSubKeyTree(name, throwOnMissingSubKey: false); removed++; }
                    catch { }
                }
            }

            // Sweep generic SOFTWARE\{publisher or app name} keys
            using var softwareKey = hive.OpenSubKey("SOFTWARE", writable: true);
            if (softwareKey != null)
            {
                var toDelete = softwareKey.GetSubKeyNames()
                    .Where(name => FuzzyMatch(name, programName))
                    .ToList();

                foreach (var name in toDelete)
                {
                    try { softwareKey.DeleteSubKeyTree(name, throwOnMissingSubKey: false); removed++; }
                    catch { }
                }
            }

            return removed;
        }

        // ===== LAYER 4 HELPERS =====

        private static int RemoveStartupEntries(string programName)
        {
            int removed = 0;
            var hives   = new[] { Registry.LocalMachine, Registry.CurrentUser };

            foreach (var hive in hives)
            {
                foreach (var path in RunRegistryPaths)
                {
                    using var key = hive.OpenSubKey(path, writable: true);
                    if (key == null) continue;

                    var toDelete = key.GetValueNames()
                        .Where(name => FuzzyMatch(name, programName) ||
                                       FuzzyMatch(key.GetValue(name) as string ?? "", programName))
                        .ToList();

                    foreach (var name in toDelete)
                    {
                        try { key.DeleteValue(name, throwOnMissingValue: false); removed++; }
                        catch { }
                    }
                }
            }

            // Check Startup folder
            var startupFolders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
            };

            foreach (var folder in startupFolders)
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    if (FuzzyMatch(Path.GetFileName(file), programName))
                    {
                        try { File.Delete(file); removed++; }
                        catch { }
                    }
                }
            }

            return removed;
        }

        // ===== LAYER 5 HELPERS =====

        private static int RemoveScheduledTasks(string programName)
        {
            int removed = 0;
            try
            {
                // Query all tasks
                var listProcess = new ProcessStartInfo("schtasks", "/query /fo csv /nh")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                };

                using var p = Process.Start(listProcess);
                if (p == null) return 0;

                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();

                foreach (var line in output.Split('\n'))
                {
                    var taskName = line.Split(',').FirstOrDefault()?.Trim('"', ' ');
                    if (taskName == null || !FuzzyMatch(taskName, programName)) continue;

                    var deleteProcess = new ProcessStartInfo("schtasks", $"/delete /tn \"{taskName}\" /f")
                    {
                        UseShellExecute = true,
                        Verb            = "runas",
                        CreateNoWindow  = true
                    };
                    using var dp = Process.Start(deleteProcess);
                    dp?.WaitForExit(10_000);
                    removed++;
                }
            }
            catch { }
            return removed;
        }

        // ===== LAYER 6 HELPERS =====

        private static int RemoveServices(string programName)
        {
            int removed = 0;
            try
            {
                using var servicesKey = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services", writable: true);

                if (servicesKey == null) return 0;

                var toDelete = servicesKey.GetSubKeyNames()
                    .Where(name => FuzzyMatch(name, programName))
                    .ToList();

                foreach (var name in toDelete)
                {
                    try
                    {
                        // Stop the service first
                        var stopProcess = new ProcessStartInfo("sc", $"stop \"{name}\"")
                        {
                            UseShellExecute = true,
                            Verb            = "runas",
                            CreateNoWindow  = true
                        };
                        using var sp = Process.Start(stopProcess);
                        sp?.WaitForExit(10_000);

                        // Delete it
                        var deleteProcess = new ProcessStartInfo("sc", $"delete \"{name}\"")
                        {
                            UseShellExecute = true,
                            Verb            = "runas",
                            CreateNoWindow  = true
                        };
                        using var dp = Process.Start(deleteProcess);
                        dp?.WaitForExit(10_000);
                        removed++;
                    }
                    catch { }
                }
            }
            catch { }
            return removed;
        }

        // ===== SHARED HELPERS =====

        private static void TryDeleteDirectory(string path)
        {
            try { Directory.Delete(path, recursive: true); }
            catch { }
        }

        /// <summary>
        /// Case-insensitive check: does the candidate contain the search term or vice versa?
        /// </summary>
        private static bool FuzzyMatch(string candidate, string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(searchTerm))
                return false;

            candidate  = candidate.ToLowerInvariant();
            searchTerm = searchTerm.ToLowerInvariant();

            return candidate.Contains(searchTerm) || searchTerm.Contains(candidate);
        }
    }
}
