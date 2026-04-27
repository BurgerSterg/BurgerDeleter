using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace BurgerDeleter
{
    public partial class App : Application
    {
        private static bool _isDarkTheme = true;

        protected override void OnStartup(StartupEventArgs e)
        {
            DispatcherUnhandledException += (s, ex) =>
            {
                var msg = $"{ex.Exception.GetType().FullName}: {ex.Exception.Message}\n\n{ex.Exception.StackTrace}";
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "runtime-error.log"), msg); }
                catch { }
                MessageBox.Show(msg, "Runtime Error — see runtime-error.log", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                var msg = (ex.ExceptionObject as Exception)?.ToString() ?? ex.ExceptionObject.ToString();
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fatal-error.log"), msg); }
                catch { }
                MessageBox.Show($"Fatal error:\n\n{msg}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            base.OnStartup(e);
        }

        public static bool IsDarkTheme => _isDarkTheme;

        public static void ToggleTheme()
        {
            _isDarkTheme = !_isDarkTheme;
            ApplyTheme(_isDarkTheme ? "Dark" : "Light");
        }

        private static void ApplyTheme(string themeName)
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri($"Themes/{themeName}Theme.xaml", UriKind.Relative)
            };
            Current.Resources.MergedDictionaries[0] = dict;
        }
    }
}
