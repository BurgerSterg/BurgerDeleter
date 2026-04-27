using System.IO;
using System.Windows;
using System.Windows.Controls;
using BurgerDeleter.Models;
using BurgerDeleter.Services;

namespace BurgerDeleter.Views
{
    public partial class GamesView : Page
    {
        private readonly GameDetectorService _detector = new();

        private List<DriveItem> _games = new();
        private CancellationTokenSource? _cts;

        private double _currentFreeGB;

        public GamesView()
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

        private async void ScanGames_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();

            ScanGamesBtn.IsEnabled  = false;
            GamesList.ItemsSource   = null;
            _games.Clear();
            UpdateSelectionLabels();

            var progress = new Progress<string>(msg =>
                Dispatcher.Invoke(() => ScanStatusLabel.Text = msg));

            try
            {
                ScanStatusLabel.Text = "Scanning for games...";

                _games = await _detector.DetectAllGamesAsync(progress, _cts.Token);

                GamesList.ItemsSource = _games;

                foreach (var item in _games)
                    item.PropertyChanged += (s, args) =>
                    {
                        if (args.PropertyName == nameof(DriveItem.IsSelected))
                            Dispatcher.Invoke(UpdateSelectionLabels);
                    };

                ScanStatusLabel.Text = _games.Count > 0
                    ? $"Found {_games.Count} game(s). Check boxes to select for removal."
                    : "No games detected. Are your launchers installed?";
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
                ScanGamesBtn.IsEnabled = true;
            }
        }

        // ===== SELECTION TRACKING =====

        private void UpdateSelectionLabels()
        {
            long selectedBytes = _games
                .Where(i => i.IsSelected)
                .Sum(i => i.SizeBytes);

            double selectedGB  = selectedBytes / 1_073_741_824.0;
            double freeAfterGB = _currentFreeGB + selectedGB;

            SelectedSizeLabel.Text = selectedGB >= 1
                ? $"{selectedGB:F2} GB"
                : $"{selectedBytes / 1_048_576.0:F0} MB";

            FreeAfterLabel.Text = $"{freeAfterGB:F1} GB";

            int selectedCount = _games.Count(i => i.IsSelected);
            DeleteSelectedBtn.IsEnabled = selectedCount > 0;
            SelectionHintLabel.Text = selectedCount > 0
                ? $"{selectedCount} game(s) selected — {SelectedSizeLabel.Text} will be freed."
                : "Check games above to select them for removal.";
        }

        // ===== DELETE =====

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!Helpers.AdminHelper.EnsureElevatedOrRelaunch()) return;

            var selected = _games.Where(i => i.IsSelected).ToList();
            if (selected.Count == 0) return;

            // Build confirmation message listing every selected game
            var gameLines = string.Join("\n", selected.Select(i => $"  • {i.Name} ({i.SizeDisplay})"));
            var confirm = MessageBox.Show(
                $"Delete {selected.Count} game(s)?\n\n{gameLines}\n\n" +
                "This will permanently remove the game folder(s). " +
                "You will need to reinstall them to play again.",
                "Confirm Game Deletion",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            DeleteSelectedBtn.IsEnabled = false;
            ScanGamesBtn.IsEnabled      = false;

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
                    if (Directory.Exists(item.FullPath) || File.Exists(item.FullPath))
                        await ForceDeleteService.ForceDeleteAsync(item.FullPath);

                    _games.Remove(item);
                    GamesList.ItemsSource = null;
                    GamesList.ItemsSource = _games;

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
            ScanStatusLabel.Text      = $"Done. Deleted {done} game(s).";

            await Task.Delay(2000);
            DeletionProgressSection.Visibility = System.Windows.Visibility.Collapsed;

            ScanGamesBtn.IsEnabled = true;
        }
    }
}
