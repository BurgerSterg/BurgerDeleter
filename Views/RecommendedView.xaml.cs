using System.IO;
using System.Windows;
using System.Windows.Controls;
using BurgerDeleter.Models;
using BurgerDeleter.Services;

namespace BurgerDeleter.Views
{
    public partial class RecommendedView : Page
    {
        private readonly RecommendedScannerService _scanner = new();

        private List<DriveItem> _results = new();
        private CancellationTokenSource? _cts;

        private double _currentFreeGB;

        public RecommendedView()
        {
            InitializeComponent();
            LoadCurrentFree();

            AppEvents.DriveStatsChanged += () => Dispatcher.Invoke(LoadCurrentFree);
        }

        private void LoadCurrentFree()
        {
            try
            {
                var drive = new DriveInfo("C");
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

            StartScanBtn.IsEnabled  = false;
            ResultsList.ItemsSource = null;
            _results.Clear();
            UpdateSelectionLabels();

            var progress = new Progress<string>(msg =>
                Dispatcher.Invoke(() => ScanStatusLabel.Text = msg));

            try
            {
                ScanStatusLabel.Text = "Scanning for recommendations...";

                _results = await _scanner.ScanAsync(progress, _cts.Token);

                ResultsList.ItemsSource = _results;

                foreach (var item in _results)
                    item.PropertyChanged += (s, args) =>
                    {
                        if (args.PropertyName == nameof(DriveItem.IsSelected))
                            Dispatcher.Invoke(UpdateSelectionLabels);
                    };

                ScanStatusLabel.Text = _results.Count > 0
                    ? $"Found {_results.Count} item(s) to review. Check boxes to select for removal."
                    : "Nothing found — your system looks clean!";
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

        private void UpdateSelectionLabels()
        {
            long selectedBytes = _results
                .Where(i => i.IsSelected)
                .Sum(i => i.SizeBytes);

            double selectedGB  = selectedBytes / 1_073_741_824.0;
            double freeAfterGB = _currentFreeGB + selectedGB;

            SelectedSizeLabel.Text = selectedGB >= 1
                ? $"{selectedGB:F2} GB"
                : $"{selectedBytes / 1_048_576.0:F0} MB";

            FreeAfterLabel.Text = $"{freeAfterGB:F1} GB";

            int count = _results.Count(i => i.IsSelected);
            DeleteSelectedBtn.IsEnabled = count > 0;
            SelectionHintLabel.Text = count > 0
                ? $"{count} item(s) selected — {SelectedSizeLabel.Text} will be freed."
                : "Check items above to select them for removal.";
        }

        // ===== DELETE =====

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!Helpers.AdminHelper.EnsureElevatedOrRelaunch()) return;

            var selected = _results.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0) return;

            // Danger gate
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

            // Caution gate
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
                $"Permanently delete {selected.Count} item(s) totaling {totalGB:F2} GB?\n\nThis cannot be undone.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            DeleteSelectedBtn.IsEnabled = false;
            StartScanBtn.IsEnabled      = false;
            int done = 0;

            foreach (var item in selected)
            {
                ScanStatusLabel.Text = $"Deleting {item.Name}...";
                try
                {
                    if (Directory.Exists(item.FullPath) || File.Exists(item.FullPath))
                        await ForceDeleteService.ForceDeleteAsync(item.FullPath);

                    _results.Remove(item);
                    ResultsList.ItemsSource = null;
                    ResultsList.ItemsSource = _results;

                    LoadCurrentFree();
                    UpdateSelectionLabels();
                    AppEvents.RaiseDriveStatsChanged();

                    done++;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Could not delete {item.Name}:\n{ex.Message}",
                        "Delete Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }

            ScanStatusLabel.Text = $"Done. Deleted {done} item(s).";
            StartScanBtn.IsEnabled = true;
        }
    }
}
