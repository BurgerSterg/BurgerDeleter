using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BurgerDeleter.Helpers;
using BurgerDeleter.Models;
using Microsoft.Win32;

namespace BurgerDeleter.Services
{
    // ===== DATA MODELS =====

    public enum HealthSeverity { Info, Warning, Critical }

    public enum HealthCategory { Storage, Performance, Security, System }

    /// <summary>What the "fix" button on a recommendation does.</summary>
    public enum HealthFix
    {
        None,
        // In-place fixes (SystemHealthService.ApplyFixAsync)
        ClearTemp, EmptyRecycleBin, UpdateDefender, EnableGameMode, EnableHags, OpenFirewallSettings,
        // Navigation to another tab (handled by the view)
        OpenScan, OpenStartup, OpenSecurity, OpenMemory, OpenGameOptimizer,
    }

    public sealed class HealthIssue
    {
        public HealthSeverity Severity { get; init; }
        public HealthCategory Category { get; init; }
        public string Title    { get; init; } = string.Empty;
        public string Detail   { get; init; } = string.Empty;
        public HealthFix Fix   { get; init; }
        public string FixLabel { get; init; } = string.Empty;

        public bool   HasFix        => Fix != HealthFix.None;
        public string SeverityLabel => Severity.ToString().ToUpperInvariant();
        public string CategoryLabel => Category.ToString().ToUpperInvariant();
        public string SeverityColor => Severity switch
        {
            HealthSeverity.Critical => "#EF4444",
            HealthSeverity.Warning  => "#F59E0B",
            _                       => "#22D3EE",
        };
    }

    public sealed class DiskHealth
    {
        public string Name      { get; init; } = string.Empty;
        public double TotalGB   { get; init; }
        public double FreeGB    { get; init; }
        public double UsedPct   => TotalGB <= 0 ? 0 : (TotalGB - FreeGB) / TotalGB * 100;
        public string Summary   => $"{FreeGB:F0} GB free of {TotalGB:F0} GB";
    }

    public sealed class HealthReport
    {
        public int    Score { get; init; }
        public Dictionary<HealthCategory, int> CategoryScores { get; init; } = new();
        public List<HealthIssue> Issues { get; init; } = new();
        public List<string> Passed     { get; init; } = new();

        public string CpuName   { get; init; } = "—";
        public string GpuName   { get; init; } = "—";
        public string OsName    { get; init; } = "—";
        public double RamTotalGB { get; init; }
        public TimeSpan Uptime  { get; init; }
        public List<DiskHealth> Disks { get; init; } = new();
        public DateTime GeneratedAt { get; init; } = DateTime.Now;

        public string Grade => Score switch
        {
            >= 90 => "OPTIMAL",
            >= 75 => "GOOD",
            >= 55 => "NEEDS ATTENTION",
            _     => "AT RISK",
        };
    }

    // ===== SERVICE =====

    public static class SystemHealthService
    {
        private static readonly Dictionary<HealthSeverity, int> Penalty = new()
        {
            [HealthSeverity.Critical] = 25,
            [HealthSeverity.Warning]  = 10,
            [HealthSeverity.Info]     = 3,
        };

        public static async Task<HealthReport> AnalyzeAsync(IProgress<string>? progress = null)
        {
            var issues = new List<HealthIssue>();
            var passed = new List<string>();

            // Independent checks run in parallel; each returns its own findings.
            var checks = new List<Task<(List<HealthIssue>, List<string>)>>
            {
                Run(CheckStorage),
                Run(CheckMemory),
                Run(CheckUptimeAndReboot),
                Run(CheckTempAndRecycleBin),
                Run(CheckFirewall),
                Run(CheckGamingSettings),
                CheckStartupAsync(),
                CheckSecurityAsync(),
            };

            progress?.Report("Analyzing system…");
            foreach (var (i, p) in await Task.WhenAll(checks))
            {
                issues.AddRange(i);
                passed.AddRange(p);
            }

            var (cpu, gpu, os, disks) = await Task.Run(() => (GetCpuName(), GetGpuName(), GetOsName(), GetDisks()));
            var (totalMB, _) = MemoryOptimizerService.GetMemoryStatus();

            var categoryScores = Enum.GetValues<HealthCategory>().ToDictionary(
                c => c,
                c => Math.Clamp(100 - issues.Where(i => i.Category == c).Sum(i => Penalty[i.Severity]), 0, 100));

            // Overall = mean of categories, so a few minor findings don't read as "at risk";
            // any critical finding caps it so it can never look healthy.
            int score = (int)Math.Round(categoryScores.Values.Average());
            if (issues.Any(i => i.Severity == HealthSeverity.Critical))
                score = Math.Min(score, 60);

            return new HealthReport
            {
                Score          = Math.Clamp(score, 5, 100),
                CategoryScores = categoryScores,
                Issues         = issues.OrderByDescending(i => i.Severity).ToList(),
                Passed         = passed,
                CpuName        = cpu,
                GpuName        = gpu,
                OsName         = os,
                RamTotalGB     = totalMB / 1024.0,
                Uptime         = TimeSpan.FromMilliseconds(Environment.TickCount64),
                Disks          = disks,
            };
        }

