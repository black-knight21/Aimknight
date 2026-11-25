using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json.Linq;
using grapher.AI;

namespace KaidenFlux
{
    internal sealed class FoundationViews
    {
        private readonly Window window;
        private readonly GameProfileStore store;
        private readonly Func<JObject> configuration;
        private readonly Action<GameProfile> select;
        private readonly Action<string> status;
        private readonly bool preview;
        private readonly Func<FluxState> setup;
        private GameProfile draft;
        private readonly ListBox list = new ListBox { Background = B("#09090F"), Foreground = B("#F0EDE6"), BorderBrush = B("#382F18"), Padding = new Thickness(8) };
        private readonly TextBox name = new TextBox(), sensitivity = new TextBox(), ads = new TextBox(), yaw = new TextBox(), notes = new TextBox { AcceptsReturn = true, Height = 75, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock metadata = new TextBlock { Foreground = B("#8A8A98"), TextWrapping = TextWrapping.Wrap };
        private readonly WrapPanel games = new WrapPanel();
        private readonly TextBlock emptyProfiles = new TextBlock { Text = "No saved profiles yet.\nCreate your first one on the right.", TextWrapping = TextWrapping.Wrap, Foreground = B("#8A8A98"), Margin = new Thickness(4, 8, 4, 15) };
        private readonly TextBox calcDpi = new TextBox(), calcSens = new TextBox(), calcYaw = new TextBox(), calcPad = new TextBox();
        private readonly TextBlock calcResults = new TextBlock { FontSize = 19, LineHeight = 34, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock conversions = new TextBlock { Foreground = B("#CCB5F8"), LineHeight = 27, TextWrapping = TextWrapping.Wrap };
        private readonly WrapPanel calcGames = new WrapPanel();
        private string calcGame = "valorant";
        internal readonly FrameworkElement ProfilesPage, CalculatorPage;
        internal event System.Action<GameProfile> Saved;
        internal System.Action CalculatorShortcut;
        private static Brush B(string color) => (Brush)new BrushConverter().ConvertFromString(color);
        private static double Number(TextBox box, string label)
        {
            double value;
            if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw new ArgumentException("Enter a valid " + label + ".");
            SensitivityMath.Positive(value, label); return value;
        }
        private Button Action(string text, System.Action click, bool primary = false)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 9, 9), Padding = new Thickness(14, 10, 14, 10) };
            if (primary) button.Style = (Style)window.Resources["Primary"];
            button.Click += (s, e) => { try { click(); } catch (Exception ex) { status(ex.Message); } };
            return button;
        }
        private Border Card(UIElement child) => new Border { Style = (Style)window.Resources["Card"], Child = child, Margin = new Thickness(0, 0, 0, 16) };
        private static TextBlock Label(string text, int size = 13) => new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        private static void Input(Panel panel, string title, TextBox field)
        {
            panel.Children.Add(Label(title)); field.Margin = new Thickness(0, 0, 0, 16); panel.Children.Add(field);
        }
        internal FoundationViews(Window window, GameProfileStore store, Func<JObject> configuration, Action<GameProfile> select, Action<string> status, Func<FluxState> setup, bool preview)
        {
            this.window = window; this.store = store; this.configuration = configuration; this.select = select; this.status = status; this.setup = setup; this.preview = preview;
            ProfilesPage = BuildProfiles(); CalculatorPage = BuildCalculator();
            Reload(); NewProfile();
        }
        private FrameworkElement BuildProfiles()
        {
            var root = new Grid(); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(245) }); root.ColumnDefinitions.Add(new ColumnDefinition());
            var left = new DockPanel { Margin = new Thickness(0, 0, 18, 0) };
            var actions = new WrapPanel(); actions.Children.Add(Action("New", NewProfile)); actions.Children.Add(Action("Duplicate", Duplicate)); actions.Children.Add(Action("Delete", Delete));
            actions.Children.Add(Action("Sensitivity", () => { if (CalculatorShortcut == null) throw new InvalidOperationException("Calculator is initializing."); CalculatorShortcut(); }));
            DockPanel.SetDock(actions, Dock.Top); left.Children.Add(actions);
            DockPanel.SetDock(emptyProfiles, Dock.Top); left.Children.Add(emptyProfiles); left.Children.Add(list); root.Children.Add(left);
            list.SelectionChanged += (s, e) => { var chosen = list.SelectedItem as GameProfile; if (chosen != null) Edit(chosen); };
            var editor = new StackPanel(); editor.Children.Add(Label("A setup for each game", 22));
            editor.Children.Add(Label("Selecting a profile stages its curve for review. The driver changes only when you click Apply."));
            Input(editor, "Profile name", name);
            var advancedFields = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var advanced = new Expander { Header = "Advanced options · ADS, custom scale & notes", Content = advancedFields, Foreground = B("#C8B0E7"), Margin = new Thickness(0, 4, 0, 12) };
            foreach (var game in GameDefinition.Games)
            {
                string id = game.Id; var button = Action(game.Name, () => { draft.GameId = id; PaintGames(games, id); if (id == "custom") advanced.IsExpanded = true; }); button.Tag = id; games.Children.Add(button);
            }
            editor.Children.Add(games);
            Input(editor, "In-game sensitivity", sensitivity);
            Input(advancedFields, "ADS / scope value · optional, stored only", ads);
            Input(advancedFields, "Custom yaw override · degrees per count at sensitivity 1", yaw);
            Input(advancedFields, "Aiming notes", notes); editor.Children.Add(advanced);
            var commands = new WrapPanel();
            commands.Children.Add(Action("Attach current curve", () => { draft.Curve = (JObject)configuration().DeepClone(); metadata.Text = "Current curve attached. Save to keep it."; }));
            commands.Children.Add(Action("Save profile", Save, true));
            commands.Children.Add(Action("Select & preview", Use));
            var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) }; footer.Children.Add(commands); footer.Children.Add(metadata);
            var dock = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
            dock.Children.Add(new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var card = Card(dock); Grid.SetColumn(card, 1); root.Children.Add(card); return root;
        }
        private void PaintGames(Panel panel, string id)
        {
            foreach (Button button in panel.Children) button.Background = B((string)button.Tag == id ? "#684395" : "#181820");
        }
        private void NewProfile()
        {
            list.SelectedItem = null;
            Edit(new GameProfile { Sensitivity = setup().GameSensitivity, Name = "New game profile" });
        }
        private void Edit(GameProfile profile)
        {
            draft = profile.Clone(); name.Text = draft.Name; sensitivity.Text = draft.Sensitivity.ToString("G");
            ads.Text = draft.AdsSensitivity?.ToString("G") ?? ""; yaw.Text = draft.CustomYaw?.ToString("G") ?? ""; notes.Text = draft.Notes;
            PaintGames(games, draft.GameId);
            metadata.Text = (draft.Curve == null ? "No preferred curve attached." : "Preferred curve attached.") + "\nCreated " + draft.CreatedUtc.ToLocalTime().ToString("g") + " · Modified " + draft.ModifiedUtc.ToLocalTime().ToString("g");
        }
        private GameProfile Gather()
        {
            var profile = draft.Clone(); profile.Name = name.Text.Trim(); profile.Sensitivity = Number(sensitivity, "game sensitivity"); profile.Notes = notes.Text;
            profile.AdsSensitivity = string.IsNullOrWhiteSpace(ads.Text) ? (double?)null : Number(ads, "ADS sensitivity");
            profile.CustomYaw = string.IsNullOrWhiteSpace(yaw.Text) ? (double?)null : Number(yaw, "yaw"); profile.Validate(); return profile;
        }
        private void Save()
        {
            if (preview) throw new InvalidOperationException("Profile writes are disabled in design preview.");
            var profile = Gather(); store.Save(profile); Reload(profile.Id); Edit(profile); Saved?.Invoke(profile.Clone()); status("Game profile saved. No driver settings were changed.");
        }
        private void Use()
        {
            if (preview) throw new InvalidOperationException("Profile selection is disabled in design preview.");
            var profile = Gather(); store.Save(profile); select(profile); Reload(profile.Id);
        }
        private void Duplicate()
        {
            if (preview) throw new InvalidOperationException("Profile writes are disabled in design preview.");
            var chosen = list.SelectedItem as GameProfile;
            if (chosen == null) throw new ArgumentException("Select a saved profile to duplicate.");
            var copy = store.Duplicate(chosen); Reload(copy.Id); Edit(copy); status("Profile duplicated with a new identifier.");
        }
        private void Delete()
        {
            if (preview) throw new InvalidOperationException("Profile writes are disabled in design preview.");
            var chosen = list.SelectedItem as GameProfile;
            if (chosen == null) throw new ArgumentException("Select a saved profile to delete.");
            if (MessageBox.Show(window, "Move ‘" + chosen.Name + "’ to the local profile trash?", "AimKnight", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            store.Delete(chosen.Id); if (setup().SelectedGameProfileId == chosen.Id) select(null);
            Reload(); NewProfile(); status("Profile moved to profiles/trash. Driver settings were not changed.");
        }
        internal void Reload(string selected = null)
        {
            int skipped = 0;
            var profiles = preview ? new List<GameProfile>() : store.LoadAll(out skipped);
            list.ItemsSource = profiles;
            if (selected != null) list.SelectedItem = profiles.FirstOrDefault(p => p.Id == selected);
            emptyProfiles.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (skipped > 0) status(skipped + " unreadable profile file(s) were skipped.");
        }
        private FrameworkElement BuildCalculator()
        {
            var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(350) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
            var inputs = new StackPanel(); inputs.Children.Add(Label("Sensitivity calculator", 22));
            inputs.Children.Add(Label("DPI is manually configured until hardware readback is implemented."));
            foreach (var game in GameDefinition.Games) {
                string id = game.Id; var b = Action(game.Name, () => { calcGame = id; PaintGames(calcGames, id); calcYaw.Text = GameDefinition.Find(id).Yaw?.ToString("G") ?? ""; }); b.Tag = id; calcGames.Children.Add(b);
            }
            inputs.Children.Add(calcGames);
            Input(inputs, "Hardware DPI", calcDpi); Input(inputs, "In-game sensitivity", calcSens); Input(inputs, "Yaw · degrees/count at sensitivity 1", calcYaw); Input(inputs, "Usable pad width · cm", calcPad);
            var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            footer.Children.Add(Action("Calculate", Calculate, true));
            footer.Children.Add(Action("Use selected profile & setup", () => SyncCalculator(selectedForCalculator)));
            var dock = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
            dock.Children.Add(new ScrollViewer { Content = inputs, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            var left = Card(dock); left.Margin = new Thickness(0, 0, 18, 16); layout.Children.Add(left);
            var results = new StackPanel(); results.Children.Add(Label("Distance, not guesswork", 22)); results.Children.Add(calcResults);
            results.Children.Add(Label("Conversions · same DPI and base multiplier", 16)); results.Children.Add(conversions);
            results.Children.Add(new TextBlock { Text = "eDPI compares values within the same game scale. Acceleration makes cm/360 speed-dependent: the base and 30 in/s figures are separate samples, not one universal sensitivity. ADS/scope formulas are not assumed; optional ADS values are stored only. Reference yaw values come from KovaaK's converter (Valorant 0.06996, Source/CS2/Apex 0.022, Overwatch 0.0066); edited game configs, FOV behavior, and firmware can affect real results. Verify with a measured in-game turn.", TextWrapping = TextWrapping.Wrap, Foreground = B("#8A8A98"), Margin = new Thickness(0, 22, 0, 0), LineHeight = 22 });
            var right = new ScrollViewer { Content = Card(results), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(right, 1); layout.Children.Add(right);
            foreach (var box in new[] { calcDpi, calcSens, calcYaw, calcPad }) box.TextChanged += (s, e) => { calcResults.Text = "Inputs changed. Click Calculate."; conversions.Text = ""; };
            return layout;
        }
        private GameProfile selectedForCalculator;
        internal void SyncCalculator(GameProfile profile)
        {
            selectedForCalculator = profile?.Clone();
            var active = selectedForCalculator;
            calcGame = active?.GameId ?? "valorant";
            calcDpi.Text = setup().Dpi.ToString(); calcSens.Text = (active?.Sensitivity ?? setup().GameSensitivity).ToString("G");
            calcYaw.Text = (active?.CustomYaw ?? GameDefinition.Find(calcGame).Yaw)?.ToString("G") ?? "";
            calcPad.Text = setup().PadCm.ToString("G"); PaintGames(calcGames, calcGame);
            try { Calculate(); } catch (Exception ex) { calcResults.Text = ex.Message; }
        }
        private void Calculate()
        {
            calcResults.Text = ""; conversions.Text = "";
            double dpi = Number(calcDpi, "DPI"), sens = Number(calcSens, "sensitivity"), yawValue = Number(calcYaw, "yaw"), pad = Number(calcPad, "pad width");
            var json = configuration(); var config = FluxState.ValidateConfig(json);
            double normalizedDpi = (double)json["defaultDeviceConfig"]["DPI (normalizes input speed unit: counts/ms -> in/s)"];
            double baseline = (double)json["profiles"][0]["Output DPI"] / (normalizedDpi > 0 ? normalizedDpi : 1000);
            double factor = normalizedDpi > 0 ? 1000 / normalizedDpi : 1;
            double accelerated;
            using (var native = config.accels[0].CreateStatelessCopy()) accelerated = native.Accelerate(100, 0, factor, 100 * factor / 30).Item1 / 100;
            var result = SensitivityMath.Calculate(dpi, sens, yawValue, baseline, pad);
            var fast = SensitivityMath.Calculate(dpi, sens, yawValue, accelerated, pad);
            calcResults.Text = "Game eDPI  " + result.Edpi.ToString("0.##") + "\nBase multiplier  " + baseline.ToString("0.00") + "×\nBase distance  " + result.Cm360.ToString("0.##") + " cm/360\nBase distance  " + result.Inches360.ToString("0.##") + " inches/360\nAcross your pad  " + result.PadDegrees.ToString("0.#") + "°\nAt 30 " + (normalizedDpi > 0 ? "in/s" : "counts/ms") + "  " + fast.Cm360.ToString("0.##") + " cm/360\n";
            conversions.Text = string.Join("\n", GameDefinition.Games.Where(g => g.Yaw.HasValue).Select(g => g.Name + "  →  " + SensitivityMath.Convert(sens, yawValue, g.Yaw.Value, dpi, dpi).ToString("0.#####")));
        }
    }
}
