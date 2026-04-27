using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BurgerDeleter.Services;

namespace BurgerDeleter.Views
{
    public partial class MemoryView : Page
    {
        private static readonly TimeSpan _fastInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan _slowInterval = TimeSpan.FromSeconds(5);

        private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        private int _driveTickCount = 0;
        private CancellationTokenSource? _cts;

        public MemoryView()
        {
            InitializeComponent();

            _statsTimer.Tick += (_, _) => UpdateStats();
            _statsTimer.Start();
            UpdateStats();

            // Slow the stats timer when user navigates to a different tab
            IsVisibleChanged += (_, e) =>
                _statsTimer.Interval = (bool)e.NewValue ? _fastInterval : _slowInterval;

            // Stop completely when the window goes to tray; restart when it returns
            AppEvents.AppHidden  += () => _statsTimer.Stop();
            AppEvents.AppVisible += () =>
            {
                _statsTimer.Interval = IsVisible ? _fastInterval : _slowInterval;
                _statsTimer.Start();
            };
        }

        // ===== LIVE RAM STATS =====

        private void UpdateStats()
        {
            var (totalMB, availMB) = MemoryOptimizerService.GetMemoryStatus();
            if (totalMB == 0) return;

            ulong usedMB = totalMB - availMB;
            double pct   = (usedMB / (double)totalMB) * 100.0;

            TotalRamLabel.Text = FormatRam(totalMB);
            UsedRamLabel.Text  = FormatRam(usedMB);
            AvailRamLabel.Text = FormatRam(availMB);
            RamPctLabel.Text   = $"{pct:F0}%";
            RamUsageBar.Value  = pct;

            // Color the bar by usage level
            RamUsageBar.Foreground = pct >= 85
                ? new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44))  // red
                : pct >= 70
                    ? new SolidColorBrush(Color.FromRgb(0xF9, 0x73, 0x16)) // orange
                    : (Brush)Application.Current.Resources["SuccessBrush"]; // green

