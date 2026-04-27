using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BurgerDeleter.Services;

namespace BurgerDeleter.Views
{
    public partial class NetworkView : Page
    {
        // ── State ─────────────────────────────────────────────────────────────

        private string _localIp  = "—";
        private string _publicIp = "—";
        private bool   _localIpVisible  = true;   // not sensitive — visible by default
        private bool   _publicIpVisible = false;  // sensitive — hidden by default

        public NetworkView()
        {
            InitializeComponent();
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            NetworkMonitorService.Updated += OnUpdated;
            NetworkMonitorService.Start(Dispatcher);
            IsVisibleChanged     += OnVisibilityChanged;
            AppEvents.AppHidden  += OnAppHidden;
            AppEvents.AppVisible += OnAppVisible;

            // Local IP is synchronous and not sensitive — show immediately
            _localIp           = NetworkMonitorService.GetLocalIp();
            LocalIpDisplay.Text = _localIp;

            // Public IP is sensitive and fetched async — stays masked until user reveals
            _ = FetchPublicIpAsync();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            NetworkMonitorService.Updated -= OnUpdated;
            NetworkMonitorService.Stop();
            IsVisibleChanged     -= OnVisibilityChanged;
            AppEvents.AppHidden  -= OnAppHidden;
            AppEvents.AppVisible -= OnAppVisible;
        }

        private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if ((bool)e.NewValue) NetworkMonitorService.Start(Dispatcher);
            else                  NetworkMonitorService.Stop();
        }

        private void OnAppHidden()  => NetworkMonitorService.Stop();
        private void OnAppVisible() { if (IsVisible) NetworkMonitorService.Start(Dispatcher); }

        // ── Live stats ─────────────────────────────────────────────────────────

        private void OnUpdated(NetworkSnapshot snap)
        {
            Dispatcher.Invoke(() =>
            {
                DownloadLabel.Text = FormatSpeed(snap.TotalDownloadBytesPerSec);
                UploadLabel.Text   = FormatSpeed(snap.TotalUploadBytesPerSec);
            });
        }

        // ── IP fetch & reveal ──────────────────────────────────────────────────

        private static readonly string[] PublicIpEndpoints =
        [
            "https://api.ipify.org",
            "https://ipv4.icanhazip.com",
            "https://checkip.amazonaws.com",
        ];

        private async Task FetchPublicIpAsync()
        {
            var errors = new List<string>();

            foreach (string url in PublicIpEndpoints)
            {
                try
                {
                    using var http = new System.Net.Http.HttpClient
                    {
                        Timeout = TimeSpan.FromSeconds(5)
                    };
                    string raw = await http.GetStringAsync(url);
                    string ip  = raw.Trim();

                    if (!string.IsNullOrWhiteSpace(ip))
                    {
                        _publicIp = ip;
                        if (_publicIpVisible)
                            Dispatcher.Invoke(() => PublicIpDisplay.Text = _publicIp);
                        return;
                    }

                    errors.Add($"{url} → empty response");
                }
                catch (Exception ex)
                {
                    errors.Add($"{url} → {ex.GetType().Name}: {ex.Message}");
                }
            }

            // All three failed
            _publicIp = "Could not retrieve — check connection";
            if (_publicIpVisible)
                Dispatcher.Invoke(() => PublicIpDisplay.Text = _publicIp);

            // Log failures to the terminal so they're visible
            string log = "Public IP fetch failed:\r\n" +
                         string.Join("\r\n", errors.Select(e => "  • " + e));
            Dispatcher.Invoke(() => ShowOutput("Public IP Fetch Error", log));
        }

        private void ToggleLocalIp_Click(object sender, RoutedEventArgs e)
        {
            _localIpVisible        = !_localIpVisible;
            LocalIpDisplay.Text    = _localIpVisible ? _localIp : "••••••••";
            LocalIpHint.Visibility = _localIpVisible ? Visibility.Hidden : Visibility.Visible;
        }

        private void TogglePublicIp_Click(object sender, RoutedEventArgs e)
        {
            _publicIpVisible        = !_publicIpVisible;
            PublicIpDisplay.Text    = _publicIpVisible ? _publicIp : "••••••••";
            PublicIpHint.Visibility = _publicIpVisible ? Visibility.Hidden : Visibility.Visible;
        }

        // ── Command handlers ────────────────────────────────────────────────────

