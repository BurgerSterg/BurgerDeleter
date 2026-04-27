namespace BurgerDeleter
{
    public static class AppEvents
    {
        public static event Action? DriveStatsChanged;

        public static void RaiseDriveStatsChanged()
            => DriveStatsChanged?.Invoke();
    }
}
