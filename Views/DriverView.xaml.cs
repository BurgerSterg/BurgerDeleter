using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BurgerDeleter.Models;
using BurgerDeleter.Services;

namespace BurgerDeleter.Views
{
    public partial class DriverView : Page
    {
        private List<DriverEntry> _allDrivers = new();
        private CancellationTokenSource? _cts;
        private DriverStatus? _activeFilter; // null = All

        // Suppress the Select All handler while we're programmatically updating
        // individual entries (prevents double-iteration).
        private bool _suppressSelectAll;

        public DriverView()
        {
            InitializeComponent();
        }

        // ── Scan ──────────────────────────────────────────────────────────────

        private async void ScanBtn_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            // Unsubscribe from the previous result set before replacing it
            UnsubscribeAll(_allDrivers);

            ScanBtn.IsEnabled          = false;
            ProgressSection.Visibility = Visibility.Visible;
            SummaryCard.Visibility     = Visibility.Collapsed;
            BatchBar.Visibility        = Visibility.Collapsed;
            DriverList.ItemsSource     = null;
            StatusBar.Text             = "Scanning drivers — this may take a moment…";

            var progress = new Progress<string>(msg =>
                Dispatcher.Invoke(() => ProgressLabel.Text = msg));

            try
            {
                _allDrivers = await DriverScannerService.ScanAsync(progress, _cts.Token);
                SubscribeAll(_allDrivers);
                ApplyFilter();
                UpdateSummaryCard();
                UpdateSelectionBar();
            }
            catch (OperationCanceledException)
            {
                StatusBar.Text = "Scan cancelled.";
            }
            catch (Exception ex)
            {
                StatusBar.Text = $"Error: {ex.Message}";
            }
            finally
            {
                ProgressSection.Visibility = Visibility.Collapsed;
                ScanBtn.IsEnabled          = true;
            }
        }

        // ── Filter pills ───────────────────────────────────────────────────────

