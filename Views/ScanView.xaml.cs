using System.IO;
using System.Windows;
using System.Windows.Controls;
using BurgerDeleter.Models;
using BurgerDeleter.Services;

namespace BurgerDeleter.Views
{
    public partial class ScanView : Page
    {
        private readonly DiskScannerService _scanner = new();
        private readonly UninstallerService _uninstaller = new();

        private List<DriveItem> _scanResults = new();
        private CancellationTokenSource? _cts;

        private double _currentFreeGB;

        public ScanView()
        {
            InitializeComponent();
            LoadCurrentFree();

            AppEvents.DriveStatsChanged += () => Dispatcher.Invoke(LoadCurrentFree);
        }

        private void LoadCurrentFree()
        {
            try
            {
                var drive   = new DriveInfo("C");
                _currentFreeGB = drive.AvailableFreeSpace / 1_073_741_824.0;
                FreeAfterLabel.Text = $"{_currentFreeGB:F1} GB";
            }
            catch { }
        }

        // ===== SCAN =====

        private async void StartScan_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            StartScanBtn.IsEnabled = false;
            ResultsList.ItemsSource = null;
            _scanResults.Clear();
            UpdateSelectionLabels();

            var progress = new Progress<string>(msg =>
                Dispatcher.Invoke(() => ScanStatusLabel.Text = msg));

            try
            {
                ScanStatusLabel.Text = "Scanning...";

                _scanResults = await _scanner.ScanAsync(@"C:\", progress, _cts.Token);

                _scanResults = _scanResults
                    .OrderBy(i => (int)i.SafetyLevel)
                    .ThenByDescending(i => i.SizeBytes)
                    .ToList();

                ResultsList.ItemsSource = _scanResults;

                // Wire up property changed for checkbox selection tracking
                foreach (var item in _scanResults)
                    item.PropertyChanged += (s, args) =>
                    {
                        if (args.PropertyName == nameof(DriveItem.IsSelected))
                            Dispatcher.Invoke(UpdateSelectionLabels);
                    };

                ScanStatusLabel.Text = $"Found {_scanResults.Count} items. Check boxes to select for removal.";
            }
            catch (OperationCanceledException)
            {
                ScanStatusLabel.Text = "Scan cancelled.";
            }
            catch (Exception ex)
            {
                ScanStatusLabel.Text = $"Error: {ex.Message}";
            }
            finally
            {
                StartScanBtn.IsEnabled = true;
            }
        }

        // ===== SELECTION TRACKING =====

        private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // ListView selection -- not used for delete logic; checkboxes drive that
        }

        private void UpdateSelectionLabels()
        {
            long selectedBytes = _scanResults
                .Where(i => i.IsSelected)
                .Sum(i => i.SizeBytes);

            double selectedGB = selectedBytes / 1_073_741_824.0;
            double freeAfterGB = _currentFreeGB + selectedGB;

            SelectedSizeLabel.Text = selectedGB >= 1
                ? $"{selectedGB:F2} GB"
                : $"{selectedBytes / 1_048_576.0:F0} MB";

            FreeAfterLabel.Text = $"{freeAfterGB:F1} GB";

            int selectedCount = _scanResults.Count(i => i.IsSelected);
            DeleteSelectedBtn.IsEnabled = selectedCount > 0;
            SelectionHintLabel.Text = selectedCount > 0
                ? $"{selectedCount} item(s) selected — {SelectedSizeLabel.Text} will be freed."
                : "Check items above to select them for removal.";
        }

        // ===== DELETE =====

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!Helpers.AdminHelper.EnsureElevatedOrRelaunch()) return;

            var selected = _scanResults.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0) return;

            // Danger: prompt once per item so the user sees every consequence
            foreach (var item in selected.Where(i => i.SafetyLevel == SafetyLevel.Danger))
            {
                var danger = MessageBox.Show(
                    $"WARNING: {item.Name} — {item.SafetyReason}\n\n" +
                    "Deleting this may make your system unbootable or unresponsive. Are you absolutely sure?",
                    "DANGER — System Critical",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Stop);
                if (danger != MessageBoxResult.Yes) return;
            }

            // Caution: single consolidated prompt listing all reasons
            var cautionItems = selected.Where(i => i.SafetyLevel == SafetyLevel.Caution).ToList();
            if (cautionItems.Count > 0)
            {
                var reasons = string.Join("\n", cautionItems.Select(i => $"• {i.Name}: {i.SafetyReason}"));
                var caution = MessageBox.Show(
                    $"Caution:\n{reasons}\n\nThis may affect other programs. Continue?",
                    "Caution — Potential Side Effects",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (caution != MessageBoxResult.Yes) return;
            }

            double totalGB = selected.Sum(i => i.SizeBytes) / 1_073_741_824.0;
            var confirm = MessageBox.Show(
                $"You are about to permanently delete {selected.Count} item(s) totaling {totalGB:F2} GB.\n\nThis cannot be undone. Continue?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            DeleteSelectedBtn.IsEnabled = false;
            StartScanBtn.IsEnabled      = false;

            int total = selected.Count;
            int done  = 0;

            DeletionProgressSection.Visibility = System.Windows.Visibility.Visible;
            DeletionProgressBar.Value = 0;
            DeletionPercentLabel.Text = "0%";
            DeletionStatusLabel.Text  = "Starting...";

            foreach (var (item, index) in selected.Select((x, i) => (x, i)))
            {
                double pct = (index / (double)total) * 100;
                DeletionProgressBar.Value = pct;
                DeletionPercentLabel.Text = $"{(int)pct}%";
                DeletionStatusLabel.Text  = $"Deleting {item.Name}...";

                try
                {
                    if (item.Category == ItemCategory.Application)
                    {
                        await _uninstaller.UninstallAsync(item.Name, item.FullPath,
                            new Progress<string>(msg => Dispatcher.Invoke(() => DeletionStatusLabel.Text = msg)));
                    }
                    else if (Directory.Exists(item.FullPath) || File.Exists(item.FullPath))
                    {
                        await ForceDeleteService.ForceDeleteAsync(item.FullPath);
                    }

                    _scanResults.Remove(item);
                    done++;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not delete {item.Name}: {ex.Message}",
                        "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }

            ResultsList.ItemsSource = null;
            ResultsList.ItemsSource = _scanResults;

            LoadCurrentFree();
            UpdateSelectionLabels();

            DeletionProgressBar.Value = 100;
            DeletionPercentLabel.Text = "100%";
            DeletionStatusLabel.Text  = "Done.";
            ScanStatusLabel.Text      = $"Done. Deleted {done} item(s).";

            await Task.Delay(2000);
            DeletionProgressSection.Visibility = System.Windows.Visibility.Collapsed;

            StartScanBtn.IsEnabled = true;
        }
    }
}
