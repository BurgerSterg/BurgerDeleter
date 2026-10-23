using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BurgerDeleter.Services;

namespace BurgerDeleter.Views
{
    public partial class HealthView : Page
    {
        private const int HistoryLength = 60;

        /// <summary>Raised when a recommendation's fix lives on another tab.</summary>
        public event Action<HealthFix>? NavigateRequested;

        private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly CpuUsageSampler _cpu = new();
        private readonly Queue<double> _cpuHistory = new();
        private readonly Queue<double> _ramHistory = new();

        private HealthReport? _report;
        private bool _analyzing;
        private bool _appVisible = true;

        private sealed record CategoryScore(string Name, int Score, Brush Brush);

        public HealthView()
        {
            InitializeComponent();
            _liveTimer.Tick += (_, _) => SampleLive();

            AppEvents.AppHidden  += () => Dispatcher.Invoke(() => { _appVisible = false; _liveTimer.Stop(); });
            AppEvents.AppVisible += () => Dispatcher.Invoke(() =>
            {
                _appVisible = true;
                if (IsLoaded) _liveTimer.Start();
            });

            DrawArc(0);
        }

        // ===== LIFECYCLE =====

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            if (_appVisible) _liveTimer.Start();
            if (_report is null) _ = AnalyzeAsync();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e) => _liveTimer.Stop();

        private void Rescan_Click(object sender, RoutedEventArgs e) => _ = AnalyzeAsync();

        // ===== ANALYSIS =====

        private async Task AnalyzeAsync()
        {
            if (_analyzing) return;
            _analyzing = true;
            RescanBtn.IsEnabled = false;
            StatusText.Text = "SCANNING SYSTEM…";
            GradeText.Text  = "SCANNING";
            if (Resources["ScanPulse"] is Storyboard pulse) pulse.Begin(this, true);

            try
            {
                _report = await SystemHealthService.AnalyzeAsync();
                ShowReport(_report);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Analysis failed: {ex.Message}";
            }
            finally
            {
                if (Resources["ScanPulse"] is Storyboard sb)
                {
                    sb.Stop(this);
                    StatusText.Opacity = 1;
                }
                _analyzing = false;
                RescanBtn.IsEnabled = true;
            }
        }

        private void ShowReport(HealthReport r)
        {
            StatusText.Text = $"LAST SCAN {r.GeneratedAt:HH:mm:ss}  ·  {r.Issues.Count} FINDINGS  ·  {r.Passed.Count} PASSED";
            GradeText.Text  = r.Grade;
            AnimateScore(r.Score);

            CategoryList.ItemsSource = r.CategoryScores
                .Select(kv => new CategoryScore(kv.Key.ToString().ToUpperInvariant(), kv.Value, ScoreBrush(kv.Value)))
                .ToList();

            SpecList.ItemsSource = new List<KeyValuePair<string, string>>
            {
                new("CPU",    r.CpuName),
                new("GPU",    r.GpuName),
                new("RAM",    $"{r.RamTotalGB:F0} GB"),
                new("OS",     r.OsName),
                new("UPTIME", FormatUptime(r.Uptime)),
            };
            DiskList.ItemsSource = r.Disks;

            IssueList.ItemsSource = r.Issues;
            RecoHeader.Text = r.Issues.Count == 0 ? "RECOMMENDATIONS" : $"RECOMMENDATIONS [{r.Issues.Count}]";
            AllClearCard.Visibility = r.Issues.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            PassedList.ItemsSource = r.Passed;
            PassedHeader.Visibility = r.Passed.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string FormatUptime(TimeSpan t)
            => t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{t.Hours}h {t.Minutes}m";

        private static Brush ScoreBrush(int score)
            => (Brush)Application.Current.Resources[
                score >= 80 ? "SuccessBrush" : score >= 55 ? "WarningBrush" : "DangerBrush"];

        // ===== FIXES =====

        private async void Fix_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is not HealthIssue issue) return;

            switch (issue.Fix)
            {
                case HealthFix.OpenScan:
                case HealthFix.OpenStartup:
                case HealthFix.OpenSecurity:
                case HealthFix.OpenMemory:
                case HealthFix.OpenGameOptimizer:
                    NavigateRequested?.Invoke(issue.Fix);
                    return;
            }

