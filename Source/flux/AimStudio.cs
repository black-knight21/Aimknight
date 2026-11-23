using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KaidenFlux
{
    internal sealed class AimAttempt
    {
        public double AtSeconds, AcquisitionMs, ErrorDip;
        public bool Hit;
        public string MissDirection;
    }
    internal sealed class TrackingSample
    {
        public double AtSeconds, IntervalSeconds;
        public double? DistanceDip;
        public bool OnTarget;
        public bool Observed = true;
    }
    internal sealed class AimSession
    {
        public int Schema = 1;
        public string Id = Guid.NewGuid().ToString("N"), Mode, ProfileId, ProfileName, CurveHash;
        public DateTime CreatedUtc = DateTime.UtcNow;
        public bool Completed;
        public double DurationSeconds, TargetRadiusDip;
        public List<AimAttempt> Attempts = new List<AimAttempt>();
        public List<TrackingSample> Tracking = new List<TrackingSample>();
        internal static string Hash(JObject config)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(config.ToString(Formatting.None)))).Replace("-", "");
        }
        internal void Validate()
        {
            Guid guid;
            if (Schema != 1 || !Guid.TryParseExact(Id, "N", out guid) || (Mode != "Flick" && Mode != "Tracking" && Mode != "Micro")) throw new ArgumentException("Unsupported aim session.");
            if (!Finite(DurationSeconds) || !Finite(TargetRadiusDip) || DurationSeconds <= 0 || DurationSeconds > 180 || TargetRadiusDip <= 0 || TargetRadiusDip > 100 || Attempts == null || Tracking == null || Attempts.Count > 10000 || Tracking.Count > 20000) throw new ArgumentException("Invalid session measurements.");
            if (Completed && Math.Abs(DurationSeconds - 30) > 0.1) throw new ArgumentException("A completed standard test must contain 30 seconds of measurements.");
            if (string.IsNullOrEmpty(CurveHash) || CurveHash.Length != 64 || !CurveHash.All(c => Uri.IsHexDigit(c))) throw new ArgumentException("Missing curve association.");
            foreach (var a in Attempts) if (a == null || !Finite(a.AtSeconds) || !Finite(a.AcquisitionMs) || !Finite(a.ErrorDip) || a.AtSeconds < 0 || a.AtSeconds > DurationSeconds + 0.1 || a.AcquisitionMs < 0 || a.ErrorDip < 0) throw new ArgumentException("Invalid click measurement.");
            foreach (var a in Attempts) if (a.Hit != (a.ErrorDip <= TargetRadiusDip)) throw new ArgumentException("Click accuracy does not match its measured distance.");
            foreach (var t in Tracking) if (t == null || !Finite(t.AtSeconds) || !Finite(t.IntervalSeconds) || t.IntervalSeconds < 0 || t.AtSeconds < 0 || t.AtSeconds > DurationSeconds + 0.1 || (t.DistanceDip.HasValue && (!Finite(t.DistanceDip.Value) || t.DistanceDip < 0))) throw new ArgumentException("Invalid tracking measurement.");
            if (Tracking.Sum(t => t.IntervalSeconds) > DurationSeconds + 0.1 || Tracking.Any(t => t.OnTarget && (!t.Observed || !t.DistanceDip.HasValue || t.DistanceDip > TargetRadiusDip))) throw new ArgumentException("Tracking timing or accuracy does not match its measurements.");
        }
        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        internal JObject Summary()
        {
            var hits = Attempts.Where(a => a.Hit).Select(a => a.AcquisitionMs).ToArray();
            double sampleTime = Tracking.Where(t => t.Observed).Sum(t => t.IntervalSeconds), measuredTime = Tracking.Where(t => t.Observed && t.DistanceDip.HasValue).Sum(t => t.IntervalSeconds);
            double? deviation = measuredTime > 0 ? (double?)(Tracking.Where(t => t.Observed && t.DistanceDip.HasValue).Sum(t => t.DistanceDip.Value * t.IntervalSeconds) / measuredTime) : null;
            double? stability = deviation.HasValue ? (double?)Math.Sqrt(Tracking.Where(t => t.Observed && t.DistanceDip.HasValue).Sum(t => Math.Pow(t.DistanceDip.Value - deviation.Value, 2) * t.IntervalSeconds) / measuredTime) : null;
            return JObject.FromObject(new { mode = Mode, completed = Completed, durationSeconds = DurationSeconds, targetRadiusDip = TargetRadiusDip,
                clicks = Attempts.Count, hits = hits.Length, accuracyPercent = Attempts.Count > 0 ? (double?)(100.0 * hits.Length / Attempts.Count) : null,
                missRatePercent = Attempts.Count > 0 ? (double?)(100.0 * (Attempts.Count - hits.Length) / Attempts.Count) : null,
                meanAcquisitionMs = hits.Length > 0 ? (double?)hits.Average() : null,
                acquisitionStdDevMs = hits.Length > 0 ? (double?)Math.Sqrt(hits.Average(v => Math.Pow(v - hits.Average(), 2))) : null,
                endpointOvershoots = Attempts.Count(a => !a.Hit && a.MissDirection == "Overshoot"), endpointUndershoots = Attempts.Count(a => !a.Hit && a.MissDirection == "Undershoot"),
                trackingOnTargetPercent = sampleTime > 0 ? (double?)(100 * Tracking.Where(t => t.Observed && t.OnTarget).Sum(t => t.IntervalSeconds) / sampleTime) : null,
                trackingCoveragePercent = DurationSeconds > 0 ? sampleTime / DurationSeconds * 100 : 0,
                meanDeviationDip = deviation, deviationStdDevDip = stability, trackingMeasuredSeconds = measuredTime,
                input = "Windowed WPF pointer input, coordinates in DIP; not raw-input FPS gameplay" });
        }
    }
    internal sealed class AimSessionStore
    {
        private readonly string folder;
        internal AimSessionStore(string folder) { this.folder = folder; }
        internal void Save(AimSession session) { session.Validate(); AtomicJson.Write(System.IO.Path.Combine(folder, session.Id + ".json"), session); }
        internal List<AimSession> Load()
        {
            var result = new List<AimSession>(); if (!Directory.Exists(folder)) return result;
            string[] files;
            try { files = Directory.GetFiles(folder, "*.json"); } catch (IOException) { return result; } catch (UnauthorizedAccessException) { return result; }
            foreach (var file in files) try {
                if (new FileInfo(file).Length > 2 * 1024 * 1024) continue;
                var value = JsonConvert.DeserializeObject<AimSession>(File.ReadAllText(file)); if (value == null || !string.Equals(System.IO.Path.GetFileNameWithoutExtension(file), value.Id, StringComparison.OrdinalIgnoreCase)) continue; value.Validate(); result.Add(value);
            } catch (Exception e) when (e is IOException || e is JsonException || e is ArgumentException || e is UnauthorizedAccessException) { }
            return result.OrderByDescending(s => s.CreatedUtc).ToList();
        }
    }
    internal sealed class AimStudioView : IDisposable
    {
        internal readonly Grid Root = new Grid();
        internal bool Running => clock.IsRunning;
        private readonly Canvas arena = new Canvas { Background = StudioUi.Ink("#110D1D"), ClipToBounds = true, MinHeight = 160 };
        private readonly Ellipse target = new Ellipse { Fill = StudioUi.Ink("#C5A4FF"), Stroke = StudioUi.Ink("#ECDFFF"), StrokeThickness = 2, IsHitTestVisible = false };
        private readonly TextBlock timer = StudioUi.Text("Choose a mode and start a 30-second session.");
        private readonly TextBlock results = StudioUi.Text("No measurements yet.");
        private readonly TextBlock comparison = StudioUi.Text("Complete comparable sessions to see a before/after comparison.");
        private readonly Canvas timeline = new Canvas { Height = 70, Background = StudioUi.Ink("#130F21"), Margin = new Thickness(0, 6, 0, 8) };
        private readonly ListBox history = new ListBox { Background = StudioUi.Ink("#130F21"), Foreground = StudioUi.Ink("#EDE5FF"), BorderThickness = new Thickness(0), MaxHeight = 155 };
        private readonly Stopwatch clock = new Stopwatch();
        private readonly Random random = new Random();
        private readonly AimSessionStore store;
        private readonly Func<JObject> getActive;
        private readonly Func<GameProfile> getProfile;
        private readonly Action<string> status;
        private readonly bool preview;
        private readonly Window window;
        private AimSession session;
        private string mode = "Flick";
        private Point center, pointer, origin;
        private double spawnedAt, sampledAt;
        private bool inside;
        internal AimStudioView(Window window, AimSessionStore store, Func<JObject> getActive, Func<GameProfile> getProfile, Action<string> status, bool preview)
        {
            this.window = window; this.store = store; this.getActive = getActive; this.getProfile = getProfile; this.status = status; this.preview = preview;
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Root.RowDefinitions.Add(new RowDefinition()); Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(185) });
            var toolbar = new WrapPanel();
            foreach (string value in new[] { "Flick", "Tracking", "Micro" }) { string selected = value; toolbar.Children.Add(StudioUi.Button(window, value, () => { if (Running) throw new InvalidOperationException("Stop the current session before changing modes."); mode = selected; timer.Text = mode + " selected · click Start session."; }, status)); }
            toolbar.Children.Add(StudioUi.Button(window, "Start session", Start, status, true)); toolbar.Children.Add(StudioUi.Button(window, "Stop & save", () => Stop(false), status)); Root.Children.Add(toolbar);
            var play = new DockPanel(); DockPanel.SetDock(timer, Dock.Top); play.Children.Add(timer);
            var note = StudioUi.Text("Windowed pointer test · 30 seconds · WPF DIP coordinates, not raw-input game movement. Miss overshoot/undershoot describes the click endpoint along the target direction only.", 11); DockPanel.SetDock(note, Dock.Bottom); play.Children.Add(note); play.Children.Add(arena);
            var playCard = StudioUi.Card(window, play); Grid.SetRow(playCard, 1); Root.Children.Add(playCard); arena.Children.Add(target); target.Visibility = Visibility.Collapsed;
            var bottom = new Grid(); bottom.ColumnDefinitions.Add(new ColumnDefinition()); bottom.ColumnDefinitions.Add(new ColumnDefinition());
            var report = new StackPanel(); report.Children.Add(results); report.Children.Add(StudioUi.Text("Performance over time · acquisition ms / tracking deviation DIP", 10)); report.Children.Add(timeline); report.Children.Add(comparison);
            var reportScroll = new ScrollViewer { Content = StudioUi.Card(window, report), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 12, 0) }; bottom.Children.Add(reportScroll);
            var historyPanel = new StackPanel(); historyPanel.Children.Add(StudioUi.Text("Local session history", 16)); historyPanel.Children.Add(history);
            var hc = StudioUi.Card(window, historyPanel); Grid.SetColumn(hc, 1); bottom.Children.Add(hc); Grid.SetRow(bottom, 2); Root.Children.Add(bottom);
            history.SelectionChanged += (s, e) => { if (history.SelectedItem is SessionItem item) Show(item.Session); };
            arena.MouseMove += (s, e) => { pointer = e.GetPosition(arena); inside = true; };
            arena.MouseLeave += (s, e) => inside = false;
            arena.MouseLeftButtonDown += Click;
            window.Deactivated += Deactivated; Root.IsVisibleChanged += (s, e) => { if (!Root.IsVisible && Running) Stop(false); };
            LoadHistory();
        }
        private void Deactivated(object sender, EventArgs e) { if (Running) Stop(false); }
        internal void Start()
        {
            if (Running) throw new InvalidOperationException("A session is already running.");
            if (arena.ActualWidth < 120 || arena.ActualHeight < 100) throw new InvalidOperationException("Increase the test area before starting.");
            var active = getActive(); var profile = getProfile();
            session = new AimSession { Mode = mode, ProfileId = profile?.Id, ProfileName = profile?.Name ?? "Default setup", CurveHash = AimSession.Hash(active), TargetRadiusDip = mode == "Micro" ? 5 : 18 };
            pointer = Mouse.GetPosition(arena); inside = arena.IsMouseOver; sampledAt = 0;
            target.Width = target.Height = session.TargetRadiusDip * 2; target.Visibility = Visibility.Visible;
            clock.Restart(); Spawn(); CompositionTarget.Rendering += Frame; status("Aim test running. Driver settings are locked until you stop.");
        }
        private void Spawn()
        {
            double margin = session.TargetRadiusDip + 8;
            origin = pointer;
            if (mode == "Micro") center = new Point(Math.Max(margin, Math.Min(arena.ActualWidth - margin, pointer.X + (random.NextDouble() * 2 - 1) * 65)), Math.Max(margin, Math.Min(arena.ActualHeight - margin, pointer.Y + (random.NextDouble() * 2 - 1) * 65)));
            else center = new Point(margin + random.NextDouble() * (arena.ActualWidth - margin * 2), margin + random.NextDouble() * (arena.ActualHeight - margin * 2));
            spawnedAt = clock.Elapsed.TotalSeconds; Position();
        }
        private void Position() { Canvas.SetLeft(target, center.X - session.TargetRadiusDip); Canvas.SetTop(target, center.Y - session.TargetRadiusDip); }
        private void Frame(object sender, EventArgs e)
        {
            if (!Running) return; double now = clock.Elapsed.TotalSeconds;
            if (now >= 30) { Stop(true); return; }
            timer.Text = mode + " · " + (30 - now).ToString("0.0") + "s remaining · " + session.Attempts.Count(a => a.Hit) + " hits";
            if (mode == "Tracking") {
                center = new Point(arena.ActualWidth * (0.5 + 0.37 * Math.Sin(now * 1.1)), arena.ActualHeight * (0.5 + 0.34 * Math.Sin(now * 1.7))); Position();
                if (now - sampledAt >= 0.025) {
                    double distance = (pointer - center).Length;
                    session.Tracking.Add(new TrackingSample { AtSeconds = now, IntervalSeconds = Math.Min(0.1, now - sampledAt), DistanceDip = inside ? (double?)distance : null, OnTarget = inside && distance <= session.TargetRadiusDip }); sampledAt = now;
                }
            }
        }
        private void Click(object sender, MouseButtonEventArgs e)
        {
            if (!Running || mode == "Tracking") return;
            pointer = e.GetPosition(arena); double now = clock.Elapsed.TotalSeconds, error = (pointer - center).Length;
            if (now >= 30) { Stop(true); return; }
            var direction = center - origin; double along = direction.Length > 0 ? Vector.Multiply(pointer - center, direction) / direction.Length : 0;
            bool hit = error <= session.TargetRadiusDip;
            session.Attempts.Add(new AimAttempt { AtSeconds = now, AcquisitionMs = (now - spawnedAt) * 1000, ErrorDip = error, Hit = hit,
                MissDirection = hit ? null : along > session.TargetRadiusDip * 0.25 ? "Overshoot" : along < -session.TargetRadiusDip * 0.25 ? "Undershoot" : "Lateral" });
            if (hit) Spawn();
        }
        internal void Stop(bool completed)
        {
            if (!Running) return;
            clock.Stop(); CompositionTarget.Rendering -= Frame;
            session.DurationSeconds = Math.Min(30, clock.Elapsed.TotalSeconds); session.Completed = completed; target.Visibility = Visibility.Collapsed;
            if (session.DurationSeconds <= 0) return;
            // Account for the final tracking interval without inventing an unseen pointer position.
            if (mode == "Tracking" && session.DurationSeconds > sampledAt) session.Tracking.Add(new TrackingSample { AtSeconds = session.DurationSeconds, IntervalSeconds = session.DurationSeconds - sampledAt, DistanceDip = null, OnTarget = false, Observed = false });
            try { if (!preview) store.Save(session); Show(session); LoadHistory(); status(completed ? "Session complete and saved locally." : "Session stopped; saved as interrupted, excluded from AI comparisons."); }
            catch (Exception ex) { status("Session ended, but saving failed: " + ex.Message); }
            timer.Text = completed ? "Session complete." : "Interrupted session. Start again when ready.";
        }
        private void Show(AimSession value)
        {
            var s = value.Summary();
            timeline.Children.Clear();
            var samples = value.Mode == "Tracking" ? value.Tracking.Where(t => t.DistanceDip.HasValue).Select(t => Tuple.Create(t.AtSeconds, t.DistanceDip.Value)).ToArray() : value.Attempts.Where(a => a.Hit).Select(a => Tuple.Create(a.AtSeconds, a.AcquisitionMs)).ToArray();
            if (samples.Length >= 2) {
                var path = new Polyline { Stroke = StudioUi.Ink("#B18AEF"), StrokeThickness = 2 };
                double max = Math.Max(1, samples.Max(p => p.Item2)), width = Math.Max(100, timeline.ActualWidth);
                foreach (var sample in samples) path.Points.Add(new Point(sample.Item1 / value.DurationSeconds * width, 65 - sample.Item2 / max * 60)); timeline.Children.Add(path);
            } else timeline.Children.Add(StudioUi.Text("Not enough measured samples for a trend.", 11));
            Func<string, string> number = key => s[key].Type == JTokenType.Null ? "Unavailable" : ((double)s[key]).ToString("0.0");
            results.Text = value.Mode + " · " + value.ProfileName + " · " + (value.Completed ? "Complete" : "Interrupted") + "\n" +
                (value.Mode == "Tracking" ? "On target " + number("trackingOnTargetPercent") + "% · deviation " + number("meanDeviationDip") + " DIP · stability SD " + number("deviationStdDevDip") + " DIP\nObserved coverage " + number("trackingCoveragePercent") + "% (unobserved gaps excluded)" :
                "Hits " + (int)s["hits"] + "/" + (int)s["clicks"] + " · accuracy " + number("accuracyPercent") + "%\nAcquisition " + number("meanAcquisitionMs") + " ms · consistency SD " + number("acquisitionStdDevMs") + " ms\nMiss endpoints: " + (int)s["endpointOvershoots"] + " over / " + (int)s["endpointUndershoots"] + " under");
            var previous = !preview && value.Completed ? store.Load().FirstOrDefault(v => v.Id != value.Id && v.Completed && v.Mode == value.Mode && v.ProfileId == value.ProfileId && v.TargetRadiusDip == value.TargetRadiusDip && v.CreatedUtc < value.CreatedUtc && Math.Abs(v.DurationSeconds - value.DurationSeconds) < 0.2) : null;
            if (previous == null) comparison.Text = "No earlier completed session with matching mode, profile, target size and duration.";
            else { var p = previous.Summary(); string metric = value.Mode == "Tracking" ? "trackingOnTargetPercent" : "meanAcquisitionMs";
                comparison.Text = s[metric].Type == JTokenType.Null || p[metric].Type == JTokenType.Null ? "Not enough measured data for comparison." : "Versus " + previous.CreatedUtc.ToLocalTime().ToString("g") + ": " + ((double)s[metric] - (double)p[metric]).ToString("+0.0;-0.0;0.0") + (value.Mode == "Tracking" ? " percentage points on target" : " ms acquisition") + " · " + (previous.CurveHash == value.CurveHash ? "same curve" : "different curve") + ". Practice comparison, not a controlled benchmark."; }
        }
        internal void LoadHistory() { if (!preview) history.ItemsSource = store.Load().Take(100).Select(s => new SessionItem { Session = s }).ToArray(); }
        private sealed class SessionItem { internal AimSession Session; public override string ToString() => Session.CreatedUtc.ToLocalTime().ToString("g") + " · " + Session.Mode + " · " + (Session.Completed ? "Complete" : "Interrupted"); }
        public void Dispose() { if (Running) Stop(false); CompositionTarget.Rendering -= Frame; window.Deactivated -= Deactivated; }
    }
}
