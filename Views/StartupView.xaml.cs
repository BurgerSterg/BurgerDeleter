using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BurgerDeleter.Models;
using BurgerDeleter.Services;

namespace BurgerDeleter.Views
{
    public partial class StartupView : Page
    {
        private List<StartupEntry> _entries = new();
        private bool _loaded;

        public StartupView()
        {
            InitializeComponent();
        }

        // Load entries the first time the page becomes visible
        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (_loaded) return;
            _loaded = true;
            await LoadEntriesAsync();
        }

        private async void ScanBtn_Click(object sender, RoutedEventArgs e)
            => await LoadEntriesAsync();

        // ── Core load ──────────────────────────────────────────────────────────

        private async Task LoadEntriesAsync()
        {
            ScanBtn.IsEnabled     = false;
            LoadingLabel.Visibility = Visibility.Visible;

            try
            {
                _entries = await StartupManagerService.GetStartupEntriesAsync();
                WireToggleHandlers(_entries);
                EntriesList.ItemsSource = _entries;
                UpdateSummary();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to read startup entries:\n{ex.Message}",
                                "Startup Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                ScanBtn.IsEnabled       = true;
                LoadingLabel.Visibility = Visibility.Collapsed;
            }
        }

        // Wire PropertyChanged so toggling IsEnabled immediately calls the service
        private void WireToggleHandlers(IEnumerable<StartupEntry> entries)
        {
            foreach (var entry in entries)
            {
                entry.PropertyChanged += async (s, e) =>
                {
                    if (e.PropertyName != nameof(StartupEntry.IsEnabled)) return;
                    if (s is not StartupEntry se) return;

                    try
                    {
                        await StartupManagerService.SetEntryEnabledAsync(se, se.IsEnabled);
                    }
                    catch (Exception ex)
                    {
                        // Revert the toggle on failure
                        se.PropertyChanged -= null; // temporarily unhook to avoid re-entry
                        se.IsEnabled = !se.IsEnabled;
                        MessageBox.Show($"Could not change startup state for \"{se.Name}\":\n{ex.Message}",
                                        "Startup Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }

                    UpdateSummary();
                };
            }
        }

        // ── Summary ────────────────────────────────────────────────────────────

        private void UpdateSummary()
        {
            int total    = _entries.Count;
            int high     = _entries.Count(e => e.Impact    == StartupImpact.High);
            int disabled = _entries.Count(e => !e.IsEnabled);

            Dispatcher.Invoke(() =>
            {
                ChipTotalCount.Text   = total.ToString();
                ChipHighCount.Text    = high.ToString();
                ChipDisabledCount.Text = disabled.ToString();

                StatusBar.Text = high > 0
                    ? $"⚠️  {high} high-impact {(high == 1 ? "entry is" : "entries are")} slowing your boot."
                    : total == 0
                        ? "No startup entries loaded."
                        : $"✓  {total} startup {(total == 1 ? "entry" : "entries")} found - {disabled} disabled.";
            });
        }

        // ── Context menu ───────────────────────────────────────────────────────

        private void CtxEnable_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextEntry(sender) is { } entry)
                entry.IsEnabled = true;
        }

        private void CtxDisable_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextEntry(sender) is { } entry)
                entry.IsEnabled = false;
        }

        private void CtxOpenLocation_Click(object sender, RoutedEventArgs e)
        {
            if (GetContextEntry(sender) is not { } entry) return;

            string? path = entry.FilePath
                        ?? TryExtractExeFromCommand(entry.Command);

            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                Process.Start("explorer.exe", path);
            }
            else
            {
                MessageBox.Show("Could not locate the file on disk.",
                                "Open File Location", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // Gets the StartupEntry bound to the right-clicked row.
        // Falls back to ListView.SelectedItem because right-click also selects.
        private StartupEntry? GetContextEntry(object sender)
        {
            if (sender is MenuItem mi)
            {
                // Walk up: MenuItem → ContextMenu → find PlacementTarget's DataContext
                if (mi.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe)
                    if (fe.DataContext is StartupEntry se) return se;
            }
            return EntriesList.SelectedItem as StartupEntry;
        }

        private static string? TryExtractExeFromCommand(string? cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return null;
            cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
            if (cmd.StartsWith('"'))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0) return cmd[1..end];
            }
            int idx = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return cmd[..(idx + 4)];
            return cmd.Split(' ')[0];
        }
    }
}
