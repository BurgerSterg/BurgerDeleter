using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BurgerDeleter.Services;
using Microsoft.Win32;

namespace BurgerDeleter.Views
{
    public partial class SecurityView : Page
    {
        private const int MaxLogLines = 400;

        private readonly Queue<string> _logLines = new();
        private CancellationTokenSource? _cts;
        private MalwarebytesInfo? _mb;
        private string? _customPath;
        private bool _loaded;

        public SecurityView()
        {
            InitializeComponent();
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loaded) return;
            _loaded = true;
            await RefreshStatusAsync();
            await ShowThreatHistoryAsync();
        }

        // ===== STATUS =====

        private async Task RefreshStatusAsync()
        {
            var defenderTask = AntivirusService.GetDefenderStatusAsync();
            var productsTask = AntivirusService.GetInstalledAntivirusAsync();
            var d = await defenderTask;
            var products = await productsTask;

            var ok      = (Brush)Application.Current.Resources["SuccessBrush"];
            var warn    = (Brush)Application.Current.Resources["WarningBrush"];
            var danger  = (Brush)Application.Current.Resources["DangerBrush"];
            var primary = (Brush)Application.Current.Resources["TextPrimaryBrush"];

            if (!d.Available)
            {
                RtValue.Text = "Unavailable"; RtValue.Foreground = warn;
                RtSub.Text   = d.Error ?? string.Empty;
            }
            else if (d.IsPassive)
            {
                var other = products.FirstOrDefault(p => !p.IsDefender && p.Enabled);
                RtValue.Text = "Passive";     RtValue.Foreground = warn;
                RtSub.Text   = $"{other?.Name ?? "Another antivirus"} is the primary protection. Defender scans may be limited.";
            }
            else
            {
                RtValue.Text       = d.RealTimeEnabled ? "On" : "Off";
                RtValue.Foreground = d.RealTimeEnabled ? ok : danger;
                RtSub.Text         = d.TamperProtected ? "Tamper protection on" : "Tamper protection off";
            }

            SigValue.Text = d.SignatureAgeDays switch
            {
                null => "—",
                0    => "Today",
                1    => "1 day old",
                var n => $"{n} days old",
            };
            SigValue.Foreground = d.SignatureAgeDays is > 3 ? warn : primary;
            SigSub.Text = d.SignatureVersion is null ? string.Empty : $"v{d.SignatureVersion}";

            int? lastScan = new[] { d.QuickScanAgeDays, d.FullScanAgeDays }.Where(a => a.HasValue).Min();
            ScanValue.Text = lastScan switch
            {
                null  => "Never",
                0     => "Today",
                1     => "Yesterday",
                var n => $"{n} days ago",
            };
            ScanValue.Foreground = lastScan is null or > 14 ? warn : primary;
            ScanSub.Text = d.FullScanAgeDays is { } full ? $"Full scan {full} days ago" : "No full scan on record";

            AvList.ItemsSource = products.Count > 0
                ? products
                : new List<AvProduct> { new() { Name = "None registered" } };

            RefreshEngineStatus();
        }

        private void RefreshEngineStatus()
        {
            bool adwReady = AntivirusService.IsAdwCleanerReady;
            AdwStatus.Text = adwReady
                ? "✓ Ready. Runs hidden; you'll get one admin prompt."
                : "Not downloaded yet. It's fetched from malwarebytes.com and its signature is verified.";
            AdwDownloadBtn.Visibility = adwReady ? Visibility.Collapsed : Visibility.Visible;
            UseAdw.IsEnabled = adwReady;
            if (!adwReady) UseAdw.IsChecked = false;

            _mb = AntivirusService.GetMalwarebytesInfo();
            MbStatus.Text = !_mb.Installed
                ? "Not installed."
                : _mb.LastScan is { } t
                    ? $"✓ Installed · last scan {t:yyyy-MM-dd HH:mm}"
                    : "✓ Installed";
            MbOpenBtn.Content = _mb.Installed ? "Open Malwarebytes" : "Get Malwarebytes";
        }

        private async Task ShowThreatHistoryAsync()
        {
            var history = await AntivirusService.GetDefenderThreatsAsync();
            ShowThreats("DEFENDER THREAT HISTORY", history);
            if (history.Count == 0 && !Helpers.AdminHelper.IsElevated())
                NoThreatsText.Text = "No threats on record. (Run as Administrator to see Defender's full history.)";
        }

        private void ShowThreats(string header, List<ThreatRecord> threats)
        {
            ThreatHeader.Text = threats.Count == 0 ? header : $"{header} [{threats.Count}]";
            ThreatList.ItemsSource = threats;
            NoThreatsText.Text = "No threats found.";
            NoThreatsText.Visibility = threats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            RemoveDefenderBtn.Visibility = threats.Any(t => t.Source == "Defender" && t.IsActive)
                ? Visibility.Visible : Visibility.Collapsed;
            CleanAdwBtn.Visibility = threats.Any(t => t.Source == "AdwCleaner" && t.IsActive)
                ? Visibility.Visible : Visibility.Collapsed;
        }

        // ===== SCAN =====

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            bool runDefender = UseDefender.IsChecked == true;
            bool runAdw      = UseAdw.IsChecked == true && AntivirusService.IsAdwCleanerReady;

            if (!runDefender && !runAdw)
            {
                RunStatus.Text = "Pick at least one scan engine.";
                return;
            }

            var type = FullRadio.IsChecked == true ? DefenderScanType.Full
                     : CustomRadio.IsChecked == true ? DefenderScanType.Custom
                     : DefenderScanType.Quick;
            if (runDefender && type == DefenderScanType.Custom && string.IsNullOrEmpty(_customPath))
            {
                RunStatus.Text = "Choose a folder to scan first.";
                return;
            }

