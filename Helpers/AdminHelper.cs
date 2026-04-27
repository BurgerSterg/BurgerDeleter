using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace BurgerDeleter.Helpers
{
    public static class AdminHelper
    {
        public static bool IsElevated()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>
        /// If already elevated, returns true immediately.
        /// Otherwise shows a prompt offering to relaunch as admin.
        /// Returns false in all cases where the caller should abort the operation.
        /// </summary>
        public static bool EnsureElevatedOrRelaunch()
        {
            if (IsElevated()) return true;

            var answer = MessageBox.Show(
                "Administrator access is needed to delete files and clean up registry entries.\n\n" +
                "Restart BurgerDeleter as administrator?",
                "Admin Access Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return false;

            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName
                          ?? Environment.ProcessPath
                          ?? throw new InvalidOperationException("Cannot determine executable path.");

                Process.Start(new ProcessStartInfo
                {
                    FileName        = exe,
                    UseShellExecute = true,
                    Verb            = "runas"
                });

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Could not relaunch as administrator:\n{ex.Message}",
                    "Elevation Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return false;
        }
    }
}
