using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BurgerDeleter.Models
{
    public class InstalledProgram : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string  DisplayName   { get; set; } = string.Empty;
        public string? Publisher     { get; set; }
        public string? InstallDate   { get; set; }   // raw YYYYMMDD from registry
        public long    EstimatedSizeKB { get; set; } // registry EstimatedSize is in KB
        public string? DisplayVersion { get; set; }
        public string? RecommendReason { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        // ===== Computed display properties =====

        public string InstallDateDisplay
        {
            get
            {
                if (string.IsNullOrWhiteSpace(InstallDate) || InstallDate.Length != 8)
                    return "—";
                // YYYYMMDD → YYYY-MM-DD
                return $"{InstallDate[..4]}-{InstallDate[4..6]}-{InstallDate[6..8]}";
            }
        }

        public string SizeDisplay
        {
            get
            {
                if (EstimatedSizeKB <= 0) return "—";
                if (EstimatedSizeKB >= 1_048_576) return $"{EstimatedSizeKB / 1_048_576.0:F2} GB";
                if (EstimatedSizeKB >= 1_024)     return $"{EstimatedSizeKB / 1_024.0:F1} MB";
                return $"{EstimatedSizeKB} KB";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
