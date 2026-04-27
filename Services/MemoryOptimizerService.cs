using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;

namespace BurgerDeleter.Services
{
    // ===== DATA MODEL =====

    public class ProcessInfo : INotifyPropertyChanged
    {
        private bool _isSelected = true;

        public int    Id       { get; init; }
        public string Name     { get; init; } = string.Empty;
        public long   MemoryMB { get; init; }
        public string Display  => $"{Name}  —  {MemoryMB} MB";

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // ===== SERVICE =====

    public static class MemoryOptimizerService
    {
        // ----- P/Invoke -----

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint   dwLength;
            public uint   dwMemoryLoad;
            public ulong  ullTotalPhys;
            public ulong  ullAvailPhys;
            public ulong  ullTotalPageFile;
            public ulong  ullAvailPageFile;
            public ulong  ullTotalVirtual;
            public ulong  ullAvailVirtual;
            public ulong  ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(
            IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        [DllImport("ntdll.dll")]
        private static extern uint NtSetSystemInformation(int InfoClass, IntPtr Info, int Length);

        // ----- Memory stats -----

        public static (ulong totalMB, ulong availMB) GetMemoryStatus()
        {
            var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref ms))
                return (ms.ullTotalPhys / 1_048_576UL, ms.ullAvailPhys / 1_048_576UL);
            return (0, 0);
        }

        // ----- Actions -----

        /// <summary>Trims the working set of every accessible process.</summary>
        public static async Task TrimWorkingSetsAsync(IProgress<string>? progress = null)
        {
            await Task.Run(() =>
            {
                var procs = Process.GetProcesses();
                progress?.Report($"Trimming {procs.Length} process working sets...");
                int trimmed = 0;
                foreach (var proc in procs)
                {
                    try
                    {
                        SetProcessWorkingSetSize(proc.Handle, new IntPtr(-1), new IntPtr(-1));
                        trimmed++;
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
                progress?.Report($"Trimmed {trimmed} processes.");
            });
        }

        /// <summary>
        /// Purges the standby memory list via NtSetSystemInformation.
        /// Requires SeProfileSingleProcessPrivilege (Administrator).
        /// Falls back silently if not elevated.
        /// </summary>
        public static async Task ClearStandbyAsync(IProgress<string>? progress = null)
        {
            // Check for EmptyStandbyList.exe (3rd-party tool) first
            var exePath = Path.Combine(AppContext.BaseDirectory, "EmptyStandbyList.exe");
            if (File.Exists(exePath))
            {
                progress?.Report("Running EmptyStandbyList.exe...");
                await RunProcessAsync(exePath, "");
                return;
            }

            // NtSetSystemInformation — SystemMemoryListInformation (80), MemoryPurgeStandbyList (4)
            await Task.Run(() =>
            {
                progress?.Report("Purging standby memory list...");
                var ptr = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    Marshal.WriteInt32(ptr, 4); // MemoryPurgeStandbyList
                    NtSetSystemInformation(80, ptr, sizeof(int));
                }
                catch { }
                finally { Marshal.FreeHGlobal(ptr); }
            });
        }

        /// <summary>Deletes unlocked files from %TEMP% and C:\Windows\Temp.</summary>
        public static async Task ClearTempFilesAsync(IProgress<string>? progress = null)
        {
            var dirs = new[]
            {
                Path.GetTempPath(),
                @"C:\Windows\Temp"
            };

            await Task.Run(() =>
            {
                foreach (var dir in dirs)
                {
                    if (!Directory.Exists(dir)) continue;

                    IEnumerable<string> files;
                    try { files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories); }
                    catch { continue; }

                    foreach (var file in files)
                    {
                        try
                        {
                            progress?.Report($"Deleting {Path.GetFileName(file)}...");
                            File.Delete(file);
                        }
                        catch { }
                    }
                }
            });
        }

        /// <summary>Flushes the DNS resolver cache.</summary>
        public static async Task FlushDnsAsync(IProgress<string>? progress = null)
        {
            progress?.Report("Flushing DNS cache...");
            await RunProcessAsync("ipconfig", "/flushdns");
        }

        /// <summary>Clears the Windows clipboard. Must be called on the STA/UI thread.</summary>
        public static void ClearClipboard()
        {
            try { Clipboard.Clear(); }
            catch { }
        }

        /// <summary>Returns non-system processes using ≥ 50 MB of RAM, sorted descending.</summary>
        public static List<ProcessInfo> GetHeavyProcesses()
        {
            // Names considered system-critical — skip these
            var systemNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "System", "svchost", "wininit", "winlogon", "lsass", "csrss",
                "smss", "services", "Registry", "Idle", "dwm", "fontdrvhost",
                "MsMpEng", "SearchIndexer", "spoolsv", "explorer", "taskhostw",
                "sihost", "ctfmon", "RuntimeBroker",
            };

            var result = new List<ProcessInfo>();
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (systemNames.Contains(proc.ProcessName)) continue;
                    long mb = proc.WorkingSet64 / 1_048_576L;
                    if (mb < 50) continue;
                    result.Add(new ProcessInfo { Id = proc.Id, Name = proc.ProcessName, MemoryMB = mb });
                }
                catch { }
                finally { proc.Dispose(); }
            }
            return result.OrderByDescending(p => p.MemoryMB).ToList();
        }

        /// <summary>Kills the selected processes.</summary>
        public static async Task KillProcessesAsync(
            IEnumerable<ProcessInfo> targets,
            IProgress<string>? progress = null)
        {
            await Task.Run(() =>
            {
                foreach (var info in targets.Where(p => p.IsSelected))
                {
                    progress?.Report($"Stopping {info.Name}...");
                    try
                    {
                        var proc = Process.GetProcessById(info.Id);
                        proc.Kill(entireProcessTree: false);
                        proc.WaitForExit(3000);
                    }
                    catch { }
                }
            });
        }

        // ----- Helpers -----

        private static async Task RunProcessAsync(string exe, string args)
        {
            await Task.Run(() =>
            {
                try
                {
                    var psi = new ProcessStartInfo(exe, args)
                    {
                        UseShellExecute = false,
                        CreateNoWindow  = true
                    };
                    var proc = Process.Start(psi);
                    proc?.WaitForExit(8000);
                }
                catch { }
            });
        }
    }
}
