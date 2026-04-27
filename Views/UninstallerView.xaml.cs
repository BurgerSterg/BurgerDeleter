using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using BurgerDeleter.Models;
using BurgerDeleter.Services;
using Microsoft.Win32;

namespace BurgerDeleter.Views
{
    public partial class UninstallerView : Page
    {
        private readonly UninstallerService _uninstaller = new();

        private List<InstalledProgram> _allPrograms      = new();
        private List<InstalledProgram> _filteredPrograms = new();
        private CancellationTokenSource? _cts;

        // Registry paths to read from all three hives
        private static readonly (RegistryKey Hive, string Path)[] RegistrySources =
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        public UninstallerView()
        {
            InitializeComponent();
        }

        // ===== LOAD =====

        private void LoadPrograms_Click(object sender, RoutedEventArgs e)
        {
            LoadProgramsBtn.IsEnabled = false;
            _allPrograms.Clear();
            _filteredPrograms.Clear();

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var (hive, path) in RegistrySources)
            {
                try
                {
                    using var key = hive.OpenSubKey(path);
                    if (key == null) continue;

                    foreach (var subKeyName in key.GetSubKeyNames())
                    {
                        try
                        {
                            using var sub = key.OpenSubKey(subKeyName);
                            if (sub == null) continue;

                            var displayName = sub.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(displayName)) continue;
                            if (!seen.Add(displayName)) continue; // deduplicate across hives

                            var sizeRaw = sub.GetValue("EstimatedSize");
                            long sizeKB = 0;
                            if (sizeRaw is int sizeInt) sizeKB = sizeInt;
                            else if (sizeRaw is long sizeLong) sizeKB = sizeLong;

                            _allPrograms.Add(new InstalledProgram
                            {
                                DisplayName     = displayName,
                                Publisher       = sub.GetValue("Publisher")      as string,
                                InstallDate     = sub.GetValue("InstallDate")    as string,
                                DisplayVersion  = sub.GetValue("DisplayVersion") as string,
                                EstimatedSizeKB = sizeKB
                            });
                        }
                        catch { }
                    }
                }
                catch { }
            }