            var btn = (Button)sender;
            btn.IsEnabled = false;
            FixResultText.Text = "Working…";
            try
            {
                FixResultText.Text = await SystemHealthService.ApplyFixAsync(issue.Fix);
            }
            catch (Exception ex)
            {
                FixResultText.Text = $"Fix failed: {ex.Message}";
            }
            await AnalyzeAsync();
        }

        // ===== SCORE RING =====

        private void AnimateScore(int target)
        {
            // Count up from zero; a short eased tick keeps it feeling like a HUD boot-up.
            var start = DateTime.Now;
            var duration = TimeSpan.FromMilliseconds(900);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                double t = Math.Min(1, (DateTime.Now - start) / duration);
                double eased = 1 - Math.Pow(1 - t, 3);
                double value = target * eased;
                ScoreText.Text = ((int)Math.Round(value)).ToString();
                DrawArc(value);
                if (t >= 1) timer.Stop();
            };
            timer.Start();

            var glow = ((SolidColorBrush)ScoreBrush(target)).Color;
            ArcGlow.Color = target >= 80 ? (Color)Resources["NeonCyan"] : glow;
        }

        private void DrawArc(double score)
        {
            const double size = 200, stroke = 10, inset = 12;
            double r = (size / 2) - inset - stroke / 2; // centreline of the track ellipse
            var center = new Point(size / 2, size / 2);

            double sweep = Math.Clamp(score, 0, 100) / 100 * 359.99;
            if (sweep <= 0.01) { ScoreArc.Data = null; return; }

            Point PointAt(double deg)
            {
                double rad = (deg - 90) * Math.PI / 180;
                return new Point(center.X + r * Math.Cos(rad), center.Y + r * Math.Sin(rad));
            }

            var fig = new PathFigure { StartPoint = PointAt(0), IsClosed = false };
            fig.Segments.Add(new ArcSegment(PointAt(sweep), new Size(r, r), 0,
                isLargeArc: sweep > 180, SweepDirection.Clockwise, isStroked: true));
            ScoreArc.Data = new PathGeometry(new[] { fig });
        }

        // ===== LIVE TELEMETRY =====

        private void SampleLive()
        {
            double cpu = _cpu.Sample();
            var (totalMB, availMB) = MemoryOptimizerService.GetMemoryStatus();
            double ram = totalMB == 0 ? 0 : (totalMB - availMB) * 100.0 / totalMB;

            Push(_cpuHistory, cpu);
            Push(_ramHistory, ram);

            CpuText.Text = $"{cpu:F0}%";
            RamText.Text = $"{ram:F0}%";
            RamDetailText.Text = $"{(totalMB - availMB) / 1024.0:F1} / {totalMB / 1024.0:F0} GB";

            DrawSparkline(CpuChart, _cpuHistory);
            DrawSparkline(RamChart, _ramHistory);
        }

        private static void Push(Queue<double> q, double v)
        {
            q.Enqueue(v);
            while (q.Count > HistoryLength) q.Dequeue();
        }

        private void Chart_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawSparkline(CpuChart, _cpuHistory);
            DrawSparkline(RamChart, _ramHistory);
        }

        private void DrawSparkline(Canvas canvas, Queue<double> data)
        {
            canvas.Children.Clear();
            double w = canvas.ActualWidth, h = canvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            // Faint grid lines at 25 / 50 / 75 %
            var gridBrush = (Brush)Application.Current.Resources["BorderBrush"];
            foreach (var pct in new[] { 0.25, 0.5, 0.75 })
                canvas.Children.Add(new Line
                {
                    X1 = 0, X2 = w, Y1 = h * pct, Y2 = h * pct,
                    Stroke = gridBrush, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 2, 4 },
                });

            if (data.Count < 2) return;

            double step = w / (HistoryLength - 1);
            double x0 = w - (data.Count - 1) * step;
            var line = new PointCollection();
            int i = 0;
            foreach (var v in data)
                line.Add(new Point(x0 + i++ * step, h - (v / 100 * (h - 2)) - 1));

            var fill = new PointCollection(line) { new Point(line[^1].X, h), new Point(line[0].X, h) };
            var cyan = (Color)Resources["NeonCyan"];

            canvas.Children.Add(new Polygon
            {
                Points = fill,
                Fill = new LinearGradientBrush(Color.FromArgb(90, cyan.R, cyan.G, cyan.B),
                                               Color.FromArgb(0, cyan.R, cyan.G, cyan.B), 90),
            });
            canvas.Children.Add(new Polyline
            {
                Points = line,
                Stroke = (Brush)Resources["NeonGradient"],
                StrokeThickness = 2,
                StrokeLineJoin = PenLineJoin.Round,
            });
        }
    }
}
