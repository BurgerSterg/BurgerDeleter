using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BurgerDeleter.Models;
using Microsoft.Win32;

namespace BurgerDeleter.Services
{
    public static class GameOptimizerService
    {
        public const string SnapshotFileName = "optimizer_snapshot.json";

        public static string SnapshotPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, SnapshotFileName);

        private const string UltimatePerfTemplateGuid = "e9a42b02-d5df-448d-aa00-03f14749eb61";
        private const string HighPerformanceGuid = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

        private static readonly Regex GuidRx = new(
            @"[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}",
            RegexOptions.Compiled);

        /// <summary>Games where killing Steam would likely break the launch path.</summary>
        private static readonly HashSet<string> SteamLikelyGames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CS2", "Apex Legends", "Rocket League", "NBA 2K",
        };

        private static readonly string[] BackgroundKillProcessNames =
        {
            "Discord", "EpicGamesLauncher", "Steam", "msedge", "chrome", "firefox",
            "OneDrive", "SearchApp", "SearchIndexer", "XboxPcApp", "XboxApp",
            "GameBarPresenceWriter", "GameBar", "YourPhone", "Spotify",
        };

        public static void SaveSnapshotToDisk(OptimizationSnapshot snapshot)
        {
            try
            {
                var opts = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Converters = { new JsonStringEnumConverter() },
                };
                var json = JsonSerializer.Serialize(snapshot, opts);
                File.WriteAllText(SnapshotPath, json);
            }
            catch { /* ignore */ }
        }

        public static OptimizationSnapshot? LoadSnapshotFromDisk()
        {
            try
            {
                if (!File.Exists(SnapshotPath)) return null;
                var opts = new JsonSerializerOptions
                {
                    Converters = { new JsonStringEnumConverter() },
                };
                return JsonSerializer.Deserialize<OptimizationSnapshot>(
                    File.ReadAllText(SnapshotPath), opts);
            }
            catch
            {
                return null;
            }
        }

        public static void DeleteSnapshotFile()
        {
            try
            {
                if (File.Exists(SnapshotPath))
                    File.Delete(SnapshotPath);
            }
            catch { /* ignore */ }
        }

        public static bool SnapshotFileExists => File.Exists(SnapshotPath);

        // ── Background kills ────────────────────────────────────────────────

        public static Task<int> KillBackgroundProcessesAsync(
            string selectedGameName,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                bool skipSteam = SteamLikelyGames.Contains(selectedGameName.Trim());
                int killed = 0;

                foreach (var name in BackgroundKillProcessNames)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (skipSteam &&
                        name.Equals("Steam", StringComparison.OrdinalIgnoreCase))
                        continue;

                    Process[] procs;
                    try { procs = Process.GetProcessesByName(name); }
                    catch { continue; }

                    foreach (var p in procs)
                    {
                        try
                        {
                            progress?.Report($"Stopping {p.ProcessName}…");
                            p.Kill(entireProcessTree: false);
                            killed++;
                        }
                        catch { /* skip */ }
                        finally { p.Dispose(); }
                    }
                }

                return killed;
            }, cancellationToken);
        }

        // ── Game priority ───────────────────────────────────────────────────

        public static void SetGamePriorityHigh(string gameName, OptimizationSnapshot snapshot)
        {
            foreach (var (proc, _) in FindGameProcesses(gameName, snapshot.GameInstallPath))
            {
                try
                {
                    var orig = proc.PriorityClass;
                    snapshot.ProcessPriorities.Add(new ProcessPriorityBackup
                    {
                        ProcessId        = proc.Id,
                        OriginalPriority = orig.ToString(),
                    });
                    proc.PriorityClass = ProcessPriorityClass.High;
                }
                catch { /* access denied etc. */ }
                finally { try { proc.Dispose(); } catch { } }
            }
        }

        /// <summary>Exe paths of the game's running processes (for GPU preference).</summary>
        public static List<string> GetRunningGameExePaths(string gameName, string? installPath)
        {
            var paths = new List<string>();
            foreach (var (proc, path) in FindGameProcesses(gameName, installPath))
            {
                if (!string.IsNullOrEmpty(path)) paths.Add(path);
                proc.Dispose();
            }
            return paths;
        }

        /// <summary>
        /// Running processes belonging to the game: by exe path under the install
        /// folder when known, otherwise by the preset process-name patterns.
        /// Caller disposes the returned processes.
        /// </summary>
        private static List<(Process Proc, string? Path)> FindGameProcesses(string gameName, string? installPath)
        {
            var result   = new List<(Process, string?)>();
            var patterns = GetGameProcessPatterns(gameName);
            string? root = string.IsNullOrEmpty(installPath)
                ? null
                : Path.TrimEndingDirectorySeparator(installPath) + Path.DirectorySeparatorChar;

            if (root is null && patterns.Count == 0) return result;

            foreach (var proc in Process.GetProcesses())
            {
                bool keep = false;
                string? path = null;
                try
                {
                    path = GetProcessImagePath(proc.Id);
                    if (root != null)
                        keep = path != null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
                    else
                        keep = patterns.Any(pat => proc.ProcessName.Contains(pat, StringComparison.OrdinalIgnoreCase));
                }
                catch { }

                if (keep) result.Add((proc, path));
                else proc.Dispose();
            }
            return result;
        }

        /// <summary>
        /// Looks for a running process whose exe lives inside one of the detected
        /// game folders. Returns the game using the most memory (the one being played).
        /// </summary>
        public static Task<DriveItem?> DetectRunningGameAsync(IReadOnlyList<DriveItem> installedGames)
        {
            return Task.Run(() =>
            {
                var roots = installedGames
                    .Where(g => !string.IsNullOrEmpty(g.FullPath))
                    .Select(g => (Game: g, Root: Path.TrimEndingDirectorySeparator(g.FullPath) + Path.DirectorySeparatorChar))
                    .ToList();

                DriveItem? best = null;
                long bestMem = 0;

                foreach (var proc in Process.GetProcesses())
                {
                    try
                    {
                        var path = GetProcessImagePath(proc.Id);
                        if (path is null) continue;

                        var match = roots.FirstOrDefault(r => path.StartsWith(r.Root, StringComparison.OrdinalIgnoreCase));
                        if (match.Game is null) continue;

                        long mem = proc.WorkingSet64;
                        if (mem > bestMem) { bestMem = mem; best = match.Game; }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
                return best;
            });
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        /// <summary>
        /// Exe path via the limited-access query, which still works for elevated
        /// and anti-cheat-protected game processes where Process.MainModule throws.
        /// </summary>
        private static string? GetProcessImagePath(int pid)
        {
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        private static List<string> GetGameProcessPatterns(string gameName)
        {
            var g = gameName.Trim();
            return g switch
            {
                "Valorant"           => new List<string> { "VALORANT-Win64-Shipping" },
                "Call of Duty"       => new List<string> { "cod", "ModernWarfare", "BlackOps" },
                "Fortnite"           => new List<string> { "FortniteClient-Win64-Shipping" },
                "Apex Legends"       => new List<string> { "r5apex" },
                "Rocket League"      => new List<string> { "RocketLeague" },
                "NBA 2K"             => new List<string> { "NBA2K" },
                "CS2"                => new List<string> { "cs2" },
                "Rainbow Six Siege"  => new List<string> { "RainbowSix", "RainbowSix_BE" },
                "Other"              => new List<string>(),
                _                    => new List<string>(),
            };
        }

        // ── Power plan ───────────────────────────────────────────────────────

        public static void SetUltimatePerformancePlan(OptimizationSnapshot snapshot)
        {
            try
            {
                var prev = GetActivePowerSchemeGuid();
                if (!string.IsNullOrEmpty(prev))
                    snapshot.PreviousPowerSchemeGuid = prev;

                var ultimateGuid = EnsureUltimatePerformanceSchemeGuid();
                if (!string.IsNullOrEmpty(ultimateGuid))
                {
                    RunHidden("powercfg", $"/s {ultimateGuid}");
                    return;
                }

                snapshot.UsedHighPerformanceFallback = true;
                RunHidden("powercfg", $"/s {HighPerformanceGuid}");
            }
            catch
            {
                try
                {
                    snapshot.UsedHighPerformanceFallback = true;
                    RunHidden("powercfg", $"/s {HighPerformanceGuid}");
                }
                catch { /* ignore */ }
            }
        }

        private static string? GetActivePowerSchemeGuid()
        {
            try
            {
                var (ok, stdout, _) = RunHiddenCapture("powercfg", "/getactivescheme");
                if (!ok || string.IsNullOrEmpty(stdout)) return null;
                return GuidRx.Match(stdout).Value is { Length: > 0 } m ? m : null;
            }
            catch { return null; }
        }

        private static string? EnsureUltimatePerformanceSchemeGuid()
        {
            try
            {
                var (ok, stdout, _) = RunHiddenCapture(
                    "powercfg", $"/duplicatescheme {UltimatePerfTemplateGuid}");
                if (!ok || string.IsNullOrEmpty(stdout)) return null;
                return GuidRx.Match(stdout).Value is { Length: > 0 } m ? m : null;
            }
            catch { return null; }
        }

        // ── Game bar registry ────────────────────────────────────────────────

        /// <summary>
        /// Turns off Game Bar background recording (Game DVR / capture).
        /// Game Mode is deliberately left alone — it helps, and has its own toggle.
        /// </summary>
        public static void DisableGameBar(OptimizationSnapshot snapshot)
        {
            BackupAndSetDword(snapshot.GameBarRegistry, RegistryHiveKind.CurrentUser,
                @"System\GameConfigStore", "GameDVR_Enabled", 0);
            BackupAndSetDword(snapshot.GameBarRegistry, RegistryHiveKind.CurrentUser,
                @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 0);
        }

        // ── Game Mode ─────────────────────────────────────────────────────────

        public static void EnableGameMode(OptimizationSnapshot snapshot)
        {
            BackupAndSetDword(snapshot.RegistryTweaks, RegistryHiveKind.CurrentUser,
                @"Software\Microsoft\GameBar", "AllowAutoGameMode", 1);
            BackupAndSetDword(snapshot.RegistryTweaks, RegistryHiveKind.CurrentUser,
                @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1);
        }

        public static bool IsGameModeEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar");
                // Missing value means Windows default, which is on.
                return key?.GetValue("AutoGameModeEnabled") is not int v || v != 0;
            }
            catch { return true; }
        }

        // ── Multimedia scheduler (MMCSS) ─────────────────────────────────────

        /// <summary>
        /// Gives the "Games" MMCSS task more CPU/GPU share and reserves less CPU
        /// for background multimedia. Requires Administrator (HKLM).
        /// </summary>
        public static int PrioritizeGamesInScheduler(OptimizationSnapshot snapshot)
        {
            const string profile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
            int before = snapshot.RegistryTweaks.Count;

            BackupAndSetDword(snapshot.RegistryTweaks, RegistryHiveKind.LocalMachine,
                profile, "SystemResponsiveness", 10);
            BackupAndSetDword(snapshot.RegistryTweaks, RegistryHiveKind.LocalMachine,
                $@"{profile}\Tasks\Games", "GPU Priority", 8);
            BackupAndSetDword(snapshot.RegistryTweaks, RegistryHiveKind.LocalMachine,
                $@"{profile}\Tasks\Games", "Priority", 6);

            return snapshot.RegistryTweaks.Count - before;
        }

        // ── Mouse acceleration ───────────────────────────────────────────────

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);

        private const uint SPI_SETMOUSE = 0x0004;
        private const uint SPIF_UPDATEINIFILE = 0x01;
        private const uint SPIF_SENDCHANGE = 0x02;

        /// <summary>Turns off "Enhance pointer precision" so aim is 1:1 with mouse movement.</summary>
        public static void DisableMouseAcceleration(OptimizationSnapshot snapshot)
        {
            const string mouse = @"Control Panel\Mouse";
            BackupAndSetString(snapshot.RegistryStrings, RegistryHiveKind.CurrentUser, mouse, "MouseSpeed", "0");
            BackupAndSetString(snapshot.RegistryStrings, RegistryHiveKind.CurrentUser, mouse, "MouseThreshold1", "0");
            BackupAndSetString(snapshot.RegistryStrings, RegistryHiveKind.CurrentUser, mouse, "MouseThreshold2", "0");

            snapshot.MouseSettingsChanged = true;
            ApplyMouseSettingsFromRegistry();
        }

        /// <summary>Pushes the registry mouse values into the live session (no sign-out needed).</summary>
        private static void ApplyMouseSettingsFromRegistry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
                int Read(string name, int fallback)
                    => int.TryParse(key?.GetValue(name) as string, out var v) ? v : fallback;

                var values = new[]
                {
                    Read("MouseThreshold1", 6),
                    Read("MouseThreshold2", 10),
                    Read("MouseSpeed", 1),
                };
                SystemParametersInfo(SPI_SETMOUSE, 0, values, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            }
            catch { /* ignore */ }
        }

        // ── Per-game GPU preference ──────────────────────────────────────────

        private const string GpuPreferencesKey = @"Software\Microsoft\DirectX\UserGpuPreferences";

        /// <summary>
        /// Tells Windows to run each game exe on the high-performance GPU
        /// (matters on laptops with integrated + dedicated graphics).
        /// </summary>
        public static int SetHighPerformanceGpu(IEnumerable<string> exePaths, OptimizationSnapshot snapshot)
        {
            int set = 0;
            foreach (var exe in exePaths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                int before = snapshot.RegistryStrings.Count;
                BackupAndSetString(snapshot.RegistryStrings, RegistryHiveKind.CurrentUser,
                    GpuPreferencesKey, exe, "GpuPreference=2;");
                if (snapshot.RegistryStrings.Count > before) set++;
            }
            return set;
        }

        private static void BackupAndSetDword(
            List<RegistryDwordBackup> list,
            RegistryHiveKind hiveKind,
            string keyPath,
            string valueName,
            int newValue)
        {
            try
            {
                using var root = OpenBaseKey(hiveKind);
                using var key = root.OpenSubKey(keyPath, writable: true)
                    ?? root.CreateSubKey(keyPath);

                var backup = new RegistryDwordBackup
                {
                    Hive       = hiveKind,
                    KeyPath    = keyPath,
                    ValueName  = valueName,
                };

                var existing = key.GetValue(valueName);
                if (existing is int i)
                {
                    backup.ValueExisted  = true;
                    backup.OriginalValue = i;
                }
                else
                {
                    backup.ValueExisted = existing != null;
                    if (existing is long l) { backup.ValueExisted = true; backup.OriginalValue = (int)l; }
                }

                key.SetValue(valueName, newValue, RegistryValueKind.DWord);
                list.Add(backup);
            }
            catch { /* skip */ }
        }

        private static void BackupAndSetString(
            List<RegistryStringBackup> list,
            RegistryHiveKind hiveKind,
            string keyPath,
            string valueName,
            string newValue)
        {
            try
            {
                using var root = OpenBaseKey(hiveKind);
                using var key = root.OpenSubKey(keyPath, writable: true)
                    ?? root.CreateSubKey(keyPath);

                var existing = key.GetValue(valueName);
                var backup = new RegistryStringBackup
                {
                    Hive          = hiveKind,
                    KeyPath       = keyPath,
                    ValueName     = valueName,
                    ValueExisted  = existing != null,
                    OriginalValue = existing?.ToString(),
                };

                key.SetValue(valueName, newValue, RegistryValueKind.String);
                list.Add(backup);
            }
            catch { /* skip */ }
        }

        private static void RestoreDwords(IEnumerable<RegistryDwordBackup> backups)
        {
            foreach (var reg in backups)
            {
                try
                {
                    using var root = OpenBaseKey(reg.Hive);
                    using var key = root.OpenSubKey(reg.KeyPath, writable: true);
                    if (key is null) continue;

                    if (!reg.ValueExisted)
                        key.DeleteValue(reg.ValueName, throwOnMissingValue: false);
                    else if (reg.OriginalValue.HasValue)
                        key.SetValue(reg.ValueName, reg.OriginalValue.Value,
                            RegistryValueKind.DWord);
                }
                catch { /* ignore */ }
            }
        }

        private static void RestoreStrings(IEnumerable<RegistryStringBackup> backups)
        {
            foreach (var reg in backups)
            {
                try
                {
                    using var root = OpenBaseKey(reg.Hive);
                    using var key = root.OpenSubKey(reg.KeyPath, writable: true);
                    if (key is null) continue;

                    if (!reg.ValueExisted || reg.OriginalValue is null)
                        key.DeleteValue(reg.ValueName, throwOnMissingValue: false);
                    else
                        key.SetValue(reg.ValueName, reg.OriginalValue, RegistryValueKind.String);
                }
                catch { /* ignore */ }
            }
        }

        // ── RAM flush ─────────────────────────────────────────────────────────

        public static Task FlushRamAsync(IProgress<string>? progress = null)
            => MemoryOptimizerService.TrimWorkingSetsAsync(progress);

        // ── DNS ───────────────────────────────────────────────────────────────

        public static Task FlushDnsAsync(IProgress<string>? progress = null)
            => MemoryOptimizerService.FlushDnsAsync(progress);

        // ── Nagle ─────────────────────────────────────────────────────────────

        public static void DisableNaglesAlgorithm(OptimizationSnapshot snapshot)
        {
            const string interfacesPath =
                @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";

            try
            {
                using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                    RegistryView.Registry64);
                using var interfaces = lm.OpenSubKey(interfacesPath, writable: true);
                if (interfaces is null) return;

                foreach (var subName in interfaces.GetSubKeyNames())
                {
                    try
                    {
                        using var ifaceKey = interfaces.OpenSubKey(subName, writable: true);
                        if (ifaceKey is null) continue;

                        var back = new InterfaceNagleBackup { InterfaceSubKey = subName };

                        object? ackObj = ifaceKey.GetValue("TcpAckFrequency");
                        if (ackObj is int ackI)
                        {
                            back.HadTcpAckFrequency     = true;
                            back.OriginalTcpAckFrequency = ackI;
                        }
                        else if (ackObj is long ackL)
                        {
                            back.HadTcpAckFrequency     = true;
                            back.OriginalTcpAckFrequency = (int)ackL;
                        }

                        object? ndObj = ifaceKey.GetValue("TCPNoDelay");
                        if (ndObj is int ndI)
                        {
                            back.HadTCPNoDelay     = true;
                            back.OriginalTCPNoDelay = ndI;
                        }
                        else if (ndObj is long ndL)
                        {
                            back.HadTCPNoDelay     = true;
                            back.OriginalTCPNoDelay = (int)ndL;
                        }

                        ifaceKey.SetValue("TcpAckFrequency", 1, RegistryValueKind.DWord);
                        ifaceKey.SetValue("TCPNoDelay", 1, RegistryValueKind.DWord);

                        snapshot.NagleInterfaces.Add(back);
                    }
                    catch { /* skip interface */ }
                }
            }
            catch { /* no admin / access */ }
        }

        // ── Network (netsh) ───────────────────────────────────────────────────

        public static async Task SetNetworkAdapterMaxPerformanceAsync(OptimizationSnapshot snapshot)
        {
            await Task.Run(() =>
            {
                try
                {
                    var (ok, stdout, _) = RunHiddenCapture("netsh", "int tcp show global");
                    if (ok && !string.IsNullOrEmpty(stdout))
                        snapshot.NetshTcpBefore = ParseNetshTcpGlobal(stdout);

                    RunHidden("netsh", "int tcp set global autotuninglevel=normal");
                    RunHidden("netsh", "int tcp set global chimney=disabled");
                    RunHidden("netsh", "int tcp set global rss=enabled");
                }
                catch { /* ignore */ }
            });
        }

        private static NetshTcpGlobalBackup ParseNetshTcpGlobal(string text)
        {
            var b = new NetshTcpGlobalBackup();

            string? LineValue(string labelPrefix)
            {
                foreach (var line in text.Split('\n'))
                {
                    var t = line.Trim();
                    if (t.StartsWith(labelPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var idx = t.IndexOf(':');
                        if (idx >= 0 && idx < t.Length - 1)
                            return t[(idx + 1)..].Trim();
                    }
                }
                return null;
            }

            var tune = LineValue("Receive Window Auto-Tuning Level");
            if (!string.IsNullOrEmpty(tune))
                b.AutoTuneLevel = MapAutoTuneToNetsh(tune);

            var chim = LineValue("Chimney Offload State");
            if (!string.IsNullOrEmpty(chim))
                b.Chimney = MapChimneyToNetsh(chim);

            var rss = LineValue("Receive Side Scaling State");
            if (!string.IsNullOrEmpty(rss))
                b.Rss = MapRssToNetsh(rss);

            return b;
        }

        private static string MapAutoTuneToNetsh(string display)
            => display.Replace(" ", "").ToLowerInvariant() switch
            {
                "disabled"         => "disabled",
                "highlyrestricted" => "highlyrestricted",
                "restricted"       => "restricted",
                "normal"           => "normal",
                "experimental"     => "experimental",
                _                  => "normal",
            };

        private static string MapChimneyToNetsh(string display)
            => display.ToLowerInvariant() switch
            {
                var s when s.Contains("disabled")   => "disabled",
                var s when s.Contains("enabled")    => "enabled",
                var s when s.Contains("automatic")  => "automatic",
                _                                   => "automatic",
            };

        private static string MapRssToNetsh(string display)
            => display.ToLowerInvariant() switch
            {
                var s when s.Contains("disabled") => "disabled",
                _                                 => "enabled",
            };

        // ── Restore ───────────────────────────────────────────────────────────

        public static async Task RestoreAllAsync(OptimizationSnapshot snapshot)
        {
            await Task.Run(() =>
            {
                // Process priorities
                foreach (var pb in snapshot.ProcessPriorities)
                {
                    try
                    {
                        using var p = Process.GetProcessById(pb.ProcessId);
                        if (Enum.TryParse<ProcessPriorityClass>(pb.OriginalPriority, out var cls))
                            p.PriorityClass = cls;
                    }
                    catch { /* exited or denied */ }
                }

                // Power scheme
                if (!string.IsNullOrEmpty(snapshot.PreviousPowerSchemeGuid))
                {
                    try
                    {
                        RunHidden("powercfg", $"/s {snapshot.PreviousPowerSchemeGuid}");
                    }
                    catch { /* ignore */ }
                }

                // Registry tweaks (Game Bar, Game Mode, scheduler, mouse, GPU preference)
                RestoreDwords(snapshot.GameBarRegistry);
                RestoreDwords(snapshot.RegistryTweaks);
                RestoreStrings(snapshot.RegistryStrings);
                if (snapshot.MouseSettingsChanged)
                    ApplyMouseSettingsFromRegistry();

                // Nagle
                foreach (var nb in snapshot.NagleInterfaces)
                {
                    try
                    {
                        const string path =
                            @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
                        using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,
                            RegistryView.Registry64);
                        using var ifaceKey = lm.OpenSubKey(
                            $@"{path}\{nb.InterfaceSubKey}", writable: true);
                        if (ifaceKey is null) continue;

                        void RestoreVal(string name, bool had, int? orig)
                        {
                            if (!had)
                                ifaceKey.DeleteValue(name, throwOnMissingValue: false);
                            else if (orig.HasValue)
                                ifaceKey.SetValue(name, orig.Value, RegistryValueKind.DWord);
                        }

                        RestoreVal("TcpAckFrequency", nb.HadTcpAckFrequency,
                            nb.OriginalTcpAckFrequency);
                        RestoreVal("TCPNoDelay", nb.HadTCPNoDelay, nb.OriginalTCPNoDelay);
                    }
                    catch { /* ignore */ }
                }

                // netsh restore
                if (snapshot.NetshTcpBefore is { } n)
                {
                    try
                    {
                        RunHidden("netsh", $"int tcp set global autotuninglevel={n.AutoTuneLevel}");
                        RunHidden("netsh", $"int tcp set global chimney={n.Chimney}");
                        RunHidden("netsh", $"int tcp set global rss={n.Rss}");
                    }
                    catch { /* ignore */ }
                }
            });
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static RegistryKey OpenBaseKey(RegistryHiveKind hive)
        {
            var h = hive switch
            {
                RegistryHiveKind.LocalMachine => RegistryHive.LocalMachine,
                _                             => RegistryHive.CurrentUser,
            };

            return RegistryKey.OpenBaseKey(h, RegistryView.Registry64);
        }

        private static void RunHidden(string fileName, string arguments)
        {
            using var p = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute  = false,
                CreateNoWindow   = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            });
            p?.WaitForExit(60_000);
        }

        private static (bool ok, string stdout, string stderr) RunHiddenCapture(
            string fileName,
            string arguments)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                });
                if (p is null) return (false, "", "");
                string o = p.StandardOutput.ReadToEnd();
                string e = p.StandardError.ReadToEnd();
                p.WaitForExit(60_000);
                return (p.ExitCode == 0, o, e);
            }
            catch
            {
                return (false, "", "");
            }
        }
    }
}