        private void FilterAll_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            _activeFilter = null;
            FilterCritical.IsChecked = false;
            FilterOutdated.IsChecked = false;
            ApplyFilter();
        }

        private void FilterType_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (sender is not ToggleButton btn) return;

            _activeFilter = btn.Name == nameof(FilterCritical)
                ? DriverStatus.Critical
                : DriverStatus.Outdated;

            FilterAll.IsChecked = false;
            if (btn.Name == nameof(FilterCritical)) FilterOutdated.IsChecked = false;
            else                                     FilterCritical.IsChecked = false;

            ApplyFilter();
        }

        private void Filter_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            if (FilterCritical.IsChecked != true && FilterOutdated.IsChecked != true)
            {
                _activeFilter       = null;
                FilterAll.IsChecked = true;
            }
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var filtered = _activeFilter == null
                ? _allDrivers
                : _allDrivers.Where(d => d.Status == _activeFilter.Value).ToList();

            DriverList.ItemsSource = filtered;

            StatusBar.Text = _allDrivers.Count == 0
                ? "No drivers with known updates found — or scan not yet run."
                : $"Showing {filtered.Count} of {_allDrivers.Count} drivers.";
        }

        // ── Select All ─────────────────────────────────────────────────────────

        private void SelectAll_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSelectAll) return;
            SetAllSelected(true);
        }

        private void SelectAll_Unchecked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _suppressSelectAll) return;
            SetAllSelected(false);
        }

        private void SetAllSelected(bool value)
        {
            // Temporarily suppress re-entrance while iterating
            _suppressSelectAll = true;
            foreach (var d in _allDrivers)
                d.IsSelected = value;
            _suppressSelectAll = false;

            UpdateSelectionBar();
        }

        // ── Per-entry PropertyChanged ──────────────────────────────────────────

        private void SubscribeAll(IEnumerable<DriverEntry> entries)
        {
            foreach (var d in entries)
                d.PropertyChanged += OnEntryPropertyChanged;
        }

        private void UnsubscribeAll(IEnumerable<DriverEntry> entries)
        {
            foreach (var d in entries)
                d.PropertyChanged -= OnEntryPropertyChanged;
        }

        private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DriverEntry.IsSelected))
                UpdateSelectionBar();
        }

        // ── Selection bar ──────────────────────────────────────────────────────

        private void UpdateSelectionBar()
        {
            int count = _allDrivers.Count(d => d.IsSelected);

            if (count == 0)
            {
                BatchBar.Visibility = Visibility.Collapsed;
                return;
            }

            BatchLabel.Text     = count == 1 ? "1 driver selected" : $"{count} drivers selected";
            BatchBar.Visibility = Visibility.Visible;
        }

        // ── Summary card ───────────────────────────────────────────────────────

        private void UpdateSummaryCard()
        {
            SummaryCard.Visibility = Visibility.Visible;

            int total    = _allDrivers.Count;
            int critical = _allDrivers.Count(d => d.Status == DriverStatus.Critical);

            if (total == 0)
            {
                SummaryCard.Background      = new SolidColorBrush(Color.FromRgb(0x0A, 0x2E, 0x1A));
                SummaryCard.BorderBrush     = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                SummaryCard.BorderThickness = new Thickness(1);
                SummaryIcon.Text            = "✓";
                SummaryIcon.Foreground      = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                SummaryTitle.Text           = "All known drivers are up to date.";
                SummaryTitle.Foreground     = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
                SummarySubtitle.Text        = "No outdated drivers with available updates were found.";
                SummarySubtitle.Foreground  = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            }
            else
            {
                bool hasCritical = critical > 0;
                var accent = hasCritical
                    ? Color.FromRgb(0xEF, 0x44, 0x44)
                    : Color.FromRgb(0xF5, 0x9E, 0x0B);
                var bg = hasCritical
                    ? Color.FromRgb(0x2E, 0x1A, 0x1A)
                    : Color.FromRgb(0x2B, 0x20, 0x0A);

                SummaryCard.Background      = new SolidColorBrush(bg);
                SummaryCard.BorderBrush     = new SolidColorBrush(accent);
                SummaryCard.BorderThickness = new Thickness(1);
                SummaryIcon.Text            = "⚠";
                SummaryIcon.Foreground      = new SolidColorBrush(accent);
                SummaryTitle.Text           = total == 1 ? "1 driver needs updating"
                                                         : $"{total} drivers need updating";
                SummaryTitle.Foreground     = new SolidColorBrush(accent);
                SummarySubtitle.Text        = critical > 0
                    ? $"{critical} critical — update these first to avoid instability."
                    : "Click Download Update on any driver to open the manufacturer's download page.";
                SummarySubtitle.Foreground  = new SolidColorBrush(accent);
            }

            StatusBar.Text = total == 0
                ? "Scan complete — no driver updates required."
                : BuildStatusSummary();
        }

        private string BuildStatusSummary()
        {
            int critical = _allDrivers.Count(d => d.Status == DriverStatus.Critical);
            int outdated = _allDrivers.Count(d => d.Status == DriverStatus.Outdated);
            var parts    = new List<string>();
            if (critical > 0) parts.Add($"{critical} critical");
            if (outdated > 0) parts.Add($"{outdated} outdated");
            return "Found: " + string.Join(", ", parts) + " — update as soon as possible.";
        }

        // ── Individual download button ──────────────────────────────────────────

        private void DownloadUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var url = btn.Tag as string ?? (btn.DataContext as DriverEntry)?.UpdateUrl;
            if (string.IsNullOrEmpty(url)) return;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
        }

        // ── Batch: Open All Download Pages ────────────────────────────────────

        private async void OpenAllPages_Click(object sender, RoutedEventArgs e)
        {
            var selected = _allDrivers.Where(d => d.IsSelected).ToList();
            if (selected.Count == 0) return;

            OpenAllBtn.IsEnabled   = false;
            CopyLinksBtn.IsEnabled = false;

            foreach (var entry in selected)
            {
                try { Process.Start(new ProcessStartInfo(entry.UpdateUrl) { UseShellExecute = true }); }
                catch { }

                // Stagger opens so the browser isn't slammed all at once
                await Task.Delay(500);
            }

            OpenAllBtn.IsEnabled   = true;
            CopyLinksBtn.IsEnabled = true;
            StatusBar.Text         = $"✓ Opened {selected.Count} download page(s) in browser.";
        }

        // ── Batch: Copy All Links ─────────────────────────────────────────────

        private void CopyAllLinks_Click(object sender, RoutedEventArgs e)
        {
            var selected = _allDrivers.Where(d => d.IsSelected).ToList();
            if (selected.Count == 0) return;

            string text = string.Join(Environment.NewLine,
                selected.Select(d => d.UpdateUrl));

            try
            {
                Clipboard.SetText(text);
                StatusBar.Text = $"✓ {selected.Count} link(s) copied to clipboard.";
            }
            catch (Exception ex)
            {
                StatusBar.Text = $"Copy failed: {ex.Message}";
            }
        }
    }
}
