using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Newtonsoft.Json.Linq;

class FluxUiTests
{
    static object shell;
    static Type type;
    static Window window;
    static int passed;
    static T Field<T>(string name) => (T)type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shell);
    static T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
    static void Invoke(string name, params object[] args) => type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(shell, args);
    static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); passed++; }
    static void Pump()
    {
        var frame = new DispatcherFrame();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); window.UpdateLayout();
    }
    static void Click(string name) { Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    static T Member<T>(object value, string name) => (T)value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(value);
    static object Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Invoke(value, args);
    static Button FindTool(DependencyObject root, string label) { if (root is Button b && b.Content as string == label) return b; for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) { var found = FindTool(System.Windows.Media.VisualTreeHelper.GetChild(root, i), label); if (found != null) return found; } return null; }
    static void Tool(object value, string label) { var button = FindTool(Member<Grid>(value, "Root"), label); if (button == null) throw new Exception("Missing control: " + label); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
    [STAThread]
    static void Main(string[] args)
    {
        var assembly = Assembly.LoadFrom(args[0]); type = assembly.GetType("KaidenFlux.FluxShell", true);
        shell = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { true }, null);
        window = (Window)type.GetField("Window", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shell);
        window.Show(); Invoke("StartAsync"); Pump();
        try
        {
            var categories=Field<System.Collections.Generic.Dictionary<string,Button>>("categoryButtons");
            Check(categories.Count==3 && categories.ContainsKey("Home") && !categories.ContainsKey("Mouse") && categories.ContainsKey("Tuning") && categories.ContainsKey("Practice"), "three core categories exclude hardware management");
            Check(Control<Button>("NavLibrary").Visibility==Visibility.Collapsed && Control<Button>("NavAI").Visibility==Visibility.Collapsed, "legacy flat navigation stays hidden");
            FindTool(window,"Setup").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Check(Control<Border>("WizardOverlay").Visibility==Visibility.Visible,"Setup remains accessible without a hardware-control category");
            Control<Border>("WizardOverlay").Visibility=Visibility.Collapsed;
            Member<Button>(shell,"memoryToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Check(Member<TextBlock>(shell,"memoryStatus").Text.Contains("not sent"),"local-memory opt-out reports that history is not sent to Gemini");
            Member<Button>(shell,"memoryToggle").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump();
            Check(Member<TextBlock>(shell,"keyStatus").Text.Contains("disabled in preview"),"design preview does not load or persist API credentials");
            categories["Tuning"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(Field<WrapPanel>("categoryTabs").Children.Count==4, "Tuning exposes its four related tools as tabs");
            Invoke("Navigate","Calculator"); categories["Home"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(Field<WrapPanel>("categoryTabs").Visibility==Visibility.Collapsed, "Home hides unrelated tool tabs");
            categories["Tuning"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(Control<TextBlock>("PageTitle").Text.IndexOf("sensitivity",StringComparison.OrdinalIgnoreCase)>=0, "categories remember the last opened tool");
            categories["Practice"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(Field<WrapPanel>("categoryTabs").Children.Count==2, "Practice groups aim tests with the AI coach");
            Invoke("Navigate","Overview"); Pump();
            Check(!Control<Button>("ApplyCurve").IsEnabled && !Control<Button>("DiscardCurve").IsEnabled,"Home actions are disabled when no changes are pending");
            Check(Field<TextBlock>("homePreviewDetails").Text.Contains("Design preview"),"Home preview card does not claim to have read driver state in demo mode");
            var activeJson=(JObject)Field<JObject>("live").DeepClone();var normalized=(JObject)activeJson.DeepClone();
            const string dpiKey="DPI (normalizes input speed unit: counts/ms -> in/s)";
            normalized["defaultDeviceConfig"][dpiKey]=3200;normalized["profiles"][0]["Output DPI"]=(double)normalized["profiles"][0]["Output DPI"]*2;
            var activeSamples=(double[])Call(shell,"SampleCurve",DriverConfig.Convert(activeJson.ToString()).Item1,1600.0);
            var normalizedSamples=(double[])Call(shell,"SampleCurve",DriverConfig.Convert(normalized.ToString()).Item1,3200.0);
            Check(Math.Abs(activeSamples[0]-normalizedSamples[0])<0.000001,"each overlay samples with its own normalization DPI");
            Control<Slider>("SensSlider").Value = 1.25; Pump();
            Check(Control<TextBlock>("PreviewTag").Text=="UNAPPLIED PREVIEW" && Control<Button>("DiscardCurve").IsEnabled && !Control<Button>("ApplyCurve").IsEnabled,"pending demo preview enables Discard while blocking driver Apply");
            Check(Control<Canvas>("CurveCanvas").Children.OfType<System.Windows.Shapes.Polyline>().Count()==2,"Home draws both native active and proposed overlays");
            Check(Math.Abs((double)Field<JObject>("pending")["profiles"][0]["Output DPI"] - 2000) < 0.1, "live slider preview uses physical DPI scaling");
            Click("DiscardCurve"); Check(Field<JObject>("pending") == null, "discard clears pending preview");
            Invoke("Navigate", "Profiles");
            object foundation = type.GetField("foundation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(shell);
            var foundationType = foundation.GetType();
            Check(((FrameworkElement)foundationType.GetField("ProfilesPage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(foundation)).Visibility == Visibility.Visible, "game profiles navigation opens the implemented editor");
            Invoke("Navigate", "Calculator"); Pump();
            Check(((FrameworkElement)foundationType.GetField("CalculatorPage", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(foundation)).Visibility == Visibility.Visible, "sensitivity navigation opens the implemented calculator");
            Check(((TextBlock)foundationType.GetField("calcResults", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(foundation)).Text.Contains("cm/360"), "calculator renders actual curve-based results");
            Invoke("Navigate", "Overview");
            Check(Control<TextBox>("ModelId").Text == "gemini-3.5-flash-lite", "new sessions default to the faster model");
            Control<TextBox>("ChatInput").Clear(); Invoke("RestoreRequest", "Keep slow aim steady");
            Check(Control<TextBox>("ChatInput").Text == "Keep slow aim steady" && Control<Button>("RetryAI").Visibility == Visibility.Visible, "failed generation restores the request and exposes Retry");
            Control<TextBox>("ChatInput").Text = "My new draft"; Invoke("RestoreRequest", "Old failed request");
            Check(Control<TextBox>("ChatInput").Text == "My new draft", "request recovery preserves a newly typed draft");
            Click("PresetBalanced"); Check((double)Field<JObject>("pending")["profiles"][0]["Whole or horizontal accel parameters"]["acceleration"] == 0.12, "library presets update a preview");
            Click("ApplyCurve"); Check(Control<TextBlock>("Status").Text.Contains("disabled"), "design preview blocks driver activation");
            Invoke("Navigate", "Editor"); Pump();
            var editor = Field<object>("curveEditor");
            Check(Member<Grid>(editor, "Root").Visibility == Visibility.Visible, "curve editor navigation opens functional page");
            Check(Member<Canvas>(editor, "canvas").Children.Count >= 10, "native graph renders grid, overlays and controls");
            var numbers = Member<TextBox[]>(editor, "numbers"); numbers[1].Text = "0.2"; Tool(editor, "Update preview");
            Check((double)Field<JObject>("pending")["profiles"][0]["Whole or horizontal accel parameters"]["acceleration"] == 0.2, "editor precise input stages a preview");
            Tool(editor, "Undo"); Check((double)Field<JObject>("pending")["profiles"][0]["Whole or horizontal accel parameters"]["acceleration"] == 0.12, "editor Undo restores preceding preview");
            Tool(editor, "Redo"); Check((double)Field<JObject>("pending")["profiles"][0]["Whole or horizontal accel parameters"]["acceleration"] == 0.2, "editor Redo restores edit");
            Invoke("Navigate", "AI"); Invoke("Navigate", "Editor"); Tool(editor, "Undo");
            Check((double)Field<JObject>("pending")["profiles"][0]["Whole or horizontal accel parameters"]["acceleration"] == 0.12, "navigation retains editor history");
            Tool(editor, "Discard"); Check(Field<JObject>("pending") == null, "editor Discard restores complete baseline");
            numbers[1].Text = "0.25"; Tool(editor, "Save curve");
            Check((double)Field<JObject>("pending")["profiles"][0]["Whole or horizontal accel parameters"]["acceleration"] == 0.25 && Control<TextBlock>("Status").Text.Contains("disabled"), "Save captures precise edits while preview mode blocks persistence");
            Tool(editor, "Discard");
            foreach(string curveMode in new[]{"Linear","Natural","Classic","Power","Jump","Synchronous","Off"}) {
                Tool(editor,curveMode); Check(Field<JObject>("pending")!=null,curveMode+" selector stages a reversible preview");
            }
            Tool(editor,"Natural");
            Check(Member<TextBlock[]>(editor,"labels")[1].Text=="Decay rate", "Natural displays its own native parameter labels");
            numbers[1].Text="0.2";numbers[3].Text="1.8";Tool(editor,"Update preview");
            Check((double)Field<JObject>("pending")["profiles"][0]["Whole or horizontal accel parameters"]["decayRate"]==0.2,"Natural precise controls stage native decay changes");
            Tool(editor,"Discard");Check(Field<JObject>("pending")==null,"discard after mode editing restores the original complete curve");
            Invoke("Navigate", "Aim"); Pump(); var aim = Field<object>("aimStudio");
            foreach (var mode in new[] { "Flick", "Tracking", "Micro" }) {
                Tool(aim, mode); Tool(aim, "Start session");
                Check((bool)aim.GetType().GetProperty("Running", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(aim), mode + " starts actual high-resolution session timer");
                Tool(aim, "Stop & save");
                Check(!(bool)aim.GetType().GetProperty("Running", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(aim) && Member<TextBlock>(aim, "results").Text.Contains("Interrupted"), mode + " stops and renders measured interrupted-session report");
            }
            Tool(aim, "Tracking"); Tool(aim, "Start session");
            var timedFrame = new DispatcherFrame(); var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            poll.Tick += (s, e) => { if (!(bool)aim.GetType().GetProperty("Running", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(aim) || elapsed.Elapsed.TotalSeconds > 33) timedFrame.Continue = false; };
            poll.Start(); Dispatcher.PushFrame(timedFrame); poll.Stop();
            var session = Member<object>(aim, "session");
            Check(Member<bool>(session, "Completed") && Math.Abs(Member<double>(session, "DurationSeconds") - 30) < 0.1, "tracking auto-completes a real 30-second rendering/timing session");
            var metrics = (JObject)Call(session, "Summary");
            Check((double)metrics["trackingCoveragePercent"] > 0 && Member<System.Collections.IList>(session, "Tracking").Count > 0, "tracking records actual observed coverage and timed samples");
            Tool(aim, "Start session"); Invoke("Navigate", "Hardware"); Pump();
            Check(!(bool)aim.GetType().GetProperty("Running", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(aim), "leaving Aim Studio stops test and detaches frame handler");
            Check(Control<Grid>("Overview").Visibility==Visibility.Visible,"removed hardware route safely returns to Dashboard");
            Check(assembly.GetType("KaidenFlux.Hardware.WindowsHidMouseDevice")==null,"application contains no HID enumeration backend");
            Invoke("Navigate", "Overview"); Pump();
            Invoke("PreviewWizard", 1); Pump();
            var input = (TextBox)Control<StackPanel>("WizardFields").Children[0]; input.Text = "not a number";
            Click("WizardNext"); Check(Field<int>("wizardStep") == 1 && Control<TextBlock>("WizardError").Text.Length > 0, "wizard rejects invalid DPI without advancing");
            input.Text = "1600"; Click("WizardNext"); Check(Field<int>("wizardStep") == 2, "wizard advances from DPI to polling");
            ((TextBox)Control<StackPanel>("WizardFields").Children[0]).Text = "1000"; Click("WizardNext");
            ((TextBox)Control<StackPanel>("WizardFields").Children[0]).Text = "Test mouse"; Click("WizardNext");
            ((TextBox)Control<StackPanel>("WizardFields").Children[0]).Text = "0.14"; Click("WizardNext");
            ((TextBox)Control<StackPanel>("WizardFields").Children[0]).Text = "30"; Click("WizardNext");
            Check(Field<int>("wizardStep") == 6, "polling, mouse, sensitivity, and pad steps advance independently");
            Click("WizardNext"); Click("WizardNext"); Check(Field<int>("wizardStep") == 8, "aiming style and starter curve lead to review");
            Click("WizardNext"); Check(Control<TextBlock>("WizardError").Text.Contains("disabled"), "preview blocks setup saves and writes");
            Invoke("PreviewWizard", 0); Pump();
            var install = (Button)Control<StackPanel>("WizardFields").Children[0]; install.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(Control<TextBlock>("WizardError").Text.Contains("disabled"), "preview blocks installer launch");
            Control<Border>("WizardOverlay").Visibility = Visibility.Collapsed;
            window.Width = 1080; window.Height = 740; Pump();
            var button = Control<Button>("ApplyCurve");
            var bounds = button.TransformToAncestor(window).TransformBounds(new Rect(new Point(0, 0), button.RenderSize));
            Check(bounds.Bottom < window.ActualHeight && button.ActualHeight > 20, "Apply remains visible at minimum window size");
            Console.WriteLine(passed + " UI checks passed. No live services were contacted.");
        }
        finally { window.Close(); }
    }
}