            _cts = new CancellationTokenSource();
            SetRunning(true);
            ClearLog();

            var all       = new List<ThreatRecord>();
            var summaries = new List<string>();
            var log       = new Progress<string>(AppendLog);

            try
            {
                if (runDefender)
                {
                    RunStatus.Text = "Microsoft Defender is scanning…";
                    var r = await AntivirusService.RunDefenderScanAsync(type, _customPath, log, _cts.Token);
                    all.AddRange(r.Threats);
                    summaries.Add(r.Summary);
                }

                if (runAdw && !_cts.IsCancellationRequested)
                {
                    RunStatus.Text = "AdwCleaner is scanning…";
                    var r = await AntivirusService.RunAdwCleanerAsync(clean: false, log, _cts.Token);
                    all.AddRange(r.Threats);
                    summaries.Add(r.Summary);
                }

                RunStatus.Text = all.Count == 0
                    ? "✓ Scan complete. Your PC is clean."
                    : $"⚠ Scan complete. {all.Count} item(s) need attention.";
                AppendLog("— " + string.Join("  ", summaries));
                ShowThreats("SCAN RESULTS", all);
            }
            catch (Exception ex)
            {
                RunStatus.Text = "Scan failed.";
                AppendLog($"Error: {ex.Message}");
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetRunning(false);
                await RefreshStatusAsync();
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            RunStatus.Text = "Cancelling…";
        }

        private void SetRunning(bool running)
        {
            StartBtn.IsEnabled        = !running;
            UpdateSigBtn.IsEnabled    = !running;
            AdwDownloadBtn.IsEnabled  = !running;
            RemoveDefenderBtn.IsEnabled = !running;
            CleanAdwBtn.IsEnabled     = !running;
            CancelBtn.Visibility      = running ? Visibility.Visible : Visibility.Collapsed;
            ScanProgress.Visibility   = running ? Visibility.Visible : Visibility.Collapsed;
        }

        // ===== ACTIONS =====

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog { Title = "Choose a folder to scan" };
            if (dlg.ShowDialog() != true) return;
            _customPath = dlg.FolderName;
            CustomPathText.Text = _customPath;
            CustomRadio.IsChecked = true;
        }

        private async void UpdateSignatures_Click(object sender, RoutedEventArgs e)
        {
            UpdateSigBtn.IsEnabled = false;
            RunStatus.Text = "Updating definitions…";
            ScanProgress.Visibility = Visibility.Visible;
            try
            {
                bool ok = await AntivirusService.UpdateDefenderSignaturesAsync(new Progress<string>(AppendLog));
                RunStatus.Text = ok ? "✓ Definitions updated." : "Definition update failed. Try running as Administrator.";
                await RefreshStatusAsync();
            }
            finally
            {
                UpdateSigBtn.IsEnabled = true;
                ScanProgress.Visibility = Visibility.Collapsed;
            }
        }

        private async void AdwDownload_Click(object sender, RoutedEventArgs e)
        {
            AdwDownloadBtn.IsEnabled = false;
            ScanProgress.Visibility  = Visibility.Visible;
            try
            {
                if (await AntivirusService.DownloadAdwCleanerAsync(new Progress<string>(AppendLog), CancellationToken.None))
                    UseAdw.IsChecked = true;
                RefreshEngineStatus();
                UseAdw.IsEnabled = AntivirusService.IsAdwCleanerReady;
            }
            finally
            {
                AdwDownloadBtn.IsEnabled = true;
                ScanProgress.Visibility  = Visibility.Collapsed;
            }
        }

        private void AdwLogs_Click(object sender, RoutedEventArgs e) => AntivirusService.OpenAdwCleanerLogs();

        private void MbOpen_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_mb?.Installed == true && _mb.ExePath != null)
                    AntivirusService.OpenMalwarebytes(_mb.ExePath);
                else
                    Process.Start(new ProcessStartInfo("https://www.malwarebytes.com/mwb-download") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                RunStatus.Text = $"Couldn't open Malwarebytes: {ex.Message}";
            }
        }

        private async void RemoveDefender_Click(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show(
                "Remove all active threats detected by Microsoft Defender?\n\nDefender quarantines or deletes the infected files.",
                "Remove Threats", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            RemoveDefenderBtn.IsEnabled = false;
            RunStatus.Text = "Removing threats…";
            bool ok = await AntivirusService.RemoveDefenderThreatsAsync();
            RunStatus.Text = ok ? "✓ Defender removed the threats." : "Removal failed or the admin prompt was declined.";
            RemoveDefenderBtn.IsEnabled = true;
            await ShowThreatHistoryAsync();
        }

        private async void CleanAdw_Click(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show(
                "Quarantine everything AdwCleaner found?\n\nItems can be restored from AdwCleaner's quarantine later. " +
                "Some cleanups need a restart to finish.",
                "Clean with AdwCleaner", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            _cts = new CancellationTokenSource();
            SetRunning(true);
            try
            {
                var r = await AntivirusService.RunAdwCleanerAsync(clean: true, new Progress<string>(AppendLog), _cts.Token);
                RunStatus.Text = r.Summary;
                ShowThreats("ADWCLEANER CLEANUP", r.Threats);
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                SetRunning(false);
            }
        }

        // ===== LOG =====

        private void ClearLog()
        {
            _logLines.Clear();
            LogBox.Text = string.Empty;
        }

        private void AppendLog(string line)
        {
            _logLines.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
            while (_logLines.Count > MaxLogLines) _logLines.Dequeue();

            var sb = new StringBuilder();
            foreach (var l in _logLines) sb.AppendLine(l);
            LogBox.Text = sb.ToString();
            LogBox.ScrollToEnd();
        }
    }
}
