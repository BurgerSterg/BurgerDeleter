using System.IO;
using System.Windows;

namespace BurgerDeleter
{
    public static class Program
    {
        [System.STAThread]
        public static void Main()
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                app.Run();
            }
            catch (Exception ex)
            {
                var msg = $"{ex.GetType().FullName}: {ex.Message}\n\n{ex.StackTrace}";

                // Write to a log file so we can read it even if no window appears
                try
                {
                    File.WriteAllText(
                        Path.Combine(AppContext.BaseDirectory, "startup-error.log"), msg);
                }
                catch { }

                MessageBox.Show(
                    $"Fatal startup error — see startup-error.log for full details.\n\n{msg}",
                    "BurgerDeleter — Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
