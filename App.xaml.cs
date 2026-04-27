using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using Velopack;

namespace BurgerDeleter
{
    public partial class App : Application
    {
        private static bool _isDarkTheme = true;

        // Held as a field so it isn't GC'd; also exposed so MainWindow can reach it.
        private TaskbarIcon? _trayIcon;

        /// <summary>Accessed by MainWindow to show the one-time "hiding to tray" balloon.</summary>
        public TaskbarIcon? TrayIcon => _trayIcon;

        /// <summary>
        /// Set to true by the tray Quit handler so OnClosing actually closes the window.
        /// </summary>
        public static bool AllowClose { get; internal set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Must be the very first call — Velopack handles install/update/uninstall
            // hooks and may call Environment.Exit() for lifecycle events.
            VelopackApp.Build().Run();

            DispatcherUnhandledException += (s, ex) =>
            {
                // Walk the full InnerException chain to capture the root cause
                var sb = new System.Text.StringBuilder();
                Exception? current = ex.Exception;
                int depth = 0;
                while (current != null)
                {
                    sb.AppendLine($"=== Exception (depth {depth}) ===");
                    sb.AppendLine($"{current.GetType().FullName}: {current.Message}");
                    sb.AppendLine(current.StackTrace);
                    sb.AppendLine();
                    current = current.InnerException;
                    depth++;
                }
                var msg = sb.ToString();
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "runtime-error.log"), msg); }
                catch { }
                MessageBox.Show(ex.Exception.Message + "\n\nSee runtime-error.log for full details.",
                    "Runtime Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ex.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                var msg = (ex.ExceptionObject as Exception)?.ToString() ?? ex.ExceptionObject.ToString();
                try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fatal-error.log"), msg); }
                catch { }
                MessageBox.Show($"Fatal error:\n\n{msg}", "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            // Deprioritise this process so it doesn't compete with the user's apps
            EnterLowResourceMode();

            base.OnStartup(e);

            // Build the tray icon programmatically after the WPF pump is running.
            // Creating TaskbarIcon inside XAML resources (before DoStartup completes)
            // causes a TargetInvocationException on .NET 8.
            BuildTrayIcon();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _trayIcon?.Dispose();
            base.OnExit(e);
        }

        // ── Tray icon setup ───────────────────────────────────────────────────

        private void BuildTrayIcon()
        {
            var icon = new BitmapImage(
                new Uri("pack://application:,,,/Resources/icon.ico", UriKind.Absolute));

            var openItem = new MenuItem { Header = "Open BurgerDeleter" };
            openItem.Click += (_, _) => ShowMainWindow();

            var quitItem = new MenuItem { Header = "Quit" };
            quitItem.Click += (_, _) => { AllowClose = true; Current.Shutdown(); };

            var menu = new ContextMenu();
            menu.Items.Add(openItem);
            menu.Items.Add(new Separator());
            menu.Items.Add(quitItem);

            _trayIcon = new TaskbarIcon
            {
                ToolTipText   = "BurgerDeleter",
                IconSource    = icon,
                ContextMenu   = menu,
            };
        }


        // ── Low-resource mode ─────────────────────────────────────────────────

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(
            IntPtr hProcess,
            IntPtr dwMinimumWorkingSetSize,
            IntPtr dwMaximumWorkingSetSize);

        private static void EnterLowResourceMode()
        {
            try
            {
                var proc = Process.GetCurrentProcess();
                // Deprioritise CPU so foreground apps always win
                proc.PriorityClass = ProcessPriorityClass.BelowNormal;
                // Cap working set to ~50 MB; the OS will trim us when memory is tight
                SetProcessWorkingSetSize(proc.Handle, new IntPtr(-1), new IntPtr(50_000_000));
            }
            catch { }

            try
            {
                // Collect garbage in large batches rather than constantly — reduces
                // background GC pauses while the user is doing something else
                GCSettings.LatencyMode = GCLatencyMode.Batch;
            }
            catch { }
        }

        // ─────────────────────────────────────────────────────────────────────

        private static void ShowMainWindow()
        {
            if (Current.MainWindow is not Window win) return;
            win.Show();
            win.WindowState = WindowState.Normal;
            win.Activate();
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