        private static Task<(List<HealthIssue>, List<string>)> Run(Action<List<HealthIssue>, List<string>> check)
            => Task.Run(() =>
            {
                var issues = new List<HealthIssue>();
                var passed = new List<string>();
                try { check(issues, passed); } catch { /* a failed check never breaks the report */ }
                return (issues, passed);
            });

        // ── Storage ──────────────────────────────────────────────────────────

        private static void CheckStorage(List<HealthIssue> issues, List<string> passed)
        {
            foreach (var d in GetDisks())
            {
                double freePct = 100 - d.UsedPct;
                if (freePct < 10)
                    issues.Add(new HealthIssue
                    {
                        Severity = HealthSeverity.Critical, Category = HealthCategory.Storage,
                        Title    = $"Drive {d.Name} is almost full",
                        Detail   = $"Only {d.FreeGB:F1} GB ({freePct:F0}%) left. Windows slows down and updates fail below 10%.",
                        Fix = HealthFix.OpenScan, FixLabel = "Free up space",
                    });
                else if (freePct < 20)
                    issues.Add(new HealthIssue
                    {
                        Severity = HealthSeverity.Warning, Category = HealthCategory.Storage,
                        Title    = $"Drive {d.Name} is getting full",
                        Detail   = $"{d.FreeGB:F1} GB ({freePct:F0}%) free. Keep at least 20% free for best performance.",
                        Fix = HealthFix.OpenScan, FixLabel = "Free up space",
                    });
                else
                    passed.Add($"Drive {d.Name} has plenty of free space");
            }

            // Physical disk SMART-style health from the Storage WMI provider
            using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, MediaType, HealthStatus FROM MSFT_PhysicalDisk");
            bool anySsd = false, anyDisk = false;
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    anyDisk = true;
                    var name   = mo["FriendlyName"] as string ?? "Disk";
                    int media  = Convert.ToInt32(mo["MediaType"] ?? 0);   // 3 HDD, 4 SSD, 5 SCM
                    int health = Convert.ToInt32(mo["HealthStatus"] ?? 0); // 0 Healthy, 1 Warning, 2 Unhealthy
                    if (media is 4 or 5) anySsd = true;

                    if (health == 2)
                        issues.Add(new HealthIssue
                        {
                            Severity = HealthSeverity.Critical, Category = HealthCategory.Storage,
                            Title    = $"{name} is reporting failure",
                            Detail   = "Windows reports this disk as unhealthy. Back up your files now and plan to replace it.",
                        });
                    else if (health == 1)
                        issues.Add(new HealthIssue
                        {
                            Severity = HealthSeverity.Warning, Category = HealthCategory.Storage,
                            Title    = $"{name} health warning",
                            Detail   = "Windows flagged this disk. Back up important files and check it with the manufacturer's tool.",
                        });
                    else
                        passed.Add($"{name} reports healthy");
                }
            }

