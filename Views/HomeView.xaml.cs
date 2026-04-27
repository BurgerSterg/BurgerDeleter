using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BurgerDeleter.Views
{
    public partial class HomeView : Page
    {
        // MainWindow subscribes to this to navigate on scan click
        public event Action? ScanRequested;
        public event Action? GamesRequested;
        public event Action? LargeFilesRequested;
        public event Action? UninstallerRequested;

        public HomeView()
        {
            InitializeComponent();
            LoadDriveStats();
        }

        private void LoadDriveStats()
        {
            try
            {
                var drive = new DriveInfo("C");
                if (!drive.IsReady) return;

                double totalGB = drive.TotalSize / 1_073_741_824.0;
                double freeGB  = drive.AvailableFreeSpace / 1_073_741_824.0;
                double usedGB  = totalGB - freeGB;

                UsedSpaceLabel.Text  = $"{usedGB:F1} GB";
                FreeSpaceLabel.Text  = $"{freeGB:F1} GB";
                TotalSpaceLabel.Text = $"{totalGB:F0} GB";
            }
            catch
            {
                UsedSpaceLabel.Text  = "N/A";
                FreeSpaceLabel.Text  = "N/A";
                TotalSpaceLabel.Text = "N/A";
            }
        }

        private void ScanNowButton_Click(object sender, RoutedEventArgs e)
            => ScanRequested?.Invoke();

        private void QuickAction_Games(object sender, MouseButtonEventArgs e)
            => GamesRequested?.Invoke();

        private void QuickAction_LargeFiles(object sender, MouseButtonEventArgs e)
            => LargeFilesRequested?.Invoke();

        private void QuickAction_Uninstaller(object sender, MouseButtonEventArgs e)
            => UninstallerRequested?.Invoke();
    }
}
