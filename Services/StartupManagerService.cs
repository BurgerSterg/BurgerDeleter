using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml.Linq;
using BurgerDeleter.Models;
using Microsoft.Win32;

namespace BurgerDeleter.Services
{
    public static class StartupManagerService
    {
        // Registry Run key paths
        private static readonly (RegistryHive Hive, string Path, string Label)[] RegistryRunKeys =
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",           "HKLM\\Run"),
            (RegistryHive.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",           "HKCU\\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run","HKLM\\Run (32-bit)"),
        };

        // ===== READ =====

        public static async Task<List<StartupEntry>> GetStartupEntriesAsync(CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var entries = new List<StartupEntry>();

                ReadRegistryEntries(entries, ct);
                ReadStartupFolderEntries(entries, ct);
                // Task Scheduler is slow — fire and forget into the list synchronously here
                ReadTaskSchedulerEntries(entries, ct);

                return entries
                    .OrderByDescending(e => e.Impact)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }, ct);
        }

        // --- Registry ---

        private static void ReadRegistryEntries(List<StartupEntry> results, CancellationToken ct)
        {
            foreach (var (hive, keyPath, label) in RegistryRunKeys)
            {
                ct.ThrowIfCancellationRequested();
                RegistryKey? root = hive == RegistryHive.LocalMachine
                    ? Registry.LocalMachine : Registry.CurrentUser;

                try
                {
                    using var key = root.OpenSubKey(keyPath, writable: false);
                    if (key == null) continue;

                    foreach (var valueName in key.GetValueNames())
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var data = key.GetValue(valueName) as string ?? string.Empty;

                            // Names starting with '-' are disabled by convention
                            bool isEnabled = !valueName.StartsWith('-');
                            string displayName = isEnabled ? valueName : valueName.TrimStart('-');

                            var exePath  = ExtractExePath(data);
                            var impact   = DetermineImpact(exePath);
                            var publisher = GetPublisher(exePath);

                            results.Add(new StartupEntry
                            {
                                Name              = displayName,
                                Publisher         = publisher,
                                Command           = data,
                                Location          = label,
                                IsEnabled         = isEnabled,
                                Impact            = impact,
                                Source            = StartupEntrySource.Registry,
                                RegistryHive      = hive,
                                RegistryKeyPath   = keyPath,
                                RegistryValueName = valueName,
                            });
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }

        // --- Startup folders ---

        private static void ReadStartupFolderEntries(List<StartupEntry> results, CancellationToken ct)
        {
            var folders = new[]
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.Startup),       "User Startup Folder"),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Common Startup Folder"),
            };

            foreach (var (folder, label) in folders)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(folder)) continue;

                // Active shortcuts
                foreach (var file in Directory.EnumerateFiles(folder, "*.lnk"))
                {
                    ct.ThrowIfCancellationRequested();
                    var name    = Path.GetFileNameWithoutExtension(file);
                    var impact  = DetermineImpact(file);

                    results.Add(new StartupEntry
                    {
                        Name      = name,
                        Command   = file,
                        Location  = label,
                        IsEnabled = true,
                        Impact    = impact,
                        Source    = StartupEntrySource.StartupFolder,
                        FilePath  = file,
                    });
                }

                // Disabled shortcuts (moved to DisabledStartup subfolder)
                var disabledDir = Path.Combine(folder, "DisabledStartup");
                if (Directory.Exists(disabledDir))
                {
                    foreach (var file in Directory.EnumerateFiles(disabledDir, "*.lnk"))
                    {
                        ct.ThrowIfCancellationRequested();
                        var name = Path.GetFileNameWithoutExtension(file);
                        results.Add(new StartupEntry
                        {
                            Name      = name,
                            Command   = file,
                            Location  = label,
                            IsEnabled = false,
                            Impact    = StartupImpact.Unknown,
                            Source    = StartupEntrySource.StartupFolder,
                            FilePath  = file,
                        });
                    }
                }
            }
        }

        // --- Task Scheduler (XML parse) ---

        private static void ReadTaskSchedulerEntries(List<StartupEntry> results, CancellationToken ct)
        {
            try
            {
                // schtasks /query /xml ALL dumps every task as XML
                var xml = RunCommandSync("schtasks", "/query /xml ALL");
                if (string.IsNullOrWhiteSpace(xml)) return;

                // Output is a sequence of XML documents — wrap in a root element
                var wrapped  = $"<Root>{xml}</Root>";
                var doc      = XElement.Parse(wrapped);
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

                foreach (var task in doc.Descendants(ns + "Task"))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        // Must have a LogonTrigger
                        if (!task.Descendants(ns + "LogonTrigger").Any()) continue;

                        var uri     = task.Parent?.Element(ns + "URI")?.Value
                                   ?? task.Ancestors().FirstOrDefault()
                                          ?.Element(ns + "URI")?.Value ?? "Unknown";
                        var enabled = task.Element(ns + "Settings")
                                         ?.Element(ns + "Enabled")?.Value;
                        var command = task.Descendants(ns + "Command").FirstOrDefault()?.Value ?? string.Empty;
                        var args    = task.Descendants(ns + "Arguments").FirstOrDefault()?.Value ?? string.Empty;
                        var fullCmd = string.IsNullOrEmpty(args) ? command : $"{command} {args}";

                        var name      = uri.TrimStart('\\').Split('\\').Last();
                        var isEnabled = !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
                        var exePath   = ExtractExePath(command);
                        var impact    = DetermineImpact(exePath);
                        var publisher = GetPublisher(exePath);

                        results.Add(new StartupEntry
                        {
                            Name      = name,
                            Publisher = publisher,
                            Command   = fullCmd,
                            Location  = "Task Scheduler",
                            IsEnabled = isEnabled,
                            Impact    = impact,
                            Source    = StartupEntrySource.TaskScheduler,
                            TaskName  = uri,
                        });
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ===== ENABLE / DISABLE =====

        public static async Task SetEntryEnabledAsync(StartupEntry entry, bool enabled)
        {
            await Task.Run(() =>
            {
                switch (entry.Source)
                {
                    case StartupEntrySource.Registry:
                        SetRegistryEntryEnabled(entry, enabled);
                        break;
                    case StartupEntrySource.StartupFolder:
                        SetFolderEntryEnabled(entry, enabled);
                        break;
                    case StartupEntrySource.TaskScheduler:
                        SetTaskSchedulerEntryEnabled(entry, enabled);
                        break;
                }
            });
        }

        private static void SetRegistryEntryEnabled(StartupEntry entry, bool enable)
        {
            if (entry.RegistryHive == null || entry.RegistryKeyPath == null ||
                entry.RegistryValueName == null) return;

            RegistryKey root = entry.RegistryHive == RegistryHive.LocalMachine
                ? Registry.LocalMachine : Registry.CurrentUser;

            try
            {
                using var key = root.OpenSubKey(entry.RegistryKeyPath, writable: true);
                if (key == null) return;

                var currentName = entry.RegistryValueName;
                string enabledName  = currentName.TrimStart('-');
                string disabledName = "-" + enabledName;

                if (enable)
                {
                    // Rename disabled (-Name) → Name
                    if (currentName.StartsWith('-'))
                    {
                        var value = key.GetValue(currentName);
                        key.SetValue(enabledName, value ?? string.Empty);
                        key.DeleteValue(currentName, throwOnMissingValue: false);
                        entry.RegistryValueName = enabledName;
                    }
                }
                else
                {
                    // Rename Name → -Name
                    if (!currentName.StartsWith('-'))
                    {
                        var value = key.GetValue(currentName);
                        key.SetValue(disabledName, value ?? string.Empty);
                        key.DeleteValue(currentName, throwOnMissingValue: false);
                        entry.RegistryValueName = disabledName;
                    }
                }
            }
            catch { }
        }

        private static void SetFolderEntryEnabled(StartupEntry entry, bool enable)
        {
            if (string.IsNullOrEmpty(entry.FilePath)) return;

            try
            {
                var currentPath = entry.FilePath;
                var parentDir   = Path.GetDirectoryName(currentPath) ?? string.Empty;
                var fileName    = Path.GetFileName(currentPath);

                if (enable)
                {
                    // Move from DisabledStartup\ back to parent
                    if (parentDir.EndsWith("DisabledStartup", StringComparison.OrdinalIgnoreCase))
                    {
                        var activeDir = Directory.GetParent(parentDir)?.FullName ?? parentDir;
                        var dest      = Path.Combine(activeDir, fileName);
                        File.Move(currentPath, dest, overwrite: true);
                        entry.FilePath = dest;
                    }
                }
                else
                {
                    // Move to DisabledStartup\ subfolder
                    if (!parentDir.EndsWith("DisabledStartup", StringComparison.OrdinalIgnoreCase))
                    {
                        var disabledDir = Path.Combine(parentDir, "DisabledStartup");
                        Directory.CreateDirectory(disabledDir);
                        var dest = Path.Combine(disabledDir, fileName);
                        File.Move(currentPath, dest, overwrite: true);
                        entry.FilePath = dest;
                    }
                }
            }
            catch { }
        }

        private static void SetTaskSchedulerEntryEnabled(StartupEntry entry, bool enable)
        {
            if (string.IsNullOrEmpty(entry.TaskName)) return;
            var arg = enable ? "/enable" : "/disable";
            RunCommandSync("schtasks", $@"/change /tn ""{entry.TaskName}"" {arg}");
        }

        // ===== HELPERS =====

        private static string? ExtractExePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            command = Environment.ExpandEnvironmentVariables(command.Trim());

            if (command.StartsWith('"'))
            {
                var end = command.IndexOf('"', 1);
                if (end > 0) return command[1..end];
            }

            var exeIdx = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx >= 0) return command[..(exeIdx + 4)];

            return command.Split(' ')[0];
        }

        private static StartupImpact DetermineImpact(string? exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return StartupImpact.Unknown;
            try
            {
                long bytes = new FileInfo(exePath).Length;
                if (bytes > 30 * 1_048_576L) return StartupImpact.High;
                if (bytes >  5 * 1_048_576L) return StartupImpact.Medium;
                return StartupImpact.Low;
            }
            catch { return StartupImpact.Unknown; }
        }

        private static string? GetPublisher(string? exePath)
        {
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;
            try { return FileVersionInfo.GetVersionInfo(exePath).CompanyName; }
            catch { return null; }
        }

        private static string RunCommandSync(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return string.Empty;
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(15_000);
                return output;
            }
            catch { return string.Empty; }
        }
    }
}
