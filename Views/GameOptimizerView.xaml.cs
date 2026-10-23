using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using BurgerDeleter.Models;
using BurgerDeleter.Services;

namespace BurgerDeleter.Views
{
    /// <summary>An entry in the game picker: a preset (matched by process name) or a detected install.</summary>
    public sealed class GameTarget
    {
        public string  Name        { get; init; } = string.Empty;
        public string? InstallPath { get; init; }
        public string? Launcher    { get; init; }

        public string Display => Launcher is null ? Name : $"{Name}  ·  {Launcher}";
    }

    public partial class GameOptimizerView : Page
    {
        private static readonly string[] PresetGames =
        {
            "Valorant", "Call of Duty", "Fortnite", "Apex Legends", "Rocket League",
            "NBA 2K", "CS2", "Rainbow Six Siege", "Other",
        };

        private OptimizationSnapshot? _sessionSnapshot;
        private readonly ObservableCollection<string> _appliedItems = new();
        private readonly ObservableCollection<GameTarget> _games = new();
        private List<DriveItem> _installedGames = new();
        private bool _gamesLoaded;

        public GameOptimizerView()
        {
            InitializeComponent();
            foreach (var name in PresetGames)
                _games.Add(new GameTarget { Name = name });
            GameCombo.ItemsSource = _games;
            GameCombo.SelectedIndex = 0;
            AppliedStepsList.ItemsSource = _appliedItems;
        }

        public void OnNavigatedTo()
        {
            if (IdlePanel.Visibility == Visibility.Visible)
                UpdateSessionBanner();

            if (!_gamesLoaded)
            {
                _gamesLoaded = true;
                _ = LoadInstalledGamesAsync();
            }
        }

        private async Task LoadInstalledGamesAsync()
        {
            try
            {
                _installedGames = await new GameDetectorService().DetectAllGamesAsync();
            }
            catch { _installedGames = new(); }

            // Presets stay first; detected installs follow, alphabetically
            foreach (var g in _installedGames.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            {
                _games.Add(new GameTarget
                {
                    Name        = g.Name,
                    InstallPath = g.FullPath,
                    Launcher    = g.GameLauncher ?? "Installed",
                });
            }

            GameHintText.Text = _installedGames.Count > 0
                ? $"{_installedGames.Count} installed games detected. Launch your game, then hit Detect for the best results."
                : "No installed games detected. Pick a preset, or launch your game and hit Detect.";
        }

        private async void DetectRunning_Click(object sender, RoutedEventArgs e)
        {
            DetectRunningBtn.IsEnabled = false;
            GameHintText.Text = "Looking for a running game…";
            try
            {
                var running = await GameOptimizerService.DetectRunningGameAsync(_installedGames);
                if (running is null)
                {
                    GameHintText.Text = "No running game found. Launch the game first, or pick it from the list.";
                    return;
                }

                GameCombo.SelectedItem = _games.FirstOrDefault(g =>
                    string.Equals(g.InstallPath, running.FullPath, StringComparison.OrdinalIgnoreCase));
                GameHintText.Text = $"Detected {running.Name} running.";
            }
            finally
            {
                DetectRunningBtn.IsEnabled = true;
            }
        }

        private void UpdateSessionBanner()
        {
            if (!GameOptimizerService.SnapshotFileExists)
            {
                SessionBanner.Visibility = Visibility.Collapsed;
                return;
            }

            SessionBanner.Visibility = Visibility.Visible;
            SessionBannerText.Text = App.OptimizerSnapshotPendingAtLaunch
                ? "Optimizations are still active from last session."
                : "Optimizer changes are saved on disk — restore when you are done gaming.";
        }

        private void StopPulse()
        {
            if (Resources["StatusPulse"] is Storyboard sb)
            {
                sb.Stop(OptimizedPulseBorder);
                OptimizedPulseBorder.BeginAnimation(UIElement.OpacityProperty, null);
                OptimizedPulseBorder.Opacity = 1.0;
            }
        }

        private void ShowIdleState()
        {
            StopPulse();
            ActivePanel.Visibility = Visibility.Collapsed;
            IdlePanel.Visibility   = Visibility.Visible;
            _sessionSnapshot       = null;
            _appliedItems.Clear();
            StepStatusText.Text    = string.Empty;
            UpdateSessionBanner();
        }

        private void ShowActiveState(string gameTitle)
        {
            IdlePanel.Visibility   = Visibility.Collapsed;
            ActivePanel.Visibility = Visibility.Visible;
            SessionBanner.Visibility = Visibility.Collapsed;

            OptimizedTitleText.Text = $"Optimized for {gameTitle}";

            if (Resources["StatusPulse"] is Storyboard sb)
                sb.Begin(OptimizedPulseBorder, true);
        }

        private async void SessionBannerRestore_Click(object sender, RoutedEventArgs e)
        {
            var snap = GameOptimizerService.LoadSnapshotFromDisk() ?? _sessionSnapshot;
            if (snap is null)
            {
                GameOptimizerService.DeleteSnapshotFile();
                App.OptimizerSnapshotPendingAtLaunch = false;
                UpdateSessionBanner();
                return;
            }

            try
            {
                await GameOptimizerService.RestoreAllAsync(snap);
            }
            catch { /* ignore */ }

            GameOptimizerService.DeleteSnapshotFile();
            App.OptimizerSnapshotPendingAtLaunch = false;
            ShowIdleState();
        }

        private void RestoreDefaults_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            ToggleKillBg.IsChecked   = true;
            TogglePriority.IsChecked = true;
            TogglePower.IsChecked    = true;
            ToggleGameBar.IsChecked  = true;
            ToggleGameMode.IsChecked = true;
            ToggleScheduler.IsChecked = true;
            ToggleGpuPref.IsChecked  = true;
            ToggleMouseAccel.IsChecked = false;
            ToggleFlushRam.IsChecked = true;
            ToggleFlushDns.IsChecked = true;
            ToggleNagle.IsChecked    = true;
            ToggleNetPerf.IsChecked  = true;
        }

