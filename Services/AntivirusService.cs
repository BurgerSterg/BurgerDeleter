using System.Diagnostics;
using System.IO;
using System.Management;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using BurgerDeleter.Helpers;

namespace BurgerDeleter.Services
{
    // ===== DATA MODELS =====

    public enum DefenderScanType { Quick = 1, Full = 2, Custom = 3 }

    public sealed class DefenderStatus
    {
        public bool    Available          { get; init; }
        public bool    AntivirusEnabled   { get; init; }
        public bool    RealTimeEnabled    { get; init; }
        public bool    TamperProtected    { get; init; }
        /// <summary>"Normal", "Passive", "SxS Passive Mode", "EDR Block Mode"…</summary>
        public string? RunningMode        { get; init; }
        public string? SignatureVersion   { get; init; }
        public int?    SignatureAgeDays   { get; init; }
        public int?    QuickScanAgeDays   { get; init; }
        public int?    FullScanAgeDays    { get; init; }
        public string? Error              { get; init; }

        /// <summary>Another antivirus is primary; Defender only does limited/periodic scanning.</summary>
        public bool IsPassive => RunningMode?.Contains("Passive", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>An antivirus registered with Windows Security Center (Defender, Malwarebytes, …).</summary>
    public sealed class AvProduct
    {
        public string Name       { get; init; } = string.Empty;
        public bool   Enabled    { get; init; }
        public bool   UpToDate   { get; init; }
        public bool   IsDefender => Name.Contains("Defender", StringComparison.OrdinalIgnoreCase);
        public string StateLabel => !Enabled ? "· real-time off" : UpToDate ? "· active" : "· active, outdated";
    }

    public sealed class ThreatRecord
    {
        public string   Source     { get; init; } = "Defender";
        public string   Name       { get; init; } = string.Empty;
        public string   Severity   { get; init; } = "Unknown";
        public string   Status     { get; init; } = "Detected";
        public string   Location   { get; init; } = string.Empty;
        public DateTime? DetectedAt { get; init; }

        /// <summary>True if the threat still needs action (not removed / quarantined).</summary>
        public bool IsActive { get; init; }

        public string DetectedDisplay => DetectedAt?.ToString("yyyy-MM-dd HH:mm") ?? "—";
    }

    public sealed class MalwarebytesInfo
    {
        public bool      Installed    { get; init; }
        public string?   ExePath      { get; init; }
        public DateTime? LastScan     { get; init; }
    }

    public sealed class ScanOutcome
    {
        public bool   Completed { get; init; }
        public string Summary   { get; init; } = string.Empty;
        public List<ThreatRecord> Threats { get; init; } = new();
    }

    // ===== SERVICE =====

    /// <summary>
    /// Wraps Microsoft Defender (WMI status + MpCmdRun scans) and Malwarebytes
    /// (AdwCleaner command-line scans, full Malwarebytes detection/launch).
    /// </summary>
    public static class AntivirusService
    {
        private const string DefenderNamespace = @"root\Microsoft\Windows\Defender";
        private const uint   NeverScanned      = uint.MaxValue;

        // ── Defender: status ─────────────────────────────────────────────────

        public static Task<DefenderStatus> GetDefenderStatusAsync() => Task.Run(() =>
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(DefenderNamespace,
                    "SELECT * FROM MSFT_MpComputerStatus");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        return new DefenderStatus
                        {
                            Available        = true,
                            AntivirusEnabled = mo["AntivirusEnabled"] as bool? ?? false,
                            RealTimeEnabled  = mo["RealTimeProtectionEnabled"] as bool? ?? false,
                            TamperProtected  = mo["IsTamperProtected"] as bool? ?? false,
                            RunningMode      = mo["AMRunningMode"] as string,
                            SignatureVersion = mo["AntivirusSignatureVersion"] as string,
                            SignatureAgeDays = AgeOrNull(mo["AntivirusSignatureAge"]),
                            QuickScanAgeDays = AgeOrNull(mo["QuickScanAge"]),
                            FullScanAgeDays  = AgeOrNull(mo["FullScanAge"]),
                        };
                    }
                }
                return new DefenderStatus { Error = "Microsoft Defender is not available on this PC." };
            }
            catch (Exception ex)
            {
                return new DefenderStatus { Error = $"Could not read Defender status: {ex.Message}" };
            }
        });

        private static int? AgeOrNull(object? value)
            => value is uint u && u != NeverScanned ? (int)u : null;

        // ── Security Center: all registered AV products ──────────────────────

        public static Task<List<AvProduct>> GetInstalledAntivirusAsync() => Task.Run(() =>
        {
            var list = new List<AvProduct>();
            try
            {
                using var searcher = new ManagementObjectSearcher(@"root\SecurityCenter2",
                    "SELECT displayName, productState FROM AntiVirusProduct");
                foreach (ManagementObject mo in searcher.Get())
                {
                    using (mo)
                    {
                        // productState is a packed hex value: 0xAABBCC
                        //   BB = 0x10/0x11 → real-time on;  CC = 0x00 → definitions up to date
                        uint state = Convert.ToUInt32(mo["productState"] ?? 0u);
                        uint rt    = (state >> 8) & 0xFF;
                        uint defs  = state & 0xFF;
                        list.Add(new AvProduct
                        {
                            Name     = mo["displayName"] as string ?? "Unknown",
                            Enabled  = rt is 0x10 or 0x11,
                            UpToDate = defs == 0x00,
                        });
                    }
                }
            }
            catch { /* Security Center is unavailable on Server SKUs */ }
            return list;
        });

        // ── Defender: scans & updates ────────────────────────────────────────

        public static string? FindMpCmdRun()
        {
            // The platform folder holds the newest engine; Program Files may be stale.
            try
            {
                var platform = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Microsoft\Windows Defender\Platform");
                if (Directory.Exists(platform))
                {
                    var newest = Directory.GetDirectories(platform)
                        .Select(d => Path.Combine(d, "MpCmdRun.exe"))
                        .Where(File.Exists)
                        .OrderByDescending(p => File.GetLastWriteTimeUtc(p))
                        .FirstOrDefault();
                    if (newest != null) return newest;
                }
            }
            catch { /* fall through */ }

            var legacy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                @"Windows Defender\MpCmdRun.exe");
            return File.Exists(legacy) ? legacy : null;
        }

        public static async Task<ScanOutcome> RunDefenderScanAsync(
            DefenderScanType type,
            string? customPath,
            IProgress<string>? log,
            CancellationToken ct)
        {
            var mpcmd = FindMpCmdRun();
            if (mpcmd is null)
                return new ScanOutcome { Summary = "Microsoft Defender command-line tool (MpCmdRun.exe) not found." };

            string args = type == DefenderScanType.Custom
                ? $"-Scan -ScanType 3 -File \"{customPath}\" -DisableRemediation"
                : $"-Scan -ScanType {(int)type}";

            var started = DateTime.Now.AddSeconds(-5);
            log?.Report($"[Defender] Starting {type.ToString().ToLowerInvariant()} scan…");

            int exit;
            try
            {
                exit = await RunStreamingAsync(mpcmd, args, log, ct, onCancel: () =>
                {
                    // Tell the Defender service to stop too — killing MpCmdRun alone leaves it running.
                    try { RunQuiet(mpcmd, "-Scan -Cancel"); } catch { }
                });
            }
            catch (OperationCanceledException)
            {
                log?.Report("[Defender] Scan cancelled.");
                return new ScanOutcome { Summary = "Defender scan cancelled." };
            }

            var threats = (await GetDefenderThreatsAsync())
                .Where(t => t.DetectedAt >= started)
                .ToList();

            // MpCmdRun: 0 = clean, 2 = threats found; anything else is an error.
            string summary = exit switch
            {
                0 when threats.Count == 0 => "Defender: no threats found.",
                0 or 2                    => $"Defender: {Math.Max(threats.Count, 1)} threat(s) found.",
                _                         => $"Defender scan failed (exit code {exit}). " +
                                             "If another antivirus is primary, Defender scans may be disabled.",
            };
            log?.Report($"[Defender] {summary}");

            return new ScanOutcome { Completed = exit is 0 or 2, Summary = summary, Threats = threats };
        }

        public static async Task<bool> UpdateDefenderSignaturesAsync(IProgress<string>? log, CancellationToken ct = default)
        {
            var mpcmd = FindMpCmdRun();
            if (mpcmd is null) return false;

            log?.Report("[Defender] Updating security intelligence…");
            int exit = await RunStreamingAsync(mpcmd, "-SignatureUpdate", log, ct);
            log?.Report(exit == 0
                ? "[Defender] Security intelligence is up to date."
                : $"[Defender] Update failed (exit code {exit}). Try running as Administrator.");
            return exit == 0;
        }

        // ── Defender: threat history ─────────────────────────────────────────

        public static Task<List<ThreatRecord>> GetDefenderThreatsAsync() => Task.Run(() =>
        {
            var results = new List<ThreatRecord>();
            try
            {
                // Threat names/severity live on MSFT_MpThreat; each detection references one by ThreatID.
                var catalog = new Dictionary<long, (string Name, string Severity)>();
                using (var threats = new ManagementObjectSearcher(DefenderNamespace,
                           "SELECT ThreatID, ThreatName, SeverityID FROM MSFT_MpThreat"))
                {
                    foreach (ManagementObject mo in threats.Get())
                    {
                        using (mo)
                        {
                            long id = Convert.ToInt64(mo["ThreatID"]);
                            catalog[id] = (mo["ThreatName"] as string ?? "Unknown threat",
                                           SeverityName(Convert.ToInt32(mo["SeverityID"] ?? 0)));
                        }
                    }
                }

                using var detections = new ManagementObjectSearcher(DefenderNamespace,
                    "SELECT ThreatID, InitialDetectionTime, Resources, ThreatStatusID FROM MSFT_MpThreatDetection");
                foreach (ManagementObject mo in detections.Get())
                {
                    using (mo)
                    {
                        long id = Convert.ToInt64(mo["ThreatID"]);
                        catalog.TryGetValue(id, out var info);
                        int status = Convert.ToInt32(mo["ThreatStatusID"] ?? 0);

                        DateTime? when = null;
                        if (mo["InitialDetectionTime"] is string dmtf && dmtf.Length > 0)
                        {
                            try { when = ManagementDateTimeConverter.ToDateTime(dmtf); } catch { }
                        }

                        var resources = mo["Resources"] as string[] ?? Array.Empty<string>();
                        results.Add(new ThreatRecord
                        {
                            Name       = info.Name ?? "Unknown threat",
                            Severity   = info.Severity ?? "Unknown",
                            Status     = StatusName(status),
                            IsActive   = status is 0 or 1 or 102 or 103 or 105 or 107,
                            Location   = CleanResource(resources.FirstOrDefault()),
                            DetectedAt = when,
                        });
                    }
                }
            }
            catch { /* history needs Administrator on most machines */ }

            return results.OrderByDescending(t => t.DetectedAt).ToList();
        });

        /// <summary>Removes all active Defender detections. Prompts for elevation if needed.</summary>
        public static Task<bool> RemoveDefenderThreatsAsync() => Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command Remove-MpThreat")
                {
                    UseShellExecute = true,
                    WindowStyle     = ProcessWindowStyle.Hidden,
                };
                if (!AdminHelper.IsElevated()) psi.Verb = "runas";

                using var p = Process.Start(psi);
                p?.WaitForExit(120_000);
                return p?.ExitCode == 0;
            }
            catch { return false; } // UAC declined
        });

        private static string SeverityName(int id) => id switch
        {
            1 => "Low",
            2 => "Moderate",
            4 => "High",
            5 => "Severe",
            _ => "Unknown",
        };

        private static string StatusName(int id) => id switch
        {
            1   => "Detected",
            2   => "Cleaned",
            3   => "Quarantined",
            4   => "Removed",
            5   => "Allowed",
            6   => "Blocked",
            102 => "Quarantine failed",
            103 => "Remove failed",
            104 => "Allow failed",
            105 => "Abandoned",
            107 => "Block failed",
            _   => "Unknown",
        };

        /// <summary>Strips the "file:_" style prefix Defender puts on resource paths.</summary>
        private static string CleanResource(string? r)
        {
            if (string.IsNullOrEmpty(r)) return string.Empty;
            int i = r.IndexOf(":_", StringComparison.Ordinal);
            return i >= 0 ? r[(i + 2)..] : r;
        }

        // ── Malwarebytes (full app) ──────────────────────────────────────────

        /// <summary>
        /// Malwarebytes (consumer) has no public scan command line, so we detect it,
        /// surface its last scan time and let the user open it for a Threat Scan.
        /// </summary>
        public static MalwarebytesInfo GetMalwarebytesInfo()
        {
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var exe = new[]
            {
                Path.Combine(pf, @"Malwarebytes\Anti-Malware\Malwarebytes.exe"), // v5+
                Path.Combine(pf, @"Malwarebytes\Anti-Malware\mbam.exe"),         // v4
            }.FirstOrDefault(File.Exists);

            DateTime? lastScan = null;
            try
            {
                var results = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    @"Malwarebytes\MBAMService\ScanResults");
                if (Directory.Exists(results))
                {
                    var newest = new DirectoryInfo(results).GetFiles()
                        .OrderByDescending(f => f.LastWriteTime)
                        .FirstOrDefault();
                    lastScan = newest?.LastWriteTime;
                }
            }
            catch { /* ProgramData folder may be ACL'd */ }

            return new MalwarebytesInfo { Installed = exe != null, ExePath = exe, LastScan = lastScan };
        }

        public static void OpenMalwarebytes(string exePath)
            => Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });

        // ── Malwarebytes AdwCleaner (command-line capable) ──────────────────

        private const string AdwCleanerUrl = "https://downloads.malwarebytes.com/file/adwcleaner";

        public static string AdwCleanerPath => Path.Combine(UpdateService.InstallDirectory, "tools", "adwcleaner.exe");

        private static string AdwCleanerLogDir =>
            Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", @"AdwCleaner\Logs");

        public static bool IsAdwCleanerReady => File.Exists(AdwCleanerPath) && IsSignedByMalwarebytes(AdwCleanerPath);

        /// <summary>Downloads AdwCleaner from Malwarebytes and verifies its Authenticode signature.</summary>
        public static async Task<bool> DownloadAdwCleanerAsync(IProgress<string>? log, CancellationToken ct)
        {
            var temp = AdwCleanerPath + ".download";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AdwCleanerPath)!);
                log?.Report("[AdwCleaner] Downloading from malwarebytes.com…");

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BurgerDeleter");
                using (var resp = await http.GetAsync(AdwCleanerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(temp);
                    await resp.Content.CopyToAsync(fs, ct);
                }

                if (!IsSignedByMalwarebytes(temp))
                {
                    File.Delete(temp);
                    log?.Report("[AdwCleaner] Download rejected: file is not validly signed by Malwarebytes.");
                    return false;
                }

                File.Move(temp, AdwCleanerPath, overwrite: true);
                log?.Report("[AdwCleaner] Downloaded and signature verified.");
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                try { File.Delete(temp); } catch { }
                log?.Report($"[AdwCleaner] Download failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Runs AdwCleaner headless. <paramref name="clean"/> = quarantine what it finds.</summary>
        public static async Task<ScanOutcome> RunAdwCleanerAsync(bool clean, IProgress<string>? log, CancellationToken ct)
        {
            if (!IsAdwCleanerReady)
                return new ScanOutcome { Summary = "AdwCleaner is not downloaded." };

            var started = DateTime.Now.AddSeconds(-5);
            log?.Report(clean
                ? "[AdwCleaner] Cleaning (approve the admin prompt)…"
                : "[AdwCleaner] Scanning for adware & PUPs (approve the admin prompt)…");

            Process? p;
            try
            {
                // AdwCleaner's manifest requires elevation; ShellExecute shows the UAC prompt.
                p = Process.Start(new ProcessStartInfo(AdwCleanerPath,
                    clean ? "/eula /clean /noreboot" : "/eula /scan /noreboot")
                {
                    UseShellExecute = true,
                    WindowStyle     = ProcessWindowStyle.Hidden,
                });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                log?.Report("[AdwCleaner] Admin prompt was declined.");
                return new ScanOutcome { Summary = "AdwCleaner needs administrator permission." };
            }
            if (p is null) return new ScanOutcome { Summary = "AdwCleaner failed to start." };

            using (p)
            {
                try { await p.WaitForExitAsync(ct); }
                catch (OperationCanceledException)
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    log?.Report("[AdwCleaner] Cancelled.");
                    return new ScanOutcome { Summary = "AdwCleaner cancelled." };
                }
            }

            var findings = ReadLatestAdwCleanerFindings(started, out var logFile);
            if (logFile is null)
            {
                log?.Report("[AdwCleaner] Finished, but no report was written.");
                return new ScanOutcome { Completed = true, Summary = "AdwCleaner finished (no report found)." };
            }

            string summary = findings.Count == 0
                ? "AdwCleaner: no adware or PUPs found."
                : clean
                    ? $"AdwCleaner: {findings.Count} item(s) quarantined. A restart may be needed."
                    : $"AdwCleaner: {findings.Count} adware/PUP item(s) found.";
            log?.Report($"[AdwCleaner] {summary}");

            return new ScanOutcome
            {
                Completed = true,
                Summary   = summary,
                Threats   = findings.Select(f => new ThreatRecord
                {
                    Source     = "AdwCleaner",
                    Name       = f.Name,
                    Severity   = "PUP",
                    Status     = clean ? "Quarantined" : "Detected",
                    IsActive   = !clean,
                    Location   = f.Location,
                    DetectedAt = DateTime.Now,
                }).ToList(),
            };
        }

        /// <summary>
        /// AdwCleaner reports are plain text with "***** [ Section ] *****" headers;
        /// each finding is a non-blank line that isn't a "No malicious … found" note.
        /// </summary>
        private static List<(string Name, string Location)> ReadLatestAdwCleanerFindings(DateTime since, out string? logFile)
        {
            var findings = new List<(string, string)>();
            logFile = null;
            try
            {
                if (!Directory.Exists(AdwCleanerLogDir)) return findings;

                logFile = new DirectoryInfo(AdwCleanerLogDir).GetFiles("AdwCleaner*.txt")
                    .Where(f => f.LastWriteTime >= since)
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault()?.FullName;
                if (logFile is null) return findings;

                bool inSection = false;
                foreach (var raw in File.ReadLines(logFile))
                {
                    var line = raw.Trim();
                    if (line.StartsWith("*****")) { inSection = !line.Contains("AdwCleaner", StringComparison.OrdinalIgnoreCase); continue; }
                    if (line.StartsWith("#")) { inSection = false; continue; }
                    if (!inSection || line.Length == 0) continue;
                    if (line.StartsWith("No ", StringComparison.OrdinalIgnoreCase)) continue;
                    // Trailer that lists previous report files / separator rows
                    if (line.StartsWith(":::") || line.Contains("AdwCleaner[", StringComparison.OrdinalIgnoreCase)) continue;

                    // Typical line: "PUP.Optional.Foo   Folder   C:\Path\To\Thing"
                    var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                    findings.Add(parts.Length == 2 ? (parts[0], parts[1].Trim()) : (line, string.Empty));
                }
            }
            catch { /* unreadable log */ }
            return findings;
        }

        public static void OpenAdwCleanerLogs()
        {
            if (Directory.Exists(AdwCleanerLogDir))
                Process.Start(new ProcessStartInfo(AdwCleanerLogDir) { UseShellExecute = true });
        }

        // ── Signature verification ───────────────────────────────────────────

        private static bool IsSignedByMalwarebytes(string path)
        {
            if (!WinTrust.IsTrusted(path)) return false;
            try
            {
#pragma warning disable SYSLIB0057
                var cert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
                return cert.Subject.Contains("Malwarebytes", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static class WinTrust
        {
            private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

            [StructLayout(LayoutKind.Sequential)]
            private struct WINTRUST_FILE_INFO
            {
                public uint   cbStruct;
                public IntPtr pcwszFilePath;
                public IntPtr hFile;
                public IntPtr pgKnownSubject;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct WINTRUST_DATA
            {
                public uint   cbStruct;
                public IntPtr pPolicyCallbackData;
                public IntPtr pSIPClientData;
                public uint   dwUIChoice;
                public uint   fdwRevocationChecks;
                public uint   dwUnionChoice;
                public IntPtr pFile;
                public uint   dwStateAction;
                public IntPtr hWVTStateData;
                public IntPtr pwszURLReference;
                public uint   dwProvFlags;
                public uint   dwUIContext;
                public IntPtr pSignatureSettings;
            }

            [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
            private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WINTRUST_DATA data);

            public static bool IsTrusted(string path)
            {
                IntPtr pathPtr = Marshal.StringToHGlobalUni(path);
                IntPtr filePtr = IntPtr.Zero;
                try
                {
                    var file = new WINTRUST_FILE_INFO
                    {
                        cbStruct      = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                        pcwszFilePath = pathPtr,
                    };
                    filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
                    Marshal.StructureToPtr(file, filePtr, false);

                    var data = new WINTRUST_DATA
                    {
                        cbStruct      = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                        dwUIChoice    = 2, // WTD_UI_NONE
                        dwUnionChoice = 1, // WTD_CHOICE_FILE
                        pFile         = filePtr,
                    };
                    var action = GenericVerifyV2;
                    return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
                }
                catch { return false; }
                finally
                {
                    if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
                    Marshal.FreeHGlobal(pathPtr);
                }
            }
        }

        // ── Process helpers ──────────────────────────────────────────────────

        private static async Task<int> RunStreamingAsync(
            string exe, string args, IProgress<string>? log, CancellationToken ct, Action? onCancel = null)
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                },
            };
            p.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Report(e.Data.Trim()); };
            p.ErrorDataReceived  += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Report(e.Data.Trim()); };

            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            try
            {
                await p.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                onCancel?.Invoke();
                try { p.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            return p.ExitCode;
        }

        private static void RunQuiet(string exe, string args)
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                CreateNoWindow  = true,
            });
            p?.WaitForExit(15_000);
        }
    }
}
