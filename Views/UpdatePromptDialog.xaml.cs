using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using BurgerDeleter.Services;

namespace BurgerDeleter.Views
{
    public partial class UpdatePromptDialog : Window
    {
        private readonly UpdateInfo _info;

        public UpdatePromptDialog(UpdateInfo info)
        {
            InitializeComponent();
            _info = info;

            var local = Assembly.GetExecutingAssembly().GetName().Version;
            CurrentVersionText.Text = $"Current version:  {local?.ToString(3) ?? "unknown"}";
            NewVersionText.Text     = $"New version:  {info.TagName}";
        }

        // ── Window drag ────────────────────────────────────────────────────────

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        // ── Update Now ─────────────────────────────────────────────────────────

        private async void UpdateNow_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_info.AssetUrl))
            {
                MessageBox.Show(
                    this,
                    "No downloadable .exe asset was found in this release.\n\n" +
                    "Visit the GitHub releases page to download manually.",
                    "No Asset Found",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            UpdateNowBtn.IsEnabled     = false;
            LaterBtn.IsEnabled         = false;
            DownloadSection.Visibility = Visibility.Visible;

            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), "BurgerDeleter_update.exe");

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                http.DefaultRequestHeaders.Add("User-Agent", "BurgerDeleter");

                using var response = await http.GetAsync(
                    _info.AssetUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;

                await using var networkStream =
                    await response.Content.ReadAsStreamAsync();
                await using var fileStream =
                    new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                                   FileShare.None, 65536, useAsync: true);

                var  buffer     = new byte[65536];
                long downloaded = 0;
                int  bytesRead;

                while ((bytesRead = await networkStream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                    downloaded += bytesRead;

                    if (totalBytes is > 0)
                    {
                        double pct = downloaded / (double)totalBytes.Value * 100.0;
                        DownloadProgress.Value  = pct;
                        DownloadStatusText.Text =
                            $"Downloading…  {pct:F0}%  " +
                            $"({FormatBytes(downloaded)} / {FormatBytes(totalBytes.Value)})";
                    }
                    else
                    {
                        DownloadStatusText.Text = $"Downloading…  {FormatBytes(downloaded)}";
                    }
                }

                DownloadStatusText.Text = "Download complete — finishing update…";

                UpdateService.StartSelfUpdateReplacerBatch();
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    $"Download failed:\n\n{ex.Message}",
                    "Update Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                UpdateNowBtn.IsEnabled     = true;
                LaterBtn.IsEnabled         = true;
                DownloadSection.Visibility = Visibility.Collapsed;
            }
        }

        // ── Later ──────────────────────────────────────────────────────────────

        private void Later_Click(object sender, RoutedEventArgs e) => Close();

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1_048_576) return $"{bytes / 1_048_576.0:F1} MB";
            return $"{bytes / 1_024.0:F0} KB";
        }
    }
}
