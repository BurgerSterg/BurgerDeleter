namespace BurgerDeleter
{
    public static class AppEvents
    {
        public static event Action? DriveStatsChanged;
        public static void RaiseDriveStatsChanged() => DriveStatsChanged?.Invoke();

        /// <summary>
        /// Fired when the main window is hidden to the system tray.
        /// All background timers should pause; running scans should cancel.
        /// </summary>
        public static event Action? AppHidden;
        public static void RaiseAppHidden() => AppHidden?.Invoke();

        /// <summary>
        /// Fired when the main window is restored from the system tray.
        /// Background timers should resume at their normal rate.
        /// </summary>
        public static event Action? AppVisible;
        public static void RaiseAppVisible() => AppVisible?.Invoke();
    }
}
