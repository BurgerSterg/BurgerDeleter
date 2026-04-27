using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BurgerDeleter.Models
{
    public enum StartupImpact { Unknown, Low, Medium, High }

    public enum StartupEntrySource { Registry, StartupFolder, TaskScheduler }

    public class StartupEntry : INotifyPropertyChanged
    {
        private bool _isEnabled;
        private bool _isSelected;

        // Display properties
        public string  Name      { get; set; } = string.Empty;
        public string? Publisher { get; set; }
        public string  Command   { get; set; } = string.Empty;
        public string  Location  { get; set; } = string.Empty;

        public StartupImpact       Impact { get; set; } = StartupImpact.Unknown;
        public StartupEntrySource  Source { get; set; }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        // --- Source-specific fields (used by SetEntryEnabled) ---

        // Registry entries
        public Microsoft.Win32.RegistryHive? RegistryHive      { get; set; }
        public string?                        RegistryKeyPath   { get; set; }
        public string?                        RegistryValueName { get; set; } // current name (may have '-' prefix)

        // Startup folder entries
        public string? FilePath { get; set; }

        // Task Scheduler entries
        public string? TaskName { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