            // Sort alphabetically
            _allPrograms.Sort((a, b) =>
                string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));

            ApplySearch();
            PopulateRecommendations();
            LoadProgramsBtn.IsEnabled = true;
        }

        // ===== RECOMMENDATIONS =====

        private static readonly string[] _bgUtilKeywords =
            ["toolbar", "assistant", "updater", "helper", "notify", "agent", "booster", "cleaner", "optimizer"];

        private static readonly Regex _versionInNameRegex =
            new(@"\b\d+\.\d+\b", RegexOptions.Compiled);

        private void PopulateRecommendations()
        {
            var now = DateTime.Now;

            var candidates = new List<(InstalledProgram prog, DateTime installDt)>();

            foreach (var prog in _allPrograms)
            {
                // Parse YYYYMMDD install date
                DateTime? installDt = null;
                if (prog.InstallDate?.Length == 8 &&
                    DateTime.TryParseExact(prog.InstallDate, "yyyyMMdd",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    installDt = dt;

                string? reason = null;

                // 1. Installed over 1 year ago — likely unused
                if (reason == null && installDt.HasValue &&
                    (now - installDt.Value).TotalDays > 365)
                {
                    int months = (int)((now - installDt.Value).TotalDays / 30.44);
                    reason = $"Installed {months} months ago, likely unused";
                }

                // 2. Background utility keyword match
                if (reason == null)
                {
                    var lower = prog.DisplayName.ToLowerInvariant();
                    if (_bgUtilKeywords.Any(k => lower.Contains(k)))
                        reason = "Background utility — often unnecessary";
                }

                // 3. Unknown publisher
                if (reason == null && string.IsNullOrWhiteSpace(prog.Publisher))
                    reason = "Unknown publisher";

                // 4. Old small utility (<50 MB, >2 years)
                if (reason == null && installDt.HasValue &&
                    (now - installDt.Value).TotalDays > 730 &&
                    prog.EstimatedSizeKB > 0 && prog.EstimatedSizeKB < 50_000)
                {
                    reason = $"Old small utility from {installDt.Value.Year}";
                }

                // 5. Version number embedded in display name
                if (reason == null && _versionInNameRegex.IsMatch(prog.DisplayName))
                    reason = "May be an outdated version";

                if (reason == null) continue;

                prog.RecommendReason = reason;
                candidates.Add((prog, installDt ?? DateTime.MinValue));
            }

            // Prioritise oldest install date first, cap at 8
            var recs = candidates
                .OrderBy(c => c.installDt)
                .Take(8)
                .Select(c => c.prog)
                .ToList();

            RecommendedCardsPanel.ItemsSource = recs;
            RecommendedSection.Visibility = recs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RecommendCard_Remove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not InstalledProgram prog) return;
            if (!_allPrograms.Contains(prog)) return;

            // Clear search so the program is visible in the full list
            SearchBox.Text = "";
            // ApplySearch() is triggered by TextChanged, but force it for safety
            ApplySearch();

            // Deselect everything then select just this program
            foreach (var p in _filteredPrograms)
                p.IsSelected = false;
            prog.IsSelected = true;

            // Scroll the list to the target row
            ProgramsList.ScrollIntoView(prog);

            // Kick off the delete flow (shows confirm dialog, then uninstalls)
            DeleteSelected_Click(sender, e);
        }

        // ===== SEARCH =====

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();

        private void ApplySearch()
        {
            var query = SearchBox.Text.Trim();

            _filteredPrograms = string.IsNullOrEmpty(query)
                ? _allPrograms.ToList()
                : _allPrograms
                    .Where(p => p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                (p.Publisher?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
                    .ToList();

            ProgramsList.ItemsSource = null;
            ProgramsList.ItemsSource = _filteredPrograms;

            foreach (var prog in _filteredPrograms)
                prog.PropertyChanged += (s, args) =>
                {
                    if (args.PropertyName == nameof(InstalledProgram.IsSelected))
                        Dispatcher.Invoke(UpdateSelectionLabel);
                };

            ProgramCountLabel.Text = _allPrograms.Count > 0
                ? $"{_filteredPrograms.Count} of {_allPrograms.Count} programs"
                : string.Empty;

            UpdateSelectionLabel();
        }

        // ===== SELECTION =====

        private void UpdateSelectionLabel()
        {
            int count = _filteredPrograms.Count(p => p.IsSelected);
            DeleteSelectedBtn.IsEnabled = count > 0;
            SelectionHintLabel.Text = count > 0
                ? $"{count} program(s) selected for deep uninstall."
                : "Check programs above to select them for removal.";
        }

        // ===== DELETE =====

        private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            if (!Helpers.AdminHelper.EnsureElevatedOrRelaunch()) return;

            var selected = _filteredPrograms.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0) return;

            var nameList = string.Join("\n", selected.Select(p => $"  • {p.DisplayName}"));
            var confirm  = MessageBox.Show(
                $"Deep uninstall {selected.Count} program(s)?\n\n{nameList}\n\n" +
                "This removes registry entries, leftover files, startup entries, scheduled tasks, and services. " +
                "This cannot be undone.",
                "Confirm Deep Uninstall",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            _cts = new CancellationTokenSource();

            DeleteSelectedBtn.IsEnabled = false;
            LoadProgramsBtn.IsEnabled   = false;
            ProgressSection.Visibility  = Visibility.Visible;

            int done = 0;

            foreach (var program in selected)
            {
                LayerStatusLabel.Text = $"Uninstalling {program.DisplayName}...";

                var progress = new Progress<string>(msg =>
                    Dispatcher.Invoke(() => LayerStatusLabel.Text = $"{program.DisplayName}: {msg}"));

                try
                {
                    await _uninstaller.UninstallAsync(
                        program.DisplayName,
                        directPath: null,
                        progress:   progress,
                        ct:         _cts.Token);

                    _allPrograms.Remove(program);
                    _filteredPrograms.Remove(program);

                    ProgramsList.ItemsSource = null;
                    ProgramsList.ItemsSource = _filteredPrograms;

                    done++;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Error uninstalling {program.DisplayName}:\n{ex.Message}",
                        "Uninstall Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }

            LayerStatusLabel.Text      = $"Done. Removed {done} program(s).";
            UninstallProgress.IsIndeterminate = false;
            UninstallProgress.Value    = 100;

            UpdateSelectionLabel();
            LoadProgramsBtn.IsEnabled  = true;

            ProgramCountLabel.Text = _allPrograms.Count > 0
                ? $"{_filteredPrograms.Count} of {_allPrograms.Count} programs"
                : string.Empty;

            AppEvents.RaiseDriveStatsChanged();
        }
    }
}
