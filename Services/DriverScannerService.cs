using System.Management;
using BurgerDeleter.Models;

namespace BurgerDeleter.Services
{
    public static class DriverScannerService
    {
        // Classes where an old driver is treated as Critical rather than just Outdated
        private static readonly HashSet<string> CriticalClasses =
            new(StringComparer.OrdinalIgnoreCase)
            { "Display", "Net", "HDC", "SCSIAdapter", "USB", "Bluetooth" };

        // ── Public entry point ────────────────────────────────────────────────

        public static Task<List<DriverEntry>> ScanAsync(
            IProgress<string>? progress = null,
            CancellationToken  ct       = default)
            => Task.Run(() => ScanInternal(progress, ct), ct);

        // ── Core scan ─────────────────────────────────────────────────────────

        private static List<DriverEntry> ScanInternal(
            IProgress<string>? progress,
            CancellationToken  ct)
        {
            var results = new List<DriverEntry>();
            var now     = DateTime.Now;

            progress?.Report("Querying WMI for signed drivers…");

            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_PnPSignedDriver WHERE DeviceName IS NOT NULL");

            int scanned = 0;
            foreach (ManagementObject obj in searcher.Get())
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var deviceName = obj["DeviceName"]    as string ?? string.Empty;
                    var version    = obj["DriverVersion"] as string ?? "—";
                    var provider   = obj["Manufacturer"]  as string
                                  ?? obj["DriverProviderName"] as string
                                  ?? string.Empty;
                    var devClass   = obj["DeviceClass"]   as string ?? string.Empty;
                    var rawDate    = obj["DriverDate"]    as string;

                    if (++scanned % 30 == 0)
                        progress?.Report($"Scanned {scanned} drivers…");

                    DateTime? driverDate = TryParseWmiDate(rawDate);

                    // Skip current drivers (≤ 6 months old)
                    if (driverDate.HasValue &&
                        (now - driverDate.Value).TotalDays <= 180)
                        continue;

                    // Determine status
                    DriverStatus status = ClassifyDriver(devClass, driverDate, now);

                    // Build the download/search URL — always returns a non-empty string
                    string url = GetUpdateUrl(deviceName, devClass, provider, version);

                    results.Add(new DriverEntry
                    {
                        DeviceName    = deviceName,
                        FriendlyName  = MakeFriendlyName(deviceName),
                        DriverVersion = version,
                        Provider      = provider,
                        DeviceClass   = devClass,
                        Status        = status,
                        StatusReason  = MakeStatusReason(devClass, driverDate, now),
                        FriendlyDate  = driverDate.HasValue
                            ? $"Last updated: {driverDate.Value:MMMM yyyy}"
                            : "Release date unknown",
                        UpdateUrl     = url,
                    });
                }
                catch { }
            }

            progress?.Report($"Done — {results.Count} driver(s) need attention.");

            // Critical first, then Outdated; alphabetical within each group
            return results
                .OrderBy(d => d.Status == DriverStatus.Critical ? 0 : 1)
                .ThenBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ── URL mapping ───────────────────────────────────────────────────────

        private static string GetUpdateUrl(string name, string devClass, string provider, string version)
        {
            const StringComparison OIC = StringComparison.OrdinalIgnoreCase;

            // NVIDIA — detection tool is more reliable than a search
            if (name.Contains("NVIDIA", OIC) || provider.Contains("NVIDIA", OIC))
                return "https://www.nvidia.com/en-us/drivers/";

            // AMD / Radeon — auto-detect page is more reliable than a search
            if (name.Contains("AMD",    OIC) || name.Contains("Radeon", OIC) ||
                provider.Contains("AMD", OIC) || provider.Contains("Advanced Micro Devices", OIC))
                return "https://www.amd.com/en/support/download/drivers.html";

            // Everything else: targeted Google search using the exact device name,
            // provider, and driver version so the result lands on the specific driver.
            string query = $"{name} {provider} driver download {version}".Trim();
            return $"https://www.google.com/search?q={Uri.EscapeDataString(query)}";
        }

        // ── Classification ────────────────────────────────────────────────────

        private static DriverStatus ClassifyDriver(
            string    devClass,
            DateTime? driverDate,
            DateTime  now)
        {
            if (!driverDate.HasValue) return DriverStatus.Outdated;

            double daysOld    = (now - driverDate.Value).TotalDays;
            bool   isCritical = CriticalClasses.Contains(devClass);

            return isCritical && daysOld > 365
                ? DriverStatus.Critical
                : DriverStatus.Outdated;
        }

        private static string MakeStatusReason(string devClass, DateTime? driverDate, DateTime now)
        {
            if (!driverDate.HasValue)
                return "Driver release date unknown — an update may be available.";

            int months = (int)((now - driverDate.Value).TotalDays / 30);
            bool isCritical = CriticalClasses.Contains(devClass);

            return isCritical && months >= 12
                ? $"This {devClass} driver is {months} months old. Critical hardware drivers should be kept current to avoid crashes and security issues."
                : $"Driver is {months} months old. An update may improve stability and performance.";
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string MakeFriendlyName(string raw)
            => raw
                .Replace("(R)", "",  StringComparison.OrdinalIgnoreCase)
                .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase)
                .Replace("  ", " ")
                .Trim();

        private static DateTime? TryParseWmiDate(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try { return ManagementDateTimeConverter.ToDateTime(raw); } catch { }

            if (raw.Length >= 8 &&
                DateTime.TryParseExact(raw[..8], "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                return dt;

            return null;
        }
    }
}
