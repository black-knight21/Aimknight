using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using grapher.AI;
using Path = System.IO.Path;

namespace KaidenFlux
{
    internal static class Entry
    {
        [STAThread]
        static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            if (args.Length == 1 && args[0] == "--install-driver")
            {
                try { FluxInstaller.Install(); return 0; }
                catch (Exception ex) { MessageBox.Show(FluxState.Friendly(ex.Message), "Kaiden Flux setup", MessageBoxButton.OK, MessageBoxImage.Error); return 1; }
            }
            bool preview = args.Any(a => a.StartsWith("--preview", StringComparison.Ordinal));
            using (var mutex = new Mutex(true, preview ? "KaidenFluxPreview" : "RawAccelGrapher", out bool first))
            {
                if (!first) { MessageBox.Show("Close the other mouse tuning app before opening Kaiden Flux.", "Kaiden Flux"); return 0; }
                var app = new Application();
                app.DispatcherUnhandledException += (s, e) => { MessageBox.Show(FluxState.Friendly(e.Exception.Message), "Kaiden Flux"); e.Handled = true; };
                var shell = new FluxShell(preview);
                shell.Window.Loaded += async (s, e) => {
                    await shell.StartAsync();
                    if (preview)
                    {
                        var step = args.FirstOrDefault(a => a.StartsWith("--preview-setup="));
                        if (step != null) shell.PreviewWizard(int.Parse(step.Split('=')[1]));
                        if (args.Contains("--preview-ai")) shell.PreviewAI();
                        if (args.Contains("--preview-library")) shell.Navigate("Library");
                        if (args.Contains("--preview-profiles")) shell.Navigate("Profiles");
                        if (args.Contains("--preview-calculator")) shell.Navigate("Calculator");
                        if (args.Contains("--preview-editor")) shell.Navigate("Editor");
                        if (args.Contains("--preview-aim")) shell.Navigate("Aim");
                        int renderIndex = Array.IndexOf(args, "--render");
                        if (renderIndex >= 0 && renderIndex + 1 < args.Length)
                        {
                            await shell.Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            shell.Render(args[renderIndex + 1]);
                            shell.Window.Close();
                        }
                    }
                };
                app.Run(shell.Window);
                GC.KeepAlive(mutex);
            }
            return 0;
        }
    }

    internal sealed partial class FluxShell
    {
        internal readonly Window Window;
        private readonly bool preview;
        private FluxState state, draft;
        private JObject live, pending, expected;
        private bool ready, busy, syncing;
        private int wizardStep;
        private TextBox wizardInput;
        private string choice;
        private double[] curveValues;
        private double curvePlotMax = 1.5;
        private Line hoverLine;
        private System.Windows.Shapes.Ellipse hoverDot;
        private readonly System.Collections.Generic.List<string> recentRequests = new System.Collections.Generic.List<string>();
        private readonly WriterBridge writer = new WriterBridge();
        private readonly CancellationTokenSource cancel = new CancellationTokenSource();
        private CancellationTokenSource generationCancel;
        private string lastRequest;
        private const string XArgs = "Whole or horizontal accel parameters";
        private const string YArgs = "Vertical accel parameters";
        private const string DpiKey = "DPI (normalizes input speed unit: counts/ms -> in/s)";

        private T C<T>(string name) where T : FrameworkElement => (T)Window.FindName(name);
        private void Text(string name, string text) => C<TextBlock>(name).Text = text;
        private static Brush Brush(string color) => (Brush)new BrushConverter().ConvertFromString(color);
        private void Button(string name, Action action) => C<Button>(name).Click += (s, e) => action();
        private void AsyncButton(string name, Func<Task> action) => C<Button>(name).Click += async (s, e) => await RunAsync(action);

        internal FluxShell(bool preview)
        {
            this.preview = preview;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("KaidenFlux.Shell.xaml"))
                Window = (Window)XamlReader.Load(stream);
            state = preview ? new FluxState { Completed = true, Starter = "Flick boost" } : FluxState.Load();
            Window.SourceInitialized += (s, e) => { Window.MaxHeight = SystemParameters.WorkArea.Height; };
            Window.Closing += (s, e) => { if (busy) { e.Cancel = true; Status("Wait for the current operation to finish before closing."); } else cancel.Cancel(); };
            Window.Closed += (s, e) => { C<PasswordBox>("ApiKey").Clear(); cancel.Dispose(); };
            C<Grid>("TitleBar").MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) ToggleMaximize(); else Window.DragMove(); };
            Button("CloseApp", () => Window.Close());
            Button("Minimize", () => Window.WindowState = WindowState.Minimized);
            Button("Maximize", ToggleMaximize);
            Button("NavOverview", () => Navigate("Overview"));
            Button("NavAI", () => Navigate("AI"));
            Button("NavLibrary", () => { LoadLibrary(); Navigate("Library"); });
            Button("NavMouse", () => OpenWizard());
            Button("OpenAI", () => Navigate("AI"));
            Button("GetKey", () => Process.Start(new ProcessStartInfo("https://aistudio.google.com/apikey") { UseShellExecute = true }));
            Button("Licenses", () => Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LICENSES.txt")) { UseShellExecute = true }));
            foreach (string name in new[] { "SensSlider", "AccelSlider", "OffsetSlider", "CapSlider" })
                C<Slider>(name).ValueChanged += (s, e) => { if (!syncing) ManualPreview(); };
            C<Canvas>("CurveCanvas").SizeChanged += (s, e) => DrawCurve();
            C<Canvas>("CurveCanvas").MouseMove += (s, e) => {
                var canvas = C<Canvas>("CurveCanvas");
                double speed = Math.Max(0, Math.Min(60, (e.GetPosition(canvas).X - 32) / Math.Max(1, canvas.ActualWidth - 48) * 60));
                if (curveValues == null) return;
                int index = Math.Min(120, (int)Math.Round(speed * 2));
                double max = curvePlotMax;
                double x = 32 + (canvas.ActualWidth - 48) * index / 120;
                double y = 14 + (canvas.ActualHeight - 40) * (1 - curveValues[index] / max);
                if (hoverLine != null) { hoverLine.X1 = hoverLine.X2 = x; hoverLine.Visibility = Visibility.Visible; }
                if (hoverDot != null) { Canvas.SetLeft(hoverDot, x - 4); Canvas.SetTop(hoverDot, y - 4); hoverDot.Visibility = Visibility.Visible; }
                canvas.ToolTip = speed.ToString("0.0") + (InputDpi() > 0 ? " in/s" : " counts/ms") + "  ·  " + curveValues[index].ToString("0.00") + "× output";
            };
            C<Canvas>("CurveCanvas").MouseLeave += (s, e) => { if (hoverLine != null) hoverLine.Visibility = Visibility.Collapsed; if (hoverDot != null) hoverDot.Visibility = Visibility.Collapsed; };
            AsyncButton("ApplyCurve", ApplyPendingAsync);
            Button("DiscardCurve", () => { pending = null; expected = null; C<Button>("ApplyAI").IsEnabled = false; Refresh(); Status("Preview discarded."); });
            AsyncButton("QuickUndo", UndoAsync);
            Button("QuickSave", SaveCurve);
            Button("SaveLibrary", SaveCurve);
            Button("PresetQuiet", () => Preset("Quiet aim"));
            Button("PresetBalanced", () => Preset("Balanced"));
            Button("PresetFlick", () => Preset("Flick boost"));
            Button("SuggestSteady", () => C<TextBox>("ChatInput").Text = "Keep slow aiming steady. Make a small, controlled adjustment.");
            Button("SuggestFlick", () => C<TextBox>("ChatInput").Text = "Keep my slow aim unchanged, but boost fast flicks slightly.");
            Button("SuggestGentle", () => C<TextBox>("ChatInput").Text = "Make the acceleration gentler with a lower output cap.");
            AsyncButton("SendAI", SendAsync);
            AsyncButton("RetryAI", async () => { C<TextBox>("ChatInput").Text = lastRequest ?? ""; await SendAsync(); });
            Button("CancelAI", () => { generationCancel?.Cancel(); Status("Canceling the AI request…"); });
            Button("FastModel", () => {
                if (busy) return;
                C<TextBox>("ModelId").Text = "gemini-3.5-flash-lite";
                Status("Fast model selected. Generate a proposal or retry your request.");
            });
            AsyncButton("ApplyAI", ApplyPendingAsync);
            Button("WizardBack", () => { if (wizardStep > 0) { wizardStep--; WizardPage(); } });
            AsyncButton("WizardNext", NextWizardAsync);
            Button("WizardClose", () => { if (state.Completed) C<Border>("WizardOverlay").Visibility = Visibility.Collapsed; else Window.Close(); });
            C<PasswordBox>("ApiKey").Password = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? "";
            C<TextBox>("ModelId").Text = Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-3.5-flash-lite";
            Message("FLUX", "Tell me what feels off: slow aim, flick speed, or how the curve ramps up. I’ll propose a change for you to review.", false);
        }

        internal async Task StartAsync()
        {
            if (preview)
            {
                live = JObject.Parse(DriverConfig.GetDefault().ToJSON());
                live = state.Configure(live);
                Text("DriverBadge", "●  Design preview");
            }
            else
            {
                try { VersionHelper.ValidOrThrow(); live = JObject.Parse(DriverConfig.GetActive().ToJSON()); ready = true; }
                catch (InteropException) { live = JObject.Parse(DriverConfig.GetDefault().ToJSON()); ready = false; }
                if (state.Completed && state.PendingApply && ready)
                    await RunAsync(async () => {
                        var next = state.Configure(live);
                        await writer.ApplyAsync(live, next, true);
                        live = JObject.Parse(DriverConfig.GetActive().ToJSON());
                        state.PendingApply = false; state.Save();
                    });
                Text("DriverBadge", ready ? "●  Driver connected" : state.DriverInstalled ? "●  Restart required" : "●  Driver setup required");
            }
            Refresh();
            InitializeFoundation();
            InitializeStudios();
            InitializeNavigation();
            InitializeMemory();
            InitializeKeyStorage();
            if(state.LoadWarning!=null)Status(state.LoadWarning);
            if (!state.Completed || (!ready && !preview)) OpenWizard();
        }
        private void ToggleMaximize() => Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Status(string text) { Text("Status", FluxState.Friendly(text)); }
        internal void Navigate(string page)
        {
            if(page=="Hardware") page="Overview";
            SyncNavigation(page);
            if (page == "Editor" || page == "Aim") { ShowStudio(page); return; }
            HideStudios(page);
            if (page == "Profiles" || page == "Calculator") { ShowFoundation(page); return; }
            HideFoundation();
            if (page == "Library") LoadLibrary();
            C<Grid>("Overview").Visibility = page == "Overview" ? Visibility.Visible : Visibility.Collapsed;
            C<Grid>("AIPage").Visibility = page == "AI" ? Visibility.Visible : Visibility.Collapsed;
            C<ScrollViewer>("LibraryPage").Visibility = page == "Library" ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in new[] { "Overview", "AI", "Library" }) C<Button>("Nav" + item).Background = Brush(item == page ? "#37303F" : "#00000000");
            Text("PageTitle", page == "AI" ? "AI assistant" : page == "Library" ? "Saved curves" : "Dashboard");
            Text("PageSubtitle", page == "AI" ? "Generate a proposal, review the parameter changes, then apply." : page == "Library" ? "Preview a starting point, or return to something you love." : "Current settings and pending changes.");
            C<Grid>(page == "Overview" ? "Overview" : "AIPage").BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.4, 1, TimeSpan.FromMilliseconds(220)));
            RefreshProfileSummary();
        }
        private async Task RunAsync(Func<Task> action)
        {
            if (busy) return;
            busy = true;
            if (curveEditor != null) curveEditor.Root.IsEnabled = false;
            foreach (string name in new[] { "ApplyCurve", "ApplyAI", "SendAI", "RetryAI", "FastModel", "QuickUndo", "WizardNext", "WizardBack", "WizardClose", "NavMouse", "PresetQuiet", "PresetBalanced", "PresetFlick", "DiscardCurve" }) C<Button>(name).IsEnabled = false;
            foreach (string name in new[] { "SensSlider", "AccelSlider", "OffsetSlider", "CapSlider" }) C<Slider>(name).IsEnabled = false;
            try { await action(); }
            catch (Exception ex) { Status(ex.Message); if (C<Border>("WizardOverlay").Visibility == Visibility.Visible) Text("WizardError", FluxState.Friendly(ex.Message)); else Message("FLUX", FluxState.Friendly(ex.Message), false); }
            finally {
                busy = false;
                if (curveEditor != null) curveEditor.Root.IsEnabled = true;
                foreach (string name in new[] { "ApplyCurve", "SendAI", "RetryAI", "FastModel", "QuickUndo", "WizardNext", "WizardBack", "WizardClose", "NavMouse", "PresetQuiet", "PresetBalanced", "PresetFlick", "DiscardCurve" }) C<Button>(name).IsEnabled = true;
                C<Button>("ApplyAI").IsEnabled = pending != null;
                RefreshSliders();
                if (live != null) Refresh();
            }
        }
        private double InputDpi() => (double)(pending ?? live)["defaultDeviceConfig"][DpiKey];
        private double ScaleDpi() => InputDpi() > 0 ? InputDpi() : 1000;
        private void Refresh()
        {
            if (live == null) return;
            Text("DpiMetric", InputDpi() > 0 ? InputDpi().ToString("N0") : "Not set");
            int polling = (int)live["defaultDeviceConfig"]["Polling rate Hz (keep at 0 for automatic adjustment)"];
            Text("PollMetric", polling == 0 ? "Automatic" : polling.ToString("N0") + " Hz");
            string mode = (string)(pending ?? live)["profiles"][0][XArgs]["mode"];
            Text("CurveMetric", CurveHistory.ModeName((pending ?? live)["profiles"][0][XArgs]));
            Text("SidebarMouse", state.Mouse);
            Text("SidebarInfo", state.Dpi.ToString("N0") + " DPI · " + state.Style.ToLowerInvariant() + " aim\n" + state.PadCm.ToString("0.#") + " cm pad");
            Text("PreviewTag", pending != null ? "UNAPPLIED PREVIEW" : preview ? "DEMO · NOT APPLIED" : ready ? "ACTIVE" : "OFFLINE");
            C<Button>("ApplyCurve").IsEnabled = pending != null && ready && !preview && !busy;
            C<Button>("DiscardCurve").IsEnabled = pending != null && !busy;
            if (homePreviewDetails != null) homePreviewDetails.Text = pending == null
                ? preview ? "Design preview only. No driver settings are being read or applied." : ready ? "No changes pending. The graph shows current driver settings." : "Offline. Showing locally available settings, not verified driver state."
                : "Unapplied curve preview. Dashed line = current driver settings; purple = proposed curve. Review before applying.";
            RefreshSliders(); DrawCurve();
            if (!ready && !preview) Status("Complete setup and restart your PC to enable live tuning.");
            else if (live["devices"] is JArray devices && devices.Count > 0) Status("Editing the default profile. Existing device-specific assignments are preserved.");
        }
        private void RefreshSliders()
        {
            if (live == null) return;
            syncing = true;
            var profile = (pending ?? live)["profiles"][0]; var args = profile[XArgs];
            C<Slider>("SensSlider").Value = (double)profile["Output DPI"] / ScaleDpi();
            C<Slider>("AccelSlider").Value = (double)args["acceleration"];
            C<Slider>("OffsetSlider").Value = (double)args["inputOffset"];
            C<Slider>("CapSlider").Value = (double)args["Cap / Jump"]["y"];
            Text("SensValue", ((double)profile["Output DPI"] / ScaleDpi()).ToString("0.00") + "×");
            Text("AccelValue", ((double)args["acceleration"]).ToString("0.00"));
            Text("OffsetValue", ((double)args["inputOffset"]).ToString("0.0"));
            Text("CapValue", ((double)args["Cap / Jump"]["y"]).ToString("0.00") + "×");
            bool classic = (string)args["mode"] == "classic" && (string)args["Cap mode"] == "output";
            C<Slider>("SensSlider").IsEnabled = !busy;
            foreach (string name in new[] { "AccelSlider", "OffsetSlider", "CapSlider" }) C<Slider>(name).IsEnabled = classic && !busy;
            syncing = false;
        }
        private void ManualPreview()
        { pendingAiRequest=null;
            if (live == null || busy) return;
            if (pending == null) { pending = (JObject)live.DeepClone(); expected = (JObject)live.DeepClone(); }
            var profile = pending["profiles"][0];
            profile["Output DPI"] = C<Slider>("SensSlider").Value * ScaleDpi();
            // Sliders operate on classic curves. Preserve other modes until a preset is chosen.
            if ((string)profile[XArgs]["mode"] == "classic" && (string)profile[XArgs]["Cap mode"] == "output")
                foreach (string axis in new[] { XArgs, YArgs })
                {
                    profile[axis]["acceleration"] = C<Slider>("AccelSlider").Value;
                    profile[axis]["inputOffset"] = C<Slider>("OffsetSlider").Value;
                    profile[axis]["Cap / Jump"]["y"] = C<Slider>("CapSlider").Value;
                }
            C<Button>("ApplyAI").IsEnabled = false;
            Refresh(); Status("Preview only. Apply curve when it feels right.");
        }
        private void Preset(string name)
        { pendingAiRequest=null;
            if (busy) return;
            expected = (JObject)live.DeepClone(); pending = (JObject)live.DeepClone();
            FluxState.ApplyPreset(pending, name);
            FluxState.ValidateConfig(pending);
            C<Button>("ApplyAI").IsEnabled = false;
            Navigate("Overview"); Refresh(); Status(name + " loaded into preview. Apply curve to use it.");
        }
        private async Task ApplyPendingAsync()
        {
            if (aimStudio != null && aimStudio.Running) throw new InvalidOperationException("Stop the aim test before applying a different curve.");
            if (pending == null) { Status("Adjust a slider or preview a curve first."); return; }
            if (!ready || preview) throw new InvalidOperationException(preview ? "Design preview: driver writes are disabled." : "Restart your PC after driver setup, then reopen Flux.");
            FluxState.ValidateConfig(pending);
            Status("Applying your curve…");
            string appliedDiff=TuningProposal.Diff(expected,pending);
            await writer.ApplyAsync(expected, pending, true);
            live = JObject.Parse(DriverConfig.GetActive().ToJSON()); pending = expected = null;
            Remember("applied",pendingAiRequest??"","Configuration applied successfully.",appliedDiff);pendingAiRequest=null;
            Text("ProposalSummary", "Applied. Your previous settings are available from Undo last apply."); Text("ProposalDiff", "");
            Refresh(); Status("Curve applied. Your settings are active.");
        }
        private async Task UndoAsync()
        {
            if (aimStudio != null && aimStudio.Running) throw new InvalidOperationException("Stop the aim test before restoring driver settings.");
            if (!ready || preview) throw new InvalidOperationException("A connected driver is required to restore settings.");
            if (!File.Exists(writer.BackupPath)) throw new InvalidOperationException("There is no previous apply to undo yet.");
            var current = JObject.Parse(DriverConfig.GetActive().ToJSON());
            await writer.ApplyAsync(current, TuningProposal.Parse(File.ReadAllText(writer.BackupPath)), false);
            live = JObject.Parse(DriverConfig.GetActive().ToJSON()); pending = expected = null; Refresh(); Status("Previous settings restored.");
        }
        private void DrawCurve()
        {
            if (live == null) return;
            var canvas = C<Canvas>("CurveCanvas"); canvas.Children.Clear();
            double width = canvas.ActualWidth, height = canvas.ActualHeight;
            if (width < 80 || height < 60) return;
            try
            {
                var config = FluxState.ValidateConfig(pending ?? live);
                var values = SampleCurve(config, InputDpi());
                double[] activeValues = pending == null ? null : SampleCurve(FluxState.ValidateConfig(live), (double)live["defaultDeviceConfig"][DpiKey]);
                double max = Math.Max(1.5, Math.Max(values.Max(), activeValues == null ? 0 : activeValues.Max()) * 1.15);
                curvePlotMax = max;
                double left = 32, top = 14, w = width - 48, h = height - 40;
                for (int i = 0; i <= 4; i++)
                {
                    double y = top + h * i / 4;
                    canvas.Children.Add(new Line { X1 = left, X2 = width - 12, Y1 = y, Y2 = y, Stroke = Brush("#33263F"), StrokeThickness = 1 });
                    var label = new TextBlock { Text = (max * (1 - i / 4.0)).ToString("0.0") + "×", FontSize = 10, Foreground = Brush("#82718F") };
                    Canvas.SetLeft(label, 0); Canvas.SetTop(label, y - 6); canvas.Children.Add(label);
                }
                for (int i = 0; i <= 3; i++)
                {
                    double x = left + w * i / 3;
                    canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = top, Y2 = top + h, Stroke = Brush("#231B30"), StrokeThickness = 1 });
                    var label = new TextBlock { Text = (i * 20).ToString(), FontSize = 10, Foreground = Brush("#82718F") };
                    Canvas.SetLeft(label, x - 5); Canvas.SetTop(label, top + h + 9); canvas.Children.Add(label);
                }
                var line = new Polyline { Stroke = Brush("#BB96FF"), StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round };
                for (int i = 0; i < values.Length; i++) line.Points.Add(new Point(left + w * i / 120, top + h * (1 - values[i] / max)));
                var area = new Polygon { Fill = new LinearGradientBrush(Color.FromArgb(65, 167, 109, 255), Color.FromArgb(0, 167, 109, 255), 90) };
                area.Points.Add(new Point(left, top + h)); foreach (var point in line.Points) area.Points.Add(point); area.Points.Add(new Point(left + w, top + h));
                canvas.Children.Add(area);
                if (activeValues != null)
                {
                    var activeLine = new Polyline { Stroke = Brush("#85768F"), StrokeThickness = 2, StrokeDashArray = new DoubleCollection(new[] { 5.0, 4.0 }), IsHitTestVisible = false };
                    for (int i = 0; i < activeValues.Length; i++) activeLine.Points.Add(new Point(left + w * i / 120, top + h * (1 - activeValues[i] / max)));
                    canvas.Children.Add(activeLine);
                }
                canvas.Children.Add(line);
                curveValues = values;
                hoverLine = new Line { Y1 = top, Y2 = top + h, Stroke = Brush("#9576BC"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection(new[] { 3.0, 4.0 }), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
                hoverDot = new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = Brush("#EDDDFF"), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
                canvas.Children.Add(hoverLine); canvas.Children.Add(hoverDot);
                Text("CurveAxis", InputDpi() > 0 ? "Input speed · in/s →" : "Input speed · counts/ms →");
            }
            catch (Exception ex) { Status("Curve preview unavailable: " + ex.Message); }
        }

        // Both active and proposed lines use identical native-engine sampling and axes.
        private double[] SampleCurve(DriverConfig config, double inputDpi)
        {
            var values = new double[121];
            double factor = inputDpi > 0 ? 1000 / inputDpi : 1;
            using (var accel = config.accels[0].CreateStatelessCopy())
                for (int i = 0; i < values.Length; i++)
                {
                    double speed = Math.Max(0.05, i * 0.5);
                    values[i] = accel.Accelerate(100, 0, factor, 100 * factor / speed).Item1 / 100;
                    if (double.IsNaN(values[i]) || double.IsInfinity(values[i]))
                        throw new InvalidOperationException("Curve contains invalid output.");
                }
            return values;
        }

        private void Message(string sender, string text, bool user)
        {
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = sender, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brush(user ? "#D5C1F4" : "#B390EB"), Margin = new Thickness(0, 0, 0, 8) });
            content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brush("#D6C9E5"), LineHeight = 22 });
            C<StackPanel>("Messages").Children.Add(new Border { Child = content, Background = Brush(user ? "#302040" : "#20172F"), CornerRadius = new CornerRadius(14), Padding = new Thickness(18), Margin = new Thickness(user ? 25 : 0, 0, user ? 0 : 25, 14) });
            C<ScrollViewer>("ChatScroll").ScrollToEnd();
        }
        private async Task SendAsync()
        {
            string request = C<TextBox>("ChatInput").Text.Trim();
            if (request.Length == 0) throw new ArgumentException("Describe what you want to change first.");
            if (!ready || preview) throw new InvalidOperationException(preview ? "Design preview: API requests are disabled." : "Complete driver setup and restart before generating a live tuning proposal.");
            live = JObject.Parse(DriverConfig.GetActive().ToJSON()); pending = null; pendingAiRequest=null; expected = (JObject)live.DeepClone();
            lastRequest = request;
            C<Button>("RetryAI").Visibility = Visibility.Collapsed;
            C<Button>("CancelAI").Visibility = Visibility.Visible;
            generationCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token);
            Message("YOU", request, true); C<TextBox>("ChatInput").Clear();
            Remember("request",request,"","");
            recentRequests.Add(request);
            if (recentRequests.Count > 5) recentRequests.RemoveAt(0);
            Text("ProposalSummary", "Thinking about your setup…"); Text("ProposalDiff", ""); Status("Generating a tuning proposal…");
            string phase = "Waiting for Gemini";
            var elapsed = Stopwatch.StartNew();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => { string waiting = phase + " · " + (int)elapsed.Elapsed.TotalSeconds + "s"; Text("ProposalSummary", waiting); Status(waiting); };
            timer.Start();
            try
            {
                var context = state.Context(); context["recentRequests"] = localMemory?.Enabled==true?new JArray(recentRequests):new JArray();
                AddGameContext(context);
                AddAimContext(context);
                context["localMemory"]=localMemory?.Context()??new JArray();
                var tuner = new GeminiTuner();
                pending = await tuner.ProposeAsync(C<PasswordBox>("ApiKey").Password, C<TextBox>("ModelId").Text.Trim(), request, expected, generationCancel.Token, context,
                    new Progress<string>(update => phase = update));
                if(!string.IsNullOrWhiteSpace(tuner.LastAdvice))Message("COACHING NOTES",tuner.LastAdvice,false);
                string diff = TuningProposal.Diff(expected, pending);
                pendingAiRequest=diff.Length>0?request:null;
                Remember("proposal",request,tuner.LastExplanation+"\n"+tuner.LastAdvice,diff);
                if (diff.Length == 0) { pending = null; Message("FLUX", string.IsNullOrWhiteSpace(tuner.LastExplanation) ? "No parameter changes were proposed." : tuner.LastExplanation, false); Text("ProposalSummary", "No changes proposed."); }
                else {
                    Message("FLUX", string.IsNullOrWhiteSpace(tuner.LastExplanation) ? "Your proposal is ready. Review the exact changes and compare the curves before applying." : tuner.LastExplanation, false);
                    Text("ProposalSummary", "Validated. Applying will update the default curve.");
                    Text("ProposalDiff", HumanDiff(diff));
                }
                Refresh(); Status("Proposal ready for review.");
            }
            catch (OperationCanceledException ex)
            {
                RestoreRequest(request);
                Text("ProposalSummary", "Request canceled. Your driver settings are unchanged.");
                throw new OperationCanceledException("AI generation canceled. Your request is still in the box; retry when you're ready. Your driver settings are unchanged.", ex);
            }
            catch { RestoreRequest(request); Text("ProposalSummary", "No proposal generated. Your driver settings are unchanged."); throw; }
            finally {
                timer.Stop(); elapsed.Stop();
                C<Button>("CancelAI").Visibility = Visibility.Collapsed;
                generationCancel.Dispose(); generationCancel = null;
            }
        }
        private void RestoreRequest(string request)
        {
            if (string.IsNullOrWhiteSpace(C<TextBox>("ChatInput").Text)) C<TextBox>("ChatInput").Text = request;
            C<Button>("RetryAI").Visibility = Visibility.Visible;
        }
        private static string HumanDiff(string diff) => diff.Replace("/profiles/0/", "").Replace(XArgs + "/", "Horizontal · ").Replace(YArgs + "/", "Vertical · ").Replace("/", " · ").Replace(" -> ", " → ");
        private void SaveCurve()
        {
            if (preview) { Status("Saving is disabled in design preview."); return; }
            var curves = Path.Combine(FluxState.Folder, "curves"); Directory.CreateDirectory(curves);
            AtomicJson.Write(Path.Combine(curves, DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 4) + ".json"), pending ?? live);
            LoadLibrary(); Status(pending != null ? "Preview saved to your curve library." : "Active curve saved to your library.");
        }
        private void LoadLibrary()
        {
            var panel = C<StackPanel>("SavedCurves"); panel.Children.Clear();
            var folder = Path.Combine(FluxState.Folder, "curves");
            string[] files = !preview && Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json").OrderByDescending(f => f).ToArray() : new string[0];
            if (files.Length == 0) panel.Children.Add(new TextBlock { Text = "No saved curves yet. Save your first one from Overview.", Foreground = Brush("#A998BC"), Margin = new Thickness(0, 10, 0, 10) });
            foreach (var file in files)
            {
                string selected = file;
                var button = new Button { Content = "Preview  ·  " + Path.GetFileNameWithoutExtension(file), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 10) };
                button.Click += (s, e) => {
                    if (busy) return;
                    try {
                        var saved = TuningProposal.Parse(File.ReadAllText(selected));
                        // Saved curves restore profile data only; current mouse/device settings stay intact.
                        pending = TuningProposal.LoadCurve(live, saved); expected = (JObject)live.DeepClone();
                        Navigate("Overview"); Refresh(); Status("Saved curve loaded into preview.");
                    } catch (Exception ex) { Status(ex.Message); }
                }; panel.Children.Add(button);
            }
        }

        private void OpenWizard()
        {
            if (aimStudio != null && aimStudio.Running) { Status("Stop the aim test before changing mouse setup."); return; }
            if (busy) return;
            draft = JsonConvert.DeserializeObject<FluxState>(JsonConvert.SerializeObject(state));
            if (state.Completed) draft.Starter = "Keep current";
            wizardStep = 0; C<Border>("WizardOverlay").Visibility = Visibility.Visible; WizardPage();
        }
        private void WizardPage()
        {
            var fields = C<StackPanel>("WizardFields"); fields.Children.Clear(); wizardInput = null;
            Text("WizardError", ""); Text("WizardCount", (wizardStep + 1).ToString("00") + " / 09");
            C<Button>("WizardBack").Visibility = wizardStep == 0 ? Visibility.Hidden : Visibility.Visible;
            C<Button>("WizardNext").Content = wizardStep == 8 ? (ready ? "Save & apply setup  →" : "Save for after restart  →") : "Continue  →";
            string[] titles = { "Let's get connected.", "What's your DPI?", "What's your polling rate?", "Meet your mouse.", "Your in-game sensitivity?", "How much room to move?", "How do you aim?", "Choose your starting point.", "Your setup. Ready to flow." };
            string[] descriptions = {
                ready ? "Your mouse driver is connected. Let's personalize the rest." : draft.DriverInstalled ? "Your driver is installed. Restart your PC to activate it. You can finish personalizing your setup now." : "The mouse driver is included. Install it here, then restart your PC after setup. No separate download needed.",
                "Enter the DPI set in your mouse software. Windows doesn't reliably report hardware DPI. You only need to enter it once.",
                "Choose the rate set in your mouse software, or use Automatic for adaptive timing.",
                "Give your mouse a name. This is a local label for your setup.",
                "This helps the AI understand your aiming style. It won't change your game's sensitivity setting.",
                "Enter your mouse pad's usable width in centimeters.",
                "Choose the style that feels most like you. This helps personalize AI suggestions.",
                "Keep your existing curve, or start with a classic acceleration preset. You'll be able to fine tune it afterward.",
                ready ? "We'll save your preferences and apply your mouse setup. Game sensitivity and pad size are context for the AI." : "We'll save your preferences now. After you restart and reopen Flux, your mouse setup will be applied automatically."
            };
            Text("WizardTitle", titles[wizardStep]); Text("WizardDescription", descriptions[wizardStep]); Text("WizardEyebrow", wizardStep == 0 ? "WELCOME TO KAIDEN FLUX" : wizardStep == 8 ? "MADE FOR YOU" : "PERSONALIZE YOUR FLOW");
            if (wizardStep == 0)
            {
                if (!ready && !draft.DriverInstalled)
                {
                    var install = new Button { Content = "Install mouse driver", Style = (Style)Window.Resources["Primary"], HorizontalAlignment = HorizontalAlignment.Left };
                    install.Click += async (s, e) => await RunAsync(async () => {
                        if (preview) throw new InvalidOperationException("Design preview: installation is disabled.");
                        install.IsEnabled = false;
                        try {
                            using (var process = Process.Start(new ProcessStartInfo {
                                FileName = Assembly.GetExecutingAssembly().Location, Arguments = "--install-driver", UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
                            })) {
                                if (process == null) throw new IOException("Driver setup could not start.");
                                await Task.Run(() => process.WaitForExit());
                                if (process.ExitCode != 0) throw new IOException("Driver setup did not finish. Try again after resolving the installation error.");
                            }
                            draft.DriverInstalled = state.DriverInstalled = true; state.Save();
                            WizardPage(); Status("Driver installed. Restart your PC after finishing setup.");
                        } catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { Text("WizardError", "Administrator permission was canceled. Click Install mouse driver when you're ready."); }
                        finally { install.IsEnabled = true; }
                    }); fields.Children.Add(install);
                }
                else fields.Children.Add(new TextBlock { Text = ready ? "●  Driver connected" : "●  Driver installed · restart required", Foreground = Brush("#A7DCBE"), FontSize = 16 });
            }
            else if (wizardStep <= 5)
            {
                string value = wizardStep == 1 ? draft.Dpi.ToString() : wizardStep == 2 ? draft.Polling.ToString() : wizardStep == 3 ? draft.Mouse : wizardStep == 4 ? draft.GameSensitivity.ToString() : draft.PadCm.ToString();
                wizardInput = new TextBox { Text = value, FontSize = 24, Padding = new Thickness(16), MaxLength = 120 };
                fields.Children.Add(wizardInput);
                string[] presets = wizardStep == 1 ? new[] { "400", "800", "1600", "3200" } : wizardStep == 2 ? new[] { "0", "500", "1000", "2000", "4000", "8000" } : new string[0];
                var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
                foreach (var preset in presets) { var b = new Button { Content = preset == "0" ? "Auto" : preset, Margin = new Thickness(0, 0, 7, 7), Padding = new Thickness(12, 9, 12, 9) }; string selected = preset; b.Click += (s, e) => wizardInput.Text = selected; row.Children.Add(b); }
                fields.Children.Add(row);
            }
            else if (wizardStep == 6 || wizardStep == 7)
            {
                choice = wizardStep == 6 ? draft.Style : draft.Starter;
                string[] choices = wizardStep == 6 ? new[] { "Arm", "Wrist", "Hybrid" } : new[] { "Keep current", "Quiet aim", "Balanced", "Flick boost" };
                foreach (var option in choices)
                {
                    string value = option; var b = new Button { Content = option, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 9), Background = Brush(option == choice ? "#6D469E" : "#2B2932") };
                    b.Click += (s, e) => { choice = value; foreach (Button item in fields.Children) item.Background = Brush(item == b ? "#6D469E" : "#2B2932"); }; fields.Children.Add(b);
                }
            }
            else fields.Children.Add(new TextBlock { Text = draft.Mouse + "\n" + draft.Dpi.ToString("N0") + " DPI  ·  " + (draft.Polling == 0 ? "Automatic polling" : draft.Polling + " Hz") + "\nGame sens " + draft.GameSensitivity + "  ·  " + draft.PadCm + " cm pad\n" + draft.Style + " aim  ·  " + draft.Starter, Foreground = Brush("#D6C0F2"), FontSize = 18, LineHeight = 32 });
            fields.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(230)));
        }
        private async Task NextWizardAsync()
        {
            if (wizardStep == 0 && !ready && !draft.DriverInstalled) throw new ArgumentException("Install the included mouse driver before continuing.");
            if (wizardStep >= 1 && wizardStep <= 5)
            {
                string input = wizardInput.Text.Trim(); double number;
                if (wizardStep == 3) draft.Mouse = input;
                else {
                    if (!double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out number) || double.IsNaN(number) || double.IsInfinity(number)) throw new ArgumentException("Enter a valid number to continue.");
                    if ((wizardStep == 1 || wizardStep == 2) && number != Math.Truncate(number)) throw new ArgumentException("DPI and polling rate must be whole numbers.");
                    if (wizardStep == 1) { if (number < 50 || number > 100000) throw new ArgumentException("Enter a DPI between 50 and 100,000."); draft.Dpi = (int)number; }
                    if (wizardStep == 2) { if (number < 0 || number > 8000) throw new ArgumentException("Use 0 for automatic, or a polling rate up to 8,000 Hz."); draft.Polling = (int)number; }
                    if (wizardStep == 4) draft.GameSensitivity = number;
                    if (wizardStep == 5) draft.PadCm = number;
                }
                draft.Validate();
            }
            if (wizardStep == 6) draft.Style = choice;
            if (wizardStep == 7) draft.Starter = choice;
            if (wizardStep < 8) { wizardStep++; WizardPage(); return; }
            if (preview) throw new InvalidOperationException("Design preview: saving and applying setup are disabled.");
            draft.Validate();
            var current = ready ? JObject.Parse(DriverConfig.GetActive().ToJSON()) : live;
            var configured = draft.Configure(current);
            if (ready) { await writer.ApplyAsync(current, configured, true); live = JObject.Parse(DriverConfig.GetActive().ToJSON()); }
            else live = configured;
            draft.Completed = true; draft.PendingApply = !ready; state = draft; state.Save();
            pending = expected = null; C<Border>("WizardOverlay").Visibility = Visibility.Collapsed;
            Navigate("Overview"); Refresh(); Status(ready ? "Your mouse setup is saved and active. Welcome to Flux." : "Setup saved. Restart your PC, then reopen Kaiden Flux to activate it.");
        }

        internal void PreviewWizard(int step) { OpenWizard(); wizardStep = Math.Max(0, Math.Min(8, step)); WizardPage(); }
        internal void PreviewAI()
        {
            Navigate("AI");
            Message("YOU", "Keep my slow aim steady, but give fast flicks a little more reach.", true);
            Message("FLUX", "This is a design preview of your tuning studio. A live proposal will use your saved mouse setup, show the exact changes here, and wait for you to apply them.", false);
            Text("ProposalSummary", "Example layout · no API request sent"); Text("ProposalDiff", "Horizontal · output cap\n1.65 → 2.00\n\nChanges will appear here after generation.");
        }
        internal void Render(string output)
        {
            Window.UpdateLayout();
            var image = new RenderTargetBitmap((int)Window.ActualWidth, (int)Window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(Window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(output)) encoder.Save(stream);
        }
    }
}
