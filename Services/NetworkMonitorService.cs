using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Threading;
using BurgerDeleter.Models;

namespace BurgerDeleter.Services
{
    public class NetworkSnapshot
    {
        public double TotalUploadBytesPerSec   { get; init; }
        public double TotalDownloadBytesPerSec { get; init; }

        /// <summary>
        /// Always empty — per-process tracking has been removed.
        /// Kept so NetworkView.xaml.cs compiles without changes.
        /// </summary>
        public List<NetworkEntry> Processes { get; init; } = new();
    }

    public static class NetworkMonitorService
    {
        // ── Events ────────────────────────────────────────────────────────────

        public static event Action<NetworkSnapshot>? Updated;

        // ── State ─────────────────────────────────────────────────────────────

        private static DispatcherTimer? _timer;

        /// <summary>Previous per-adapter BytesSent snapshot; key = adapter Id.</summary>
        private static readonly Dictionary<string, long> _prevSent     = new();

        /// <summary>Previous per-adapter BytesReceived snapshot; key = adapter Id.</summary>
        private static readonly Dictionary<string, long> _prevReceived = new();

        private static DateTime _lastSample = DateTime.MinValue;

        // ── Public API ────────────────────────────────────────────────────────

        public static void Start(Dispatcher dispatcher)
        {
            try
            {
                if (_timer != null) return;
                _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                _timer.Tick += (_, _) => Tick();
                _timer.Start();
            }
            catch { }
        }

        public static void Stop()
        {
            try
            {
                _timer?.Stop();
                _timer = null;
            }
            catch { }
        }

        // ── Network adapter helpers (used by NetworkView) ─────────────────────

        public static IEnumerable<NetworkInterface> GetNetworkAdapters()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus        == OperationalStatus.Up
                             && n.NetworkInterfaceType     != NetworkInterfaceType.Loopback
                             && n.NetworkInterfaceType     != NetworkInterfaceType.Tunnel);
            }
            catch
            {
                return Enumerable.Empty<NetworkInterface>();
            }
        }

        public static string GetLocalIp()
        {
            try
            {
                return GetNetworkAdapters()
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .FirstOrDefault() ?? "—";
            }
            catch { return "—"; }
        }

        public static string GetDnsServers()
        {
            try
            {
                var addrs = GetNetworkAdapters()
                    .SelectMany(n => n.GetIPProperties().DnsAddresses)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .Distinct()
                    .Take(2)
                    .ToList();
                return addrs.Count > 0 ? string.Join("  /  ", addrs) : "—";
            }
            catch { return "—"; }
        }

        public static string GetPrimaryAdapterName()
        {
            try { return GetNetworkAdapters().FirstOrDefault()?.Name ?? "Ethernet"; }
            catch { return "Ethernet"; }
        }

        public static async Task<string> FetchPublicIpAsync()
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                return await http.GetStringAsync("https://api.ipify.org");
            }
            catch { return "—"; }
        }

        // ── Tick ──────────────────────────────────────────────────────────────

        private static void Tick()
        {
            try
            {
                var (upload, download) = ComputeBandwidth();
                Updated?.Invoke(new NetworkSnapshot
                {
                    TotalUploadBytesPerSec   = upload,
                    TotalDownloadBytesPerSec = download,
                });
            }
            catch { }
        }

        // ── Bandwidth calculation ─────────────────────────────────────────────

        private static (double Upload, double Download) ComputeBandwidth()
        {
            try
            {
                var now     = DateTime.UtcNow;
                double elapsed = _lastSample == DateTime.MinValue
                    ? 2.0
                    : Math.Max(0.1, (now - _lastSample).TotalSeconds);
                _lastSample = now;

                double totalUpload   = 0.0;
                double totalDownload = 0.0;

                foreach (var iface in GetNetworkAdapters())
                {
                    try
                    {
                        var stats = iface.GetIPv4Statistics();
                        long sent     = stats.BytesSent;
                        long received = stats.BytesReceived;

                        if (_prevSent.TryGetValue(iface.Id, out long prevSent) &&
                            _prevReceived.TryGetValue(iface.Id, out long prevReceived))
                        {
                            long sentDelta     = sent     - prevSent;
                            long receivedDelta = received - prevReceived;

                            // Ignore negative deltas (counter reset or adapter change)
                            if (sentDelta     > 0) totalUpload   += sentDelta     / elapsed;
                            if (receivedDelta > 0) totalDownload += receivedDelta / elapsed;
                        }

                        _prevSent[iface.Id]     = sent;
                        _prevReceived[iface.Id] = received;
                    }
                    catch { }
                }

                return (totalUpload, totalDownload);
            }
            catch
            {
                return (0.0, 0.0);
            }
        }
    }
}
