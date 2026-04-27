using System.IO;
using System.Windows;
using System.Windows.Controls;
using BurgerDeleter.Services;

namespace BurgerDeleter.Views
{
    public partial class FileOrganizerView : Page
    {
        private readonly FileOrganizerService _organizer = new();

        private List<PlannedMove> _plan = new();
        private CancellationTokenSource? _cts;

        public FileOrganizerView()
        {
            InitializeComponent();
            FolderPathBox.Text = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        // ===== FOLDER PICKER =====

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title          = "Select a folder to organize",
                InitialDirectory = FolderPathBox.Text,
            };

            if (dialog.ShowDialog() == true)
            {
                FolderPathBox.Text = dialog.FolderName;
                ClearPreview();
            }
        }

        // ===== PREVIEW =====

        private async void Preview_Click(object sender, RoutedEventArgs e)
        {
            var folder = FolderPathBox.Text.Trim();
            if (!Directory.Exists(folder))
            {
                MessageBox.Show("Folder not found. Please select a valid folder.",
                    "Invalid Folder", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            PreviewBtn.IsEnabled   = false;
            OrganizeBtn.IsEnabled  = false;
            StatusLabel.Text       = "Building preview...";
            PreviewEmptyLabel.Visibility = Visibility.Collapsed;

            _cts = new CancellationTokenSource();

            try
            {
                _plan = await _organizer.PreviewAsync(folder, _cts.Token);
                PopulateTreeView(_plan);

                if (_plan.Count == 0)
                {
                    StatusLabel.Text = "Nothing to organize — all files are already sorted.";
                    OrganizeBtn.IsEnabled = false;
                    PreviewEmptyLabel.Text = "Nothing to organize — all files are already sorted.";
                    PreviewEmptyLabel.Visibility = Visibility.Visible;
                }
                else
                {
                    var catCount = _plan.Select(m => m.Category).Distinct().Count();
                    StatusLabel.Text      = $"Preview: {_plan.Count} file(s) will be moved into {catCount} folder(s). Click Organize Now to apply.";
                    OrganizeBtn.IsEnabled = true;
                }
            }
            catch (OperationCanceledException)
            {
                StatusLabel.Text = "Preview cancelled.";
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Error: {ex.Message}";
            }
            finally
            {
                PreviewBtn.IsEnabled = true;
            }
        }

        private void PopulateTreeView(List<PlannedMove> plan)
        {
            PreviewTree.Items.Clear();

            var byCategory = plan
                .GroupBy(m => m.Category)
                .OrderBy(g => g.Key);

            foreach (var group in byCategory)
            {
                var icon = group.Key switch
                {
                    "Images"     => "📷",
                    "Videos"     => "🎬",
                    "Audio"      => "🎵",
                    "Documents"  => "📄",
                    "Archives"   => "📦",
                    "Installers" => "⚙️",
                    "Code"       => "💻",
                    _            => "📁"
                };

                var folderNode = new TreeViewItem
                {
                    Header     = $"{icon}  {group.Key}\\  ({group.Count()} file{(group.Count() == 1 ? "" : "s")})",
                    IsExpanded = true,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (System.Windows.Media.Brush)Application.Current
                                     .Resources["TextPrimaryBrush"]
                };

                foreach (var move in group.OrderBy(m => Path.GetFileName(m.SourcePath)))
                {
                    folderNode.Items.Add(new TreeViewItem
                    {
                        Header     = $"  {Path.GetFileName(move.SourcePath)}",
                        FontWeight = FontWeights.Normal,
                        Foreground = (System.Windows.Media.Brush)Application.Current
                                         .Resources["TextSecondaryBrush"]
                    });
                }

                PreviewTree.Items.Add(folderNode);
            }
        }

        private void ClearPreview()
        {
            PreviewTree.Items.Clear();
            _plan.Clear();
            OrganizeBtn.IsEnabled         = false;
            PreviewEmptyLabel.Text        = "Click Preview to see how files will be organized.";
            PreviewEmptyLabel.Visibility  = Visibility.Visible;
            StatusLabel.Text              = "Choose a folder and click Preview to get started.";
        }

        // ===== ORGANIZE =====

        private async void Organize_Click(object sender, RoutedEventArgs e)
        {
            if (_plan.Count == 0) return;

            var folder = FolderPathBox.Text.Trim();
            var catCount = _plan.Select(m => m.Category).Distinct().Count();

            var confirm = MessageBox.Show(
                $"Move {_plan.Count} file(s) into {catCount} subfolder(s) inside:\n{folder}\n\nThis cannot be undone automatically.",
                "Confirm Organize",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            OrganizeBtn.IsEnabled  = false;
            PreviewBtn.IsEnabled   = false;
            BrowseBtn.IsEnabled    = false;
            ProgressSection.Visibility = Visibility.Visible;
            OrganizeProgressBar.Value  = 0;
            ProgressPercentLabel.Text  = "0%";

            _cts = new CancellationTokenSource();
            int total = _plan.Count;
            int done  = 0;

            var progress = new Progress<string>(msg =>
            {
                Dispatcher.Invoke(() =>
                {
                    ProgressStatusLabel.Text  = msg;
                    done++;
                    double pct = (done / (double)total) * 100;
                    OrganizeProgressBar.Value = pct;
                    ProgressPercentLabel.Text = $"{(int)pct}%";
                });
            });

            try
            {
                int moved = await _organizer.OrganizeAsync(folder, progress, _cts.Token);

                OrganizeProgressBar.Value = 100;
                ProgressPercentLabel.Text = "100%";
                ProgressStatusLabel.Text  = "Done.";

                var uniqueCats = _plan.Select(m => m.Category).Distinct().Count();
                StatusLabel.Text = $"✅  Moved {moved} file{(moved == 1 ? "" : "s")} into {uniqueCats} folder{(uniqueCats == 1 ? "" : "s")}.";

                await Task.Delay(1500);
                ProgressSection.Visibility = Visibility.Collapsed;

                // Refresh preview to show remaining unorganized files (if any)
                _plan.Clear();
                ClearPreview();
                StatusLabel.Text = $"Done! Moved {moved} file{(moved == 1 ? "" : "s")} into {uniqueCats} folder{(uniqueCats == 1 ? "" : "s")}.";
            }
            catch (OperationCanceledException)
            {
                StatusLabel.Text = "Organize cancelled.";
                ProgressSection.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Error: {ex.Message}";
                ProgressSection.Visibility = Visibility.Collapsed;
            }
            finally
            {
                OrganizeBtn.IsEnabled = false;
                PreviewBtn.IsEnabled  = true;
                BrowseBtn.IsEnabled   = true;
            }
        }
    }
}