        private async void OptimizeNow_Click(object sender, RoutedEventArgs e)
        {
            var target = GameCombo.SelectedItem as GameTarget ?? new GameTarget { Name = "Other" };
            var game   = target.Name;

            IdlePanel.IsEnabled    = false;
            OptimizeNowBtn.IsEnabled = false;
            StepStatusText.Text    = string.Empty;

            var snap = new OptimizationSnapshot { GameName = game, GameInstallPath = target.InstallPath };
            IProgress<string> stepProgress = new System.Progress<string>(s =>
                Dispatcher.Invoke(() => StepStatusText.Text = s));

            try
            {
                if (ToggleKillBg.IsChecked == true)
                {
                    int killed = await GameOptimizerService.KillBackgroundProcessesAsync(
                        game, stepProgress, CancellationToken.None);
                    snap.AppliedSteps.Add($"Killed background processes ({killed} stopped)");
                }

                if (TogglePriority.IsChecked == true)
                {
                    stepProgress.Report("Setting game priority…");
                    int before = snap.ProcessPriorities.Count;
                    GameOptimizerService.SetGamePriorityHigh(game, snap);
                    if (snap.ProcessPriorities.Count > before)
                        snap.AppliedSteps.Add("Set game priority to High");
                    else
                        snap.AppliedSteps.Add("Game priority — no running match (launch the game first)");
                }

                if (TogglePower.IsChecked == true)
                {
                    stepProgress.Report("Applying power plan…");
                    GameOptimizerService.SetUltimatePerformancePlan(snap);
                    snap.AppliedSteps.Add(
                        snap.UsedHighPerformanceFallback
                            ? "Applied High Performance power plan (Ultimate Performance unavailable)"
                            : "Applied Ultimate Performance power plan");
                }

                if (ToggleGameBar.IsChecked == true)
                {
                    stepProgress.Report("Updating Game Bar settings…");
                    GameOptimizerService.DisableGameBar(snap);
                    snap.AppliedSteps.Add("Disabled Xbox Game Bar background recording");
                }

                if (ToggleGameMode.IsChecked == true)
                {
                    stepProgress.Report("Enabling Game Mode…");
                    GameOptimizerService.EnableGameMode(snap);
                    snap.AppliedSteps.Add("Enabled Windows Game Mode");
                }

                if (ToggleScheduler.IsChecked == true)
                {
                    stepProgress.Report("Prioritizing games in the scheduler…");
                    int changed = GameOptimizerService.PrioritizeGamesInScheduler(snap);
                    snap.AppliedSteps.Add(changed > 0
                        ? "Raised CPU/GPU scheduling priority for games"
                        : "Game scheduling priority — no changes (Administrator required)");
                }

                if (ToggleGpuPref.IsChecked == true)
                {
                    stepProgress.Report("Setting GPU preference…");
                    var exes = GameOptimizerService.GetRunningGameExePaths(game, target.InstallPath);
                    int set = GameOptimizerService.SetHighPerformanceGpu(exes, snap);
                    snap.AppliedSteps.Add(set > 0
                        ? $"Set high-performance GPU for {set} game exe(s) — applies next launch"
                        : "GPU preference — no running game exe found (launch the game first)");
                }

                if (ToggleMouseAccel.IsChecked == true)
                {
                    stepProgress.Report("Disabling mouse acceleration…");
                    GameOptimizerService.DisableMouseAcceleration(snap);
                    snap.AppliedSteps.Add("Disabled mouse acceleration (Enhance pointer precision)");
                }

                if (ToggleFlushRam.IsChecked == true)
                {
                    await GameOptimizerService.FlushRamAsync(stepProgress);
                    snap.AppliedSteps.Add("Flushed RAM (working sets trimmed)");
                }

                if (ToggleFlushDns.IsChecked == true)
                {
                    await GameOptimizerService.FlushDnsAsync(stepProgress);
                    snap.AppliedSteps.Add("Flushed DNS cache");
                }

                if (ToggleNagle.IsChecked == true)
                {
                    stepProgress.Report("Updating TCP parameters…");
                    int before = snap.NagleInterfaces.Count;
                    GameOptimizerService.DisableNaglesAlgorithm(snap);
                    int added = snap.NagleInterfaces.Count - before;
                    snap.AppliedSteps.Add(added > 0
                        ? $"Disabled Nagle's Algorithm ({added} interfaces)"
                        : "Nagle's Algorithm — no changes (Administrator may be required)");
                }

                if (ToggleNetPerf.IsChecked == true)
                {
                    stepProgress.Report("Tuning TCP stack…");
                    await GameOptimizerService.SetNetworkAdapterMaxPerformanceAsync(snap);
                    snap.AppliedSteps.Add("Tuned global TCP settings (netsh)");
                }

                _sessionSnapshot = snap;

                _appliedItems.Clear();
                foreach (var step in snap.AppliedSteps)
                    _appliedItems.Add(step);

                GameOptimizerService.SaveSnapshotToDisk(snap);
                ShowActiveState(game);
            }
            finally
            {
                IdlePanel.IsEnabled      = true;
                OptimizeNowBtn.IsEnabled = true;
                StepStatusText.Text      = string.Empty;
            }
        }

        private async void RestoreReset_Click(object sender, RoutedEventArgs e)
        {
            var snap = _sessionSnapshot ?? GameOptimizerService.LoadSnapshotFromDisk();
            if (snap is null) return;

            RestoreResetBtn.IsEnabled = false;
            try
            {
                await GameOptimizerService.RestoreAllAsync(snap);
            }
            finally
            {
                RestoreResetBtn.IsEnabled = true;
            }

            GameOptimizerService.DeleteSnapshotFile();
            App.OptimizerSnapshotPendingAtLaunch = false;
            ShowIdleState();
        }
    }
}
