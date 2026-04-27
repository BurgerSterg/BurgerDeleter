using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BurgerDeleter.Models
{
    public class NetworkEntry : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string ProcessName       { get; set; } = string.Empty;
        public int    PID               { get; set; }
        public string ExePath           { get; set; } = string.Empty;

        public double UploadBytesPerSec   { get; set; }
        public double DownloadBytesPerSec { get; set; }

        public double TotalBytesPerSec => UploadBytesPerSec + DownloadBytesPerSec;

        public string UploadDisplay   => FormatSpeed(UploadBytesPerSec);
        public string DownloadDisplay => FormatSpeed(DownloadBytesPerSec);

        /// <summary>Raw bytes/sec values for the last 10 seconds (oldest first).</summary>
        public List<double> Sparkline { get; set; } = Enumerable.Repeat(0.0, 10).ToList();

        /// <summary>Sparkline heights in pixels (0–20), used directly by XAML.</summary>
        public List<double> NormalizedSparkline
        {
            get
            {
                const double maxPx = 20.0;
                var max = Sparkline.Count > 0 ? Sparkline.Max() : 0;
                if (max <= 0) return Enumerable.Repeat(0.0, 10).ToList();
                return Sparkline.Select(v => Math.Max(1, v / max * maxPx)).ToList();
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }

        private static string FormatSpeed(double bytesPerSec)
        {
            if (bytesPerSec >= 1_048_576)  return $"{bytesPerSec / 1_048_576.0:F1} MB/s";
            if (bytesPerSec >= 1_024)      return $"{bytesPerSec / 1_024.0:F0} KB/s";
            return $"{bytesPerSec:F0} B/s";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
