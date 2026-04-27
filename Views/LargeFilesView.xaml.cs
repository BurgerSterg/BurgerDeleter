using System.IO;
using System.Windows;
using System.Windows.Controls;
using BurgerDeleter.Models;
using BurgerDeleter.Services;

namespace BurgerDeleter.Views
{
    public partial class LargeFilesView : Page
    {
        private readonly DiskScannerService _scanner = new();

        private List<DriveItem> _allResults  = new();
        private List<DriveItem> _viewResults = new();
        private CancellationTokenSource? _cts;

        private double _currentFreeGB;

        public LargeFilesView()
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
            _allResults.Clear();
            _viewResults.Clear();
            UpdateSelectionLabels();

            long minBytes = GetSelectedMinBytes();

            var progress = new Progress<string>(msg =>
                Dispatcher.Invoke(() => ScanStatusLabel.Text = msg));

            try
            {
                ScanStatusLabel.Text = "Scanning...";

                _allResults = await _scanner.ScanLargeFilesAsync(@"C:\", minBytes, progress, _cts.Token);

                ApplyFilter();

                ScanStatusLabel.Text = _allResults.Count > 0
                    ? $"Found {_allResults.Count} file(s). Check boxes to select for removal."
                    : "No large files found at this threshold.";
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

        private long GetSelectedMinBytes()
        {
            if (SizeThresholdBox.SelectedItem is ComboBoxItem item &&
                long.TryParse(item.Tag?.ToString(), out long bytes))
                return bytes;
            return 104_857_600; // default 100 MB
        }

        // ===== FILTER =====

        private void Filter_Checked(object sender, RoutedEventArgs e) => ApplyFilter();

        private void ApplyFilter()
        {
            // Guard: called during InitializeComponent before controls are ready
            if (ResultsList == null) return;

            _viewResults = GetActiveFilter() switch
            {
                "Videos"     => _allResults.Where(i => i.Category == ItemCategory.VideoFile).ToList(),
                "Archives"   => _allResults.Where(i => i.Category == ItemCategory.ArchiveFile).ToList(),
                "Installers" => _allResults.Where(i => i.Category == ItemCategory.Application).ToList(),
                _            => _allResults.ToList()
            };

            ResultsList.ItemsSource = null;
            ResultsList.ItemsSource = _viewResults;

            foreach (var item in _viewResults)
                item.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(DriveItem.IsSelected))
                        Dispatcher.Invoke(UpdateSelectionLabels);
                };

            UpdateSelectionLabels();
        }

        private string GetActiveFilter()
        {
            if (FilterVideos?.IsChecked    == true) return "Videos";
            if (FilterArchives?.IsChecked  == true) return "Archives";
            if (FilterInstallers?.IsChecked == true) return "Installers";
            return "All";
        }

        // ===== SELECTION TRACKING =====

        private void UpdateSelectionLabels()
        {
            long selectedBytes = _viewResults
                .Where(i => i.IsSelected)
                .Sum(i => i.SizeBytes);

            double selectedGB  = selectedBytes / 1_073_741_824.0;
            double freeAfterGB = _currentFreeGB + selectedGB;

            SelectedSizeLabel.Text = selectedGB >= 1
                ? $"{selectedGB:F2} GB"
                : $"{selectedBytes / 1_048_576.0:F0} MB";

            FreeAfterLabel.Text = $"{freeAfterGB:F1} GB";

            int count = _viewResults.Count(i => i.IsSelected);
            DeleteSelectedBtn.IsEnabled = count > 0;
            SelectionHintLabel.Text = count > 0
                ? $"{count} file(s) selected — {SelectedSizeLabel.Text} will be freed."
                : "Check files above to select them for removal.";
        }

        // ===== DELETE =====

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!Helpers.AdminHelper.EnsureElevatedOrRelaunch()) return;

            var selected = _viewResults.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0) return;

            double totalGB = selected.Sum(i => i.SizeBytes) / 1_073_741_824.0;
            var confirm = MessageBox.Show(
                $"Permanently delete {selected.Count} file(s) totaling {totalGB:F2} GB?\n\nThis cannot be undone.",
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
                    await ForceDeleteService.ForceDeleteAsync(item.FullPath);

                    _allResults.Remove(item);
                    _viewResults.Remove(item);

                    ResultsList.ItemsSource = null;
                    ResultsList.ItemsSource = _viewResults;

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

            DeletionProgressBar.Value = 100;
            DeletionPercentLabel.Text = "100%";
            DeletionStatusLabel.Text  = "Done.";
            ScanStatusLabel.Text      = $"Done. Deleted {done} file(s).";

            await Task.Delay(2000);
            DeletionProgressSection.Visibility = System.Windows.Visibility.Collapsed;

            StartScanBtn.IsEnabled = true;
        }
    }
}