        private async void FlushDns_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(FlushDnsBtn, true);
            ShowOutput("Flush DNS", await RunCmdAsync("ipconfig", "/flushdns"));
            SetBusy(FlushDnsBtn, false);
        }

        private async void ReleaseIp_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(ReleaseIpBtn, true);
            ShowOutput("Release IP", await RunCmdAsync("ipconfig", "/release"));
            SetBusy(ReleaseIpBtn, false);
        }

        private async void RenewIp_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(RenewIpBtn, true);
            ShowOutput("Renew IP", await RunCmdAsync("ipconfig", "/renew"));
            SetBusy(RenewIpBtn, false);
        }

        private async void ResetStack_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "This will reset the Windows network stack (Winsock and IP configuration).\n\n" +
                "⚠  A restart will be required to take effect. Continue?",
                "Reset Network Stack", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            SetBusy(ResetStackBtn, true);
            string out1 = await RunCmdAsync("netsh", "winsock reset");
            string out2 = await RunCmdAsync("netsh", "int ip reset");
            ShowOutput("Reset Network Stack", out1 + "\r\n" + out2);
            SetBusy(ResetStackBtn, false);

            MessageBox.Show(
                "Network stack reset complete.\n\nPlease restart your computer for changes to take effect.",
                "Restart Required", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void TestDns_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(TestDnsBtn, true);
            DnsSpeedResultCard.Visibility = Visibility.Collapsed;

            string cfOut = await RunCmdAsync("ping", "1.1.1.1 -n 4");
            string ggOut = await RunCmdAsync("ping", "8.8.8.8 -n 4");

            int cfMs = ParsePingAvg(cfOut);
            int ggMs = ParsePingAvg(ggOut);

            string result;
            if (cfMs < 0 && ggMs < 0)
            {
                result = "Both servers unreachable. Check your internet connection.";
            }
            else if (cfMs < 0)
            {
                result = $"Cloudflare: unreachable  /  Google: {ggMs}ms  —  Google is your best option.";
            }
            else if (ggMs < 0)
            {
                result = $"Cloudflare: {cfMs}ms  /  Google: unreachable  —  Cloudflare is your best option.";
            }
            else
            {
                string faster = cfMs <= ggMs ? "Cloudflare" : "Google";
                result = $"Cloudflare: {cfMs}ms  /  Google: {ggMs}ms  —  {faster} is faster.";
            }

            DnsSpeedResult.Text           = result;
            DnsSpeedResultCard.Visibility = Visibility.Visible;
            ShowOutput("Test DNS Speed", $"--- Cloudflare (1.1.1.1) ---\r\n{cfOut}\r\n\r\n--- Google (8.8.8.8) ---\r\n{ggOut}");
            SetBusy(TestDnsBtn, false);
        }

        private void ApplyDns_Click(object sender, RoutedEventArgs e)
        {
            if (DnsCombo.SelectedItem is not ComboBoxItem selected) return;

            string dnsTag      = selected.Tag?.ToString() ?? "dhcp";
            string adapterName = NetworkMonitorService.GetPrimaryAdapterName();

            var confirm = MessageBox.Show(
                $"Change DNS on adapter \"{adapterName}\" to {selected.Content}?\n\n" +
                "This requires administrator privileges.",
                "Change DNS", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                string args = dnsTag == "dhcp"
                    ? $"interface ip set dns \"{adapterName}\" dhcp"
                    : $"interface ip set dns \"{adapterName}\" static {dnsTag}";

                Process.Start(new ProcessStartInfo("netsh", args)
                {
                    Verb            = "runas",
                    UseShellExecute = true,
                    CreateNoWindow  = true,
                });

                ShowDnsStatus($"✓  DNS set to {selected.Content}. May take a moment to refresh.");
                ShowOutput("Change DNS",
                    $"Applied: netsh {args}\r\nAdapter: {adapterName}");
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80004005))
            {
                ShowDnsStatus("Cancelled — administrator access required.");
            }
            catch (Exception ex)
            {
                ShowDnsStatus($"Error: {ex.Message}");
            }
        }

        private void ShowDnsStatus(string message)
        {
            DnsStatusLabel.Text       = message;
            DnsStatusLabel.Visibility = Visibility.Visible;
        }

        // ── Terminal ────────────────────────────────────────────────────────────

        private void CloseTerminal_Click(object sender, RoutedEventArgs e)
        {
            TerminalSection.Visibility = Visibility.Collapsed;
            TerminalOutput.Text        = string.Empty;
        }

        private void ShowOutput(string title, string output)
        {
            TerminalTitle.Text         = $"Output — {title}";
            TerminalOutput.Text        = output.Trim();
            TerminalSection.Visibility = Visibility.Visible;

            // Scroll to bottom after layout is updated
            Dispatcher.InvokeAsync(
                () => TerminalScroll.ScrollToBottom(),
                DispatcherPriority.Background);
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static async Task<string> RunCmdAsync(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                };
                using var proc = Process.Start(psi)!;
                string stdout = await proc.StandardOutput.ReadToEndAsync();
                string stderr = await proc.StandardError.ReadToEndAsync();
                await proc.WaitForExitAsync();
                string combined = (stdout + stderr).Trim();
                return string.IsNullOrEmpty(combined) ? "(no output)" : combined;
            }
            catch (Exception ex)
            {
                return $"Error running {exe}: {ex.Message}";
            }
        }

        private static int ParsePingAvg(string output)
        {
            // Windows: "Average = 12ms" / "Durchschnitt = 12ms"
            var m = Regex.Match(output, @"Average\s*=\s*(\d+)\s*ms", RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        private static void SetBusy(Button btn, bool busy)
        {
            btn.IsEnabled = !busy;
            btn.Content   = busy ? "⏳  Running…" : "▶  Run";
        }

        private static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec >= 1_048_576) return $"{bytesPerSec / 1_048_576.0:F1} MB/s";
            if (bytesPerSec >= 1_024)     return $"{bytesPerSec / 1_024.0:F0} KB/s";
            return $"{bytesPerSec:F0} B/s";
        }
    }
}
