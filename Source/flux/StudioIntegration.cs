using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;


namespace KaidenFlux
{
    internal sealed partial class FluxShell
    {
        private CurveEditorView curveEditor;
        private AimStudioView aimStudio;
        private AimSessionStore aimStore;
        private Button curveNav, aimNav;
        private TextBlock hardwareOverview;
        private bool studiosInitialized;
        private void InitializeStudios()
        {
            if (studiosInitialized) return; studiosInitialized = true;
            curveEditor = new CurveEditorView(Window, StageEditor, async () => { await RunAsync(ApplyPendingAsync); curveEditor.Load(live, pending ?? live); }, SaveCurve, Status);
            curveEditor.Load(live, pending ?? live);
            aimStore = new AimSessionStore(Path.Combine(FluxState.Folder, "aim-tests"));
            aimStudio = new AimStudioView(Window, aimStore, () => {
                if (busy) throw new InvalidOperationException("Wait for the current operation before starting a test.");
                return preview ? (JObject)live.DeepClone() : ready ? JObject.Parse(DriverConfig.GetActive().ToJSON()) : throw new InvalidOperationException("Connect the driver before associating a test with its live curve.");
            }, () => selectedGameProfile, Status, preview);
            var parent = (Grid)C<Grid>("Overview").Parent;
            foreach (var page in new FrameworkElement[] { curveEditor.Root, aimStudio.Root }) { Grid.SetRow(page, 1); page.Visibility = Visibility.Collapsed; parent.Children.Add(page); }
            var sidebar = (StackPanel)C<Button>("NavLibrary").Parent;
            // Keep the eight requested workspace destinations; the calculator remains available from profiles/overview.
            curveNav = new Button { Content = "◇   Curve Editor", Style = (Style)Window.Resources["Nav"] };
            aimNav = new Button { Content = "◉   Aim Studio", Style = (Style)Window.Resources["Nav"] };
            sidebar.Children.Insert(sidebar.Children.IndexOf(C<Button>("NavAI")), curveNav);
            sidebar.Children.Insert(sidebar.Children.IndexOf(profilesNav), aimNav);
            sidebar.Children.Remove(calculatorNav);
            foreach (var child in sidebar.Children.OfType<Button>()) child.Padding = new Thickness(12, 10, 12, 10);
            curveNav.Click += (s, e) => Navigate("Editor"); aimNav.Click += (s, e) => Navigate("Aim");
            // Add shortcuts without changing the existing curve library actions.
            var overview = C<Grid>("Overview");
            var shortcuts = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -3, 0, 0) };
            shortcuts.Children.Add(StudioUi.Button(Window, "Curve Editor", () => Navigate("Editor"), Status));
            shortcuts.Children.Add(StudioUi.Button(Window, "Aim Studio", () => Navigate("Aim"), Status));
            shortcuts.Children.Add(StudioUi.Button(Window, "Sensitivity", () => Navigate("Calculator"), Status));
            // A dedicated top row keeps shortcuts outside graph/card hit areas.
            overview.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
            foreach (FrameworkElement child in overview.Children) Grid.SetRow(child, Grid.GetRow(child) + 1);
            overview.Children.Add(shortcuts);
            hardwareOverview = new TextBlock { Text = "DPI and polling values are configured in Setup.", FontSize = 10, Foreground = StudioUi.Ink("#AAA6B2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            var shortcutStack = new StackPanel(); overview.Children.Remove(shortcuts); shortcutStack.Children.Add(hardwareOverview); shortcutStack.Children.Add(shortcuts); overview.Children.Add(shortcutStack);
            var aiButtons = (StackPanel)C<Button>("ApplyAI").Parent;
            aiButtons.Children.Add(StudioUi.Button(Window, "Compare curves", () => Navigate("Editor"), Status));
            aiButtons.Children.Add(StudioUi.Button(Window, "Discard proposal", DiscardStudioPreview, Status));
            var ai = C<Grid>("AIPage"); var right = ai.Children.OfType<StackPanel>().FirstOrDefault(c => Grid.GetColumn(c) == 1);
            if (right != null) { ai.Children.Remove(right); var scroll = new ScrollViewer { Content = right, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); ai.Children.Add(scroll); }
            foundation.CalculatorShortcut = () => Navigate("Calculator");
            Window.Closed += (s, e) => { aimStudio.Dispose(); };
        }
        private void StageEditor(JObject baseline, JObject value)
        {
            if (busy) throw new InvalidOperationException("Wait for the current operation before editing.");
            if (aimStudio != null && aimStudio.Running) throw new InvalidOperationException("Stop the aim test before editing its associated curve.");
            pendingAiRequest=null;
            FluxState.ValidateConfig(value);
            expected = (JObject)baseline.DeepClone(); pending = JToken.DeepEquals(baseline, value) ? null : (JObject)value.DeepClone();
            C<Button>("ApplyAI").IsEnabled = false; Refresh(); Status(pending == null ? "Preview cleared. Driver unchanged." : "Editor preview updated. Apply explicitly to change the driver.");
        }
        private void DiscardStudioPreview()
        {
            if (busy) return; pending = expected = null; pendingAiRequest=null; C<Button>("ApplyAI").IsEnabled = false;
            Text("ProposalSummary", "Proposal discarded. Driver settings unchanged."); Text("ProposalDiff", "");
            curveEditor?.Load(live, live); Refresh();
        }
        private void HideStudios(string destination)
        {
            if (!studiosInitialized) return;
            if (destination != "Aim" && aimStudio.Running) aimStudio.Stop(false);
            curveEditor.Root.Visibility = aimStudio.Root.Visibility = Visibility.Collapsed;
            curveNav.Background = aimNav.Background = Brush("#00000000");
        }
        private void ShowStudio(string page)
        {
            HideFoundation(); HideStudios(page);
            C<Grid>("Overview").Visibility = C<Grid>("AIPage").Visibility = C<ScrollViewer>("LibraryPage").Visibility = Visibility.Collapsed;
            foreach (string name in new[] { "NavOverview", "NavAI", "NavLibrary" }) C<Button>(name).Background = Brush("#00000000");
            if (page == "Editor") {
                curveEditor.Load(expected ?? live, pending ?? live); curveEditor.Root.Visibility = Visibility.Visible; curveNav.Background = Brush("#37303F");
                Text("PageTitle", "Curve editor"); Text("PageSubtitle", "Native curves, separate axes, drag handles and reversible previews.");
            } else if (page == "Aim") {
                aimStudio.LoadHistory(); aimStudio.Root.Visibility = Visibility.Visible; aimNav.Background = Brush("#37303F");
                Text("PageTitle", "Aim tests"); Text("PageSubtitle", "Flick, tracking and micro-adjustment practice with measured local results.");
            }
        }
        private void AddAimContext(JObject context)
        {
            context["dpiSource"] = "manually configured; hardware readback unverified";
            if (preview || aimStore == null) return;
            var recent = aimStore.Load().Where(s => s.Completed && s.ProfileId == selectedGameProfile?.Id).Take(5).ToArray();
            if (recent.Length == 0) return;
            context["aimTests"] = new JArray(recent.Select(s => new JObject { ["summary"] = s.Summary(), ["curveChangedFromCurrent"] = s.CurveHash != AimSession.Hash(live) }));
        }
    }
}
