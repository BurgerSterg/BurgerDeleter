using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BurgerDeleter.Models
{
    public enum DriverStatus { Outdated, Critical }

    public class DriverEntry : INotifyPropertyChanged
    {
        private bool _isSelected;

        /// <summary>Raw WMI device name (kept for internal use).</summary>
        public string DeviceName    { get; set; } = string.Empty;

        /// <summary>Human-readable device name (trademark symbols stripped).</summary>
        public string FriendlyName  { get; set; } = string.Empty;

        public string DriverVersion { get; set; } = "—";
        public string Provider      { get; set; } = string.Empty;
        public string DeviceClass   { get; set; } = string.Empty;
        public DriverStatus Status  { get; set; } = DriverStatus.Outdated;
        public string StatusReason  { get; set; } = string.Empty;

        /// <summary>Human-readable date, e.g. "Last updated: March 2022".</summary>
        public string FriendlyDate  { get; set; } = "Release date unknown";

        /// <summary>
        /// Direct download page URL.  Only populated when we have a known mapping;
        /// entries without a URL are excluded from results entirely.
        /// </summary>
        public string UpdateUrl     { get; set; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