            // Raise drive-stats event every ~30 seconds (15 ticks × 2 s)
            _driveTickCount++;
            if (_driveTickCount >= 15)
            {
                _driveTickCount = 0;
                AppEvents.RaiseDriveStatsChanged();
            }
        }

        private static string FormatRam(ulong mb)
            => mb >= 1024 ? $"{mb / 1024.0:F1} GB" : $"{mb} MB";

        // ===== BACKGROUND PROCESS LIST =====

        private void KillAppsCheck_Checked(object sender, RoutedEventArgs e)
        {
            ProcessListSection.Visibility = Visibility.Visible;
            LoadProcessList();
        }

        private void KillAppsCheck_Unchecked(object sender, RoutedEventArgs e)
            => ProcessListSection.Visibility = Visibility.Collapsed;

        private void RefreshProcesses_Click(object sender, RoutedEventArgs e)
            => LoadProcessList();

        private void LoadProcessList()
        {
            var procs = MemoryOptimizerService.GetHeavyProcesses();
            ProcessListView.ItemsSource = procs;
        }

        // ===== OPTIMIZE =====

        private async void Optimize_Click(object sender, RoutedEventArgs e)
        {
            // Build the list of selected actions
            bool doTrim      = TrimCheck.IsChecked      == true;
            bool doStandby   = StandbyCheck.IsChecked   == true;
            bool doTemp      = TempCheck.IsChecked       == true;
            bool doDns       = DnsCheck.IsChecked        == true;
            bool doClipboard = ClipboardCheck.IsChecked  == true;
            bool doKill      = KillAppsCheck.IsChecked   == true;

            if (!doTrim && !doStandby && !doTemp && !doDns && !doClipboard && !doKill)
            {
                MessageBox.Show("Select at least one action.", "Nothing Selected",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Snapshot available RAM before
            var (_, availBefore) = MemoryOptimizerService.GetMemoryStatus();

            // Count total steps for progress calculation
            int totalSteps = (doTrim ? 1 : 0) + (doStandby ? 1 : 0) + (doTemp ? 1 : 0) +
                             (doDns  ? 1 : 0) + (doClipboard ? 1 : 0) + (doKill ? 1 : 0);
            int step = 0;

            void Advance(string label)
            {
                step++;
                double pct = (step / (double)totalSteps) * 100.0;
                Dispatcher.Invoke(() =>
                {
                    ProgressStatusLabel.Text = label;
                    ProgressStepLabel.Text   = $"{step}/{totalSteps}";
                    OptimizeProgressBar.Value = pct;
                });
            }

            OptimizeBtn.IsEnabled      = false;
            ResultsCard.Visibility     = Visibility.Collapsed;
            ProgressSection.Visibility = Visibility.Visible;
            OptimizeProgressBar.Value  = 0;
            StatusLabel.Text           = "Optimizing...";

            _cts = new CancellationTokenSource();

            try
            {
                if (doTrim)
                {
                    ProgressStatusLabel.Text = "Trimming process working sets...";
                    var p = MakeProgress();
                    await MemoryOptimizerService.TrimWorkingSetsAsync(p);
                    Advance("Trimmed working sets.");
                }

                if (doStandby)
                {
                    ProgressStatusLabel.Text = "Clearing standby memory...";
                    var p = MakeProgress();
                    await MemoryOptimizerService.ClearStandbyAsync(p);
                    Advance("Standby list cleared.");
                }

                if (doTemp)
                {
                    ProgressStatusLabel.Text = "Deleting temp files...";
                    var p = MakeProgress();
                    await MemoryOptimizerService.ClearTempFilesAsync(p);
                    Advance("Temp files cleared.");
                }

                if (doDns)
                {
                    ProgressStatusLabel.Text = "Flushing DNS cache...";
                    var p = MakeProgress();
                    await MemoryOptimizerService.FlushDnsAsync(p);
                    Advance("DNS cache flushed.");
                }

                if (doClipboard)
                {
                    ProgressStatusLabel.Text = "Clearing clipboard...";
                    MemoryOptimizerService.ClearClipboard();
                    Advance("Clipboard cleared.");
                }

                if (doKill)
                {
                    var targets = (ProcessListView.ItemsSource as List<ProcessInfo>)
                                  ?? new List<ProcessInfo>();
                    ProgressStatusLabel.Text = "Stopping background apps...";
                    var p = MakeProgress();
                    await MemoryOptimizerService.KillProcessesAsync(targets, p);
                    Advance("Background apps stopped.");
                }

                // Final state
                OptimizeProgressBar.Value = 100;
                ProgressStatusLabel.Text  = "Done.";

                await Task.Delay(1000);
                ProgressSection.Visibility = Visibility.Collapsed;

                // Snapshot available RAM after
                var (_, availAfter) = MemoryOptimizerService.GetMemoryStatus();
                long freedMB = (long)availAfter - (long)availBefore;

                ResultBeforeLabel.Text = FormatRam(availBefore) + " free";
                ResultAfterLabel.Text  = FormatRam(availAfter)  + " free";
                ResultFreedLabel.Text  = freedMB > 0
                    ? $"+ {FormatRam((ulong)freedMB)} freed"
                    : "Optimization complete";
                ResultsCard.Visibility = Visibility.Visible;

                StatusLabel.Text = freedMB > 0
                    ? $"Done! Freed approximately {FormatRam((ulong)freedMB)} of RAM."
                    : "Done! Optimization complete.";

                UpdateStats();
                AppEvents.RaiseDriveStatsChanged();
            }
            catch (Exception ex)
            {
                ProgressSection.Visibility = Visibility.Collapsed;
                StatusLabel.Text = $"Error: {ex.Message}";
            }
            finally
            {
                OptimizeBtn.IsEnabled = true;
            }
        }

        private IProgress<string> MakeProgress() =>
            new Progress<string>(msg => Dispatcher.Invoke(() => ProgressStatusLabel.Text = msg));
    }
}