            if (anyDisk && !anySsd)
                issues.Add(new HealthIssue
                {
                    Severity = HealthSeverity.Info, Category = HealthCategory.Storage,
                    Title    = "No SSD detected",
                    Detail   = "Moving Windows and your games to an SSD is the single biggest upgrade for load times.",
                });
        }

        public static List<DiskHealth> GetDisks()
        {
            var list = new List<DiskHealth>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady || d.TotalSize == 0) continue;
                    list.Add(new DiskHealth
                    {
                        Name    = d.Name.TrimEnd('\\'),
                        TotalGB = d.TotalSize / 1_073_741_824.0,
                        FreeGB  = d.AvailableFreeSpace / 1_073_741_824.0,
                    });
                }
                catch { }
            }
            return list;
        }

        // ── Memory ───────────────────────────────────────────────────────────

        private static void CheckMemory(List<HealthIssue> issues, List<string> passed)
        {
            var (totalMB, availMB) = MemoryOptimizerService.GetMemoryStatus();
            if (totalMB == 0) return;

            double usedPct = (totalMB - availMB) * 100.0 / totalMB;
            double totalGB = totalMB / 1024.0;

            if (usedPct > 90)
                issues.Add(new HealthIssue
                {
                    Severity = HealthSeverity.Warning, Category = HealthCategory.Performance,
                    Title    = "Memory is nearly maxed out",
                    Detail   = $"{usedPct:F0}% of RAM in use. Close heavy apps or free memory to avoid stutter.",
                    Fix = HealthFix.OpenMemory, FixLabel = "Free memory",
                });
            else if (usedPct > 80)
                issues.Add(new HealthIssue
                {
                    Severity = HealthSeverity.Info, Category = HealthCategory.Performance,
                    Title    = "High memory usage",
                    Detail   = $"{usedPct:F0}% of RAM in use.",
                    Fix = HealthFix.OpenMemory, FixLabel = "Free memory",
                });
            else
                passed.Add("Memory usage is healthy");

            if (totalGB < 7.5)
                issues.Add(new HealthIssue
                {
                    Severity = HealthSeverity.Warning, Category = HealthCategory.Performance,
                    Title    = $"Only {totalGB:F0} GB of RAM",
                    Detail   = "Modern games and browsers want 16 GB. A RAM upgrade is cheap and very noticeable.",
                });
        }

        // ── Uptime & pending reboot ──────────────────────────────────────────

        private static void CheckUptimeAndReboot(List<HealthIssue> issues, List<string> passed)
        {
            var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
            bool rebootPending = IsRebootPending();

            if (rebootPending)
                issues.Add(new HealthIssue
                {
                    Severity = HealthSeverity.Warning, Category = HealthCategory.System,
                    Title    = "Restart required",
                    Detail   = "Windows has installed updates that only finish after a restart.",
                });
            else if (uptime.TotalDays > 7)
                issues.Add(new HealthIssue
                {
                    Severity = HealthSeverity.Warning, Category = HealthCategory.System,
                    Title    = $"No restart in {uptime.Days} days",
                    Detail   = "Long uptimes leak memory and delay updates. A restart clears it out.",
                });
            else
                passed.Add("No restart pending");
        }

        private static bool IsRebootPending()
        {
            string[] keys =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
            };
            foreach (var k in keys)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(k);
                    if (key != null) return true;
                }
                catch { }
            }
            return false;
        }

        // ── Junk ─────────────────────────────────────────────────────────────

        // shellapi.h packs this to 8 on x64 (natural alignment), which is the only target we ship.
        [StructLayout(LayoutKind.Sequential)]
        private struct SHQUERYRBINFO
        {
            public int  cbSize;
            public long i64Size;
            public long i64NumItems;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO info);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

        private static void CheckTempAndRecycleBin(List<HealthIssue> issues, List<string> passed)
        {
            long temp = GetTempBytes();
            double tempGB = temp / 1_073_741_824.0;
            if (tempGB > 2)
                issues.Add(new HealthIssue
                {
                    Severity = HealthSeverity.Warning, Category = HealthCategory.Storage,
                    Title    = $"{tempGB:F1} GB of temporary files",
                    Detail   = "Leftover installer and app temp files that are safe to delete.",
                    Fix = HealthFix.ClearTemp, FixLabel = "Clean now",
                });
            else if (temp > 500L * 1_048_576)
                issues.Add(new HealthIssue
                {
                    Severity = HealthSeverity.Info, Category = HealthCategory.Storage,
                    Title    = $"{temp / 1_048_576} MB of temporary files",
                    Detail   = "Safe to delete.",
                    Fix = HealthFix.ClearTemp, FixLabel = "Clean now",
                });
            else
                passed.Add("Temp folders are clean");

            var rb = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            if (SHQueryRecycleBin(null, ref rb) == 0 && rb.i64Size > 1_073_741_824)
                issues.Add(new HealthIssue
                {
                    Severity = rb.i64Size > 5L * 1_073_741_824 ? HealthSeverity.Warning : HealthSeverity.Info,
                    Category = HealthCategory.Storage,
                    Title    = $"Recycle Bin holds {rb.i64Size / 1_073_741_824.0:F1} GB",
                    Detail   = $"{rb.i64NumItems} deleted items are still using disk space.",
                    Fix = HealthFix.EmptyRecycleBin, FixLabel = "Empty bin",
                });
        }

        private static long GetTempBytes()
        {
            long total = 0;
            var sw = Stopwatch.StartNew();
            foreach (var dir in new[] { Path.GetTempPath(), @"C:\Windows\Temp" })
            {
                try
                {
                    var opts = new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", opts))
                    {
                        try { total += f.Length; } catch { }
                        if (sw.Elapsed.TotalSeconds > 15) return total; // good enough estimate
                    }
                }
                catch { }
            }
            return total;
        }

        // ── Startup ──────────────────────────────────────────────────────────

        private static async Task<(List<HealthIssue>, List<string>)> CheckStartupAsync()
        {
            var issues = new List<HealthIssue>();
            var passed = new List<string>();
            try
            {
                var entries = await StartupManagerService.GetStartupEntriesAsync();
                int enabled = entries.Count(e => e.IsEnabled);
                int heavy   = entries.Count(e => e.IsEnabled && e.Impact == StartupImpact.High);

                if (enabled >= 15 || heavy >= 4)
                    issues.Add(new HealthIssue
                    {
                        Severity = HealthSeverity.Warning, Category = HealthCategory.Performance,
                        Title    = $"{enabled} apps launch at startup",
                        Detail   = $"{heavy} of them are high impact. Disabling the ones you don't need speeds up boot.",
                        Fix = HealthFix.OpenStartup, FixLabel = "Review startup",
                    });
                else if (enabled >= 8)
                    issues.Add(new HealthIssue
                    {
                        Severity = HealthSeverity.Info, Category = HealthCategory.Performance,
                        Title    = $"{enabled} apps launch at startup",
                        Detail   = "Trimming startup apps makes boot faster.",
                        Fix = HealthFix.OpenStartup, FixLabel = "Review startup",
                    });
                else
                    passed.Add("Startup is lean");
            }
            catch { }
            return (issues, passed);
        }

        // ── Security ─────────────────────────────────────────────────────────

        private static async Task<(List<HealthIssue>, List<string>)> CheckSecurityAsync()
        {
            var issues = new List<HealthIssue>();
            var passed = new List<string>();
            try
            {
                var defender = await AntivirusService.GetDefenderStatusAsync();
                var products = await AntivirusService.GetInstalledAntivirusAsync();
                var otherAv  = products.FirstOrDefault(p => !p.IsDefender && p.Enabled);

                bool defenderActive = defender.Available && defender.RealTimeEnabled && !defender.IsPassive;

                if (!defenderActive && otherAv is null)
                    issues.Add(new HealthIssue
                    {
                        Severity = HealthSeverity.Critical, Category = HealthCategory.Security,
                        Title    = "No active antivirus protection",
                        Detail   = "Real-time protection is off and no other antivirus is running.",
                        Fix = HealthFix.OpenSecurity, FixLabel = "Open Security",
                    });
                else
                    passed.Add(otherAv != null && !defenderActive
                        ? $"Protected by {otherAv.Name}"
                        : "Real-time protection is on");

                if (defenderActive)
                {
                    if (defender.SignatureAgeDays is > 3)
                        issues.Add(new HealthIssue
                        {
                            Severity = defender.SignatureAgeDays > 7 ? HealthSeverity.Critical : HealthSeverity.Warning,
                            Category = HealthCategory.Security,
                            Title    = $"Virus definitions are {defender.SignatureAgeDays} days old",
                            Detail   = "New threats appear daily. Update security intelligence.",
                            Fix = HealthFix.UpdateDefender, FixLabel = "Update now",
                        });
                    else
                        passed.Add("Virus definitions are current");

                    if (defender.QuickScanAgeDays is null or > 14)
                        issues.Add(new HealthIssue
                        {
                            Severity = HealthSeverity.Warning, Category = HealthCategory.Security,
                            Title    = defender.QuickScanAgeDays is null
                                ? "No antivirus scan on record"
                                : $"Last scan was {defender.QuickScanAgeDays} days ago",
                            Detail   = "Run a quick scan to make sure nothing slipped through.",
                            Fix = HealthFix.OpenSecurity, FixLabel = "Scan now",
                        });
                    else
                        passed.Add("Scanned recently");
                }
                else if (otherAv is { UpToDate: false })
                {
                    issues.Add(new HealthIssue
                    {
                        Severity = HealthSeverity.Warning, Category = HealthCategory.Security,
                        Title    = $"{otherAv.Name} definitions are out of date",
                        Detail   = "Open it and run an update.",
                        Fix = HealthFix.OpenSecurity, FixLabel = "Open Security",
                    });
                }

                var active = (await AntivirusService.GetDefenderThreatsAsync()).Count(t => t.IsActive);
                if (active > 0)
                    issues.Add(new HealthIssue
                    {
                        Severity = HealthSeverity.Critical, Category = HealthCategory.Security,
                        Title    = $"{active} unresolved threat(s)",
                        Detail   = "Defender found threats that haven't been removed yet.",
                        Fix = HealthFix.OpenSecurity, FixLabel = "Review threats",
                    });
            }
            catch { }
            return (issues, passed);
        }

        private static void CheckFirewall(List<HealthIssue> issues, List<string> passed)
        {
            const string root = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
            var off = new List<string>();
            foreach (var (profile, label) in new[] { ("StandardProfile", "Private"), ("PublicProfile", "Public") })
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{root}\{profile}");
                if (key?.GetValue("EnableFirewall") is int v && v == 0) off.Add(label);
            }

            if (off.Count > 0)
                issues.Add(new HealthIssue
                {
                    Severity = HealthSeverity.Warning, Category = HealthCategory.Security,
                    Title    = $"Windows Firewall is off ({string.Join(", ", off)})",
                    Detail   = "Unless another firewall is protecting you, turn it back on.",
                    Fix = HealthFix.OpenFirewallSettings, FixLabel = "Open firewall",
                });
            else
                passed.Add("Firewall is on");
        }

        // ── Gaming ───────────────────────────────────────────────────────────

        private static void CheckGamingSettings(List<HealthIssue> issues, List<string> passed)
        {
            var plan = GetActivePowerPlanName();
            if (plan?.Contains("saver", StringComparison.OrdinalIgnoreCase) == true)
                issues.Add(new HealthIssue
                {
                    Severity = HealthSeverity.Warning, Category = HealthCategory.Performance,
                    Title    = "Power Saver plan is active",
                    Detail   = "Your CPU is being throttled. Game Optimizer switches to Ultimate Performance while you play.",
                    Fix = HealthFix.OpenGameOptimizer, FixLabel = "Optimize",
                });
            else if (plan != null)
                passed.Add($"Power plan: {plan}");

            if (!GameOptimizerService.IsGameModeEnabled())
                issues.Add(new HealthIssue
                {
                    Severity = HealthSeverity.Info, Category = HealthCategory.Performance,
                    Title    = "Windows Game Mode is off",
                    Detail   = "Game Mode stops Windows Update and background tasks from interrupting games.",
                    Fix = HealthFix.EnableGameMode, FixLabel = "Turn on",
                });
            else
                passed.Add("Game Mode is on");

            // HwSchMode only exists when the GPU/driver supports HAGS: 1 = off, 2 = on
            using var gfx = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            if (gfx?.GetValue("HwSchMode") is int hags && hags == 1)
                issues.Add(new HealthIssue
                {
                    Severity = HealthSeverity.Info, Category = HealthCategory.Performance,
                    Title    = "GPU scheduling is off",
                    Detail   = "Hardware-accelerated GPU scheduling can lower latency. Needs a restart.",
                    Fix = HealthFix.EnableHags, FixLabel = "Turn on",
                });

            if (GameOptimizerService.SnapshotFileExists)
                issues.Add(new HealthIssue
                {
                    Severity = HealthSeverity.Info, Category = HealthCategory.Performance,
                    Title    = "Game optimizations are still active",
                    Detail   = "Restore them when you're done gaming to get normal power and network settings back.",
                    Fix = HealthFix.OpenGameOptimizer, FixLabel = "Restore",
                });
        }

        public static string? GetActivePowerPlanName()
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("powercfg", "/getactivescheme")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                });
                if (p is null) return null;
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(10_000);
                // "Power Scheme GUID: … (Name)"; the name itself may contain parentheses
                int open = output.IndexOf('('), close = output.LastIndexOf(')');
                return open >= 0 && close > open ? output[(open + 1)..close].Trim() : null;
            }
            catch { return null; }
        }

        // ── Fixes ────────────────────────────────────────────────────────────

        /// <summary>Applies an in-place fix. Returns a short result message for the UI.</summary>
        public static async Task<string> ApplyFixAsync(HealthFix fix)
        {
            switch (fix)
            {
                case HealthFix.ClearTemp:
                    await MemoryOptimizerService.ClearTempFilesAsync();
                    AppEvents.RaiseDriveStatsChanged();
                    return "Temporary files cleared.";

                case HealthFix.EmptyRecycleBin:
                    const uint noConfirm = 0x1, noProgress = 0x2, noSound = 0x4;
                    await Task.Run(() => SHEmptyRecycleBin(IntPtr.Zero, null, noConfirm | noProgress | noSound));
                    AppEvents.RaiseDriveStatsChanged();
                    return "Recycle Bin emptied.";

                case HealthFix.UpdateDefender:
                    return await AntivirusService.UpdateDefenderSignaturesAsync(null)
                        ? "Virus definitions updated."
                        : "Update failed. Try running as Administrator.";

                case HealthFix.EnableGameMode:
                    using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar"))
                    {
                        key.SetValue("AllowAutoGameMode", 1, RegistryValueKind.DWord);
                        key.SetValue("AutoGameModeEnabled", 1, RegistryValueKind.DWord);
                    }
                    return "Game Mode turned on.";

                case HealthFix.EnableHags:
                    if (!AdminHelper.EnsureElevatedOrRelaunch()) return "Administrator required.";
                    using (var key = Registry.LocalMachine.OpenSubKey(
                               @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", writable: true))
                    {
                        key?.SetValue("HwSchMode", 2, RegistryValueKind.DWord);
                    }
                    return "GPU scheduling turned on. Restart your PC to apply.";

                case HealthFix.OpenFirewallSettings:
                    Process.Start(new ProcessStartInfo("windowsdefender://network") { UseShellExecute = true });
                    return "Opened Windows Security.";

                default:
                    return string.Empty;
            }
        }

        // ── System info ──────────────────────────────────────────────────────

        private static string GetCpuName()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "—";
            }
            catch { return "—"; }
        }

        private static string GetGpuName()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController");
                var names = new List<string>();
                foreach (ManagementObject mo in searcher.Get())
                    using (mo)
                    {
                        var n = mo["Name"] as string;
                        if (!string.IsNullOrEmpty(n) && !n.Contains("Basic", StringComparison.OrdinalIgnoreCase))
                            names.Add(n);
                    }
                // Prefer a dedicated GPU over integrated graphics when both are present
                return names.FirstOrDefault(n => Regex.IsMatch(n, "NVIDIA|Radeon RX|Arc", RegexOptions.IgnoreCase))
                       ?? names.FirstOrDefault() ?? "—";
            }
            catch { return "—"; }
        }

        private static string GetOsName()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                var name    = key?.GetValue("ProductName") as string ?? "Windows";
                var version = key?.GetValue("DisplayVersion") as string;
                int.TryParse(key?.GetValue("CurrentBuild") as string, out var build);
                // ProductName still says "Windows 10" on Windows 11
                if (build >= 22000) name = name.Replace("Windows 10", "Windows 11");
                return version is null ? name : $"{name} {version}";
            }
            catch { return "Windows"; }
        }
    }

    /// <summary>Total CPU usage between calls, from GetSystemTimes (no PerformanceCounter warm-up).</summary>
    public sealed class CpuUsageSampler
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        private long _idle, _kernel, _user;

        public CpuUsageSampler() => GetSystemTimes(out _idle, out _kernel, out _user);

        public double Sample()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
            long dIdle = idle - _idle, dKernel = kernel - _kernel, dUser = user - _user;
            (_idle, _kernel, _user) = (idle, kernel, user);

            long total = dKernel + dUser; // kernel time includes idle
            return total <= 0 ? 0 : Math.Clamp((total - dIdle) * 100.0 / total, 0, 100);
        }
    }
}
