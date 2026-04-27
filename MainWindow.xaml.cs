using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BurgerDeleter.Services;
using BurgerDeleter.Views;
using Hardcodet.Wpf.TaskbarNotification;

namespace BurgerDeleter
{
    public partial class MainWindow : Window
    {
        private readonly HomeView          _homeView          = new();
        private readonly ScanView          _scanView          = new();
        private readonly GamesView         _gamesView         = new();
        private readonly RecommendedView   _recommendedView   = new();
        private readonly UninstallerView   _uninstallerView   = new();
        private readonly FileOrganizerView _fileOrganizerView = new();
        private readonly MemoryView        _memoryView        = new();
        private readonly StartupView       _startupView       = new();
        private readonly NetworkView       _networkView       = new();
        private readonly DriverView        _driverView        = new();

        public MainWindow()
        {
            InitializeComponent();
            NavigateTo(NavHome, _homeView);
            LoadDriveInfo();
            CheckAdminStatus();

            _homeView.ScanRequested += () => NavigateTo(NavScan, _scanView);
            AppEvents.DriveStatsChanged += () => Dispatcher.Invoke(LoadDriveInfo);

            // Broadcast to all subscribers whenever the window appears / disappears
            IsVisibleChanged += (_, e) =>
            {
                if ((bool)e.NewValue)
                    AppEvents.RaiseAppVisible();
                else
                    AppEvents.RaiseAppHidden();
            };
        }

        // ===== NAVIGATION =====

        private Button? _activeNavButton;

        private void NavigateTo(Button navBtn, object view)
        {
            if (_activeNavButton != null)
                _activeNavButton.Style = (Style)Resources["NavButtonStyle"];

            navBtn.Style = (Style)Resources["NavButtonActiveStyle"];
            _activeNavButton = navBtn;
            MainFrame.Navigate(view);
        }

        private void NavHome_Click(object sender, RoutedEventArgs e)
            => NavigateTo(NavHome, _homeView);

        private void NavScan_Click(object sender, RoutedEventArgs e)
            => NavigateTo(NavScan, _scanView);

        private void NavGames_Click(object sender, RoutedEventArgs e)
            => NavigateTo(NavGames, _gamesView);

        private void NavRecommended_Click(object sender, RoutedEventArgs e)
            => NavigateTo(NavRecommended, _recommendedView);

        private void NavUninstaller_Click(object sender, RoutedEventArgs e)
            => NavigateTo(NavUninstaller, _uninstallerView);

        private void NavOrganizer_Click(object sender, RoutedEventArgs e)
            => NavigateTo(NavOrganizer, _fileOrganizerView);

        private void NavMemory_Click(object sender, RoutedEventArgs e)
            => NavigateTo(NavMemory, _memoryView);

        private void NavStartup_Click(object sender, RoutedEventArgs e)
            => NavigateTo(NavStartup, _startupView);

        private void NavNetwork_Click(object sender, RoutedEventArgs e)
            => NavigateTo(NavNetwork, _networkView);

        private void NavDrivers_Click(object sender, RoutedEventArgs e)
            => NavigateTo(NavDrivers, _driverView);

        // ===== AUTO-UPDATE CHECK =====

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            // Fire-and-forget: check GitHub for a newer release without blocking the UI.
            _ = Task.Run(async () =>
            {
                var info = await UpdateService.CheckForUpdatesAsync();
                if (info is null) return;

                Dispatcher.Invoke(() =>
                {
                    var dialog = new UpdatePromptDialog(info) { Owner = this };
                    dialog.ShowDialog();
                });
            });
        }

        // ===== THEME =====

        private void ThemeToggle_Click(object sender, RoutedEventArgs e)
            => App.ToggleTheme();

        // ===== DRIVE INFO (sidebar) =====

        private void LoadDriveInfo()
        {
            try
            {
                var drive = new System.IO.DriveInfo("C");
                if (!drive.IsReady) return;

                double totalGB  = drive.TotalSize / 1_073_741_824.0;
                double freeGB   = drive.AvailableFreeSpace / 1_073_741_824.0;
                double usedGB   = totalGB - freeGB;
                double usedPct  = (usedGB / totalGB) * 100;

                DriveProgressBar.Value = usedPct;
                DriveUsageLabel.Text = $"{usedGB:F1} GB used of {totalGB:F0} GB";

                // Turn bar red if <15% free
                if (freeGB / totalGB < 0.15)
                    DriveProgressBar.Foreground = (System.Windows.Media.Brush)
                        Application.Current.Resources["DangerBrush"];
            }
            catch { /* drive read failed, just skip */ }
        }

        // ===== ADMIN BADGE =====

        private void CheckAdminStatus()
        {
            bool isAdmin;
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { isAdmin = false; }

            if (isAdmin)
            {
                AdminBadge.Background    = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A2E1A"));
                AdminBadgeText.Text      = "✓ Admin";
                AdminBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22C55E"));
                AdminBadge.Cursor        = Cursors.Arrow;
                ((ToolTip)AdminBadge.ToolTip).Content = "Running with administrator privileges";
            }
            else
            {
                AdminBadge.Background    = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E1A1A"));
                AdminBadgeText.Text      = "✗ No Admin";
                AdminBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                AdminBadge.Cursor        = Cursors.Hand;
                ((ToolTip)AdminBadge.ToolTip).Content = "Click to relaunch as Administrator";
            }
        }

        private void AdminBadge_Click(object sender, MouseButtonEventArgs e)
        {
            using var identity = WindowsIdentity.GetCurrent();
            bool isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            if (isAdmin) return;

            var answer = MessageBox.Show(
                "Relaunch BurgerDeleter as Administrator?\n\nSome delete operations require elevated privileges.",
                "Relaunch as Admin",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName
                          ?? throw new InvalidOperationException("Cannot determine executable path.");
                Process.Start(new ProcessStartInfo(exe) { Verb = "runas", UseShellExecute = true });
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not relaunch as administrator:\n{ex.Message}",
                    "Elevation Failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===== TRAY / CLOSE BEHAVIOUR =====

        private static bool _hasShownTrayNotice;

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!App.AllowClose)
            {
                // Hide to tray instead of closing
                e.Cancel = true;
                Hide();

                if (!_hasShownTrayNotice)
                {
                    _hasShownTrayNotice = true;
                    ((App)Application.Current).TrayIcon?.ShowBalloonTip(
                        "BurgerDeleter",
                        "BurgerDeleter is still running in the background.",
                        BalloonIcon.Info);
                }
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnStateChanged(EventArgs e)
        {
            if (WindowState == WindowState.Minimized)
                Hide();
            base.OnStateChanged(e);
        }

        // ===== CUSTOM WINDOW CHROME =====

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
                MaximizeButton_Click(sender, e);
            else
                DragMove();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e)
            => Close();
    }
}
