using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BurgerDeleter.Models
{
    public enum ItemCategory
    {
        Folder,
        Game,
        VideoFile,
        ArchiveFile,
        Application,
        OtherFile
    }

    public enum SafetyLevel
    {
        Safe             = 0,
        ManagedElsewhere = 1,
        Caution          = 2,
        Danger           = 3
    }

    public class DriveItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public ItemCategory Category { get; set; }

        public SafetyLevel SafetyLevel { get; set; } = SafetyLevel.Safe;
        public string SafetyReason { get; set; } = "No critical dependencies detected.";

        public string? RecommendationReason   { get; set; }
        public string? RecommendationCategory { get; set; }

        public DateTime LastModified { get; set; }

        // Display label e.g. "Steam" "Epic Games"
        public string? GameLauncher { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        // ===== Computed display properties =====

        public string SizeDisplay
        {
            get
            {
                if (SizeBytes >= 1_073_741_824)
                    return $"{SizeBytes / 1_073_741_824.0:F2} GB";
                if (SizeBytes >= 1_048_576)
                    return $"{SizeBytes / 1_048_576.0:F1} MB";
                if (SizeBytes >= 1_024)
                    return $"{SizeBytes / 1_024.0:F0} KB";
                return $"{SizeBytes} B";
            }
        }

        public string CategoryIcon => Category switch
        {
            ItemCategory.Game        => "🎮",
            ItemCategory.VideoFile   => "📹",
            ItemCategory.ArchiveFile => "📦",
            ItemCategory.Application => "⚙️",
            ItemCategory.Folder      => "📁",
            _                        => "📄"
        };

        public string Location => System.IO.Path.GetDirectoryName(FullPath) ?? FullPath;

        public string LastModifiedDisplay => LastModified == default
            ? "—"
            : LastModified.ToString("yyyy-MM-dd");

        public string CategoryLabel => Category switch
        {
            ItemCategory.Game        => GameLauncher ?? "Game",
            ItemCategory.VideoFile   => "Video File",
            ItemCategory.ArchiveFile => "Archive",
            ItemCategory.Application => "Application",
            ItemCategory.Folder      => "Folder",
            _                        => "File"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
