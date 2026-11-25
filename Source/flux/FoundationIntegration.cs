using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Newtonsoft.Json.Linq;
using grapher.AI;

namespace KaidenFlux
{
    internal sealed partial class FluxShell
    {
        private FoundationViews foundation;
        private GameProfile selectedGameProfile;
        private Button profilesNav, calculatorNav;
        private GameProfileStore profileStore;
        private void InitializeFoundation()
        {
            if (foundation != null) return;
            profileStore = new GameProfileStore(Path.Combine(FluxState.Folder, "profiles"));
            if (!preview) {
                int skipped; var profiles = profileStore.LoadAll(out skipped);
                selectedGameProfile = profiles.FirstOrDefault(p => p.Id == state.SelectedGameProfileId);
            }
            foundation = new FoundationViews(Window, profileStore, () => pending ?? live, SelectGameProfile, Status, () => state, preview);
            foundation.Saved += profile => {
                if (state.SelectedGameProfileId == profile.Id) { selectedGameProfile = profile; foundation.SyncCalculator(profile); RefreshProfileSummary(); }
            };
            var parent = (Grid)C<Grid>("Overview").Parent;
            Grid.SetRow(foundation.ProfilesPage, 1); Grid.SetRow(foundation.CalculatorPage, 1);
            foundation.ProfilesPage.Visibility = foundation.CalculatorPage.Visibility = Visibility.Collapsed;
            parent.Children.Add(foundation.ProfilesPage); parent.Children.Add(foundation.CalculatorPage);
            var sidebar = (StackPanel)C<Button>("NavLibrary").Parent;
            profilesNav = new Button { Content = "▣   Game profiles", Style = (Style)Window.Resources["Nav"] };
            calculatorNav = new Button { Content = "◷   Sensitivity", Style = (Style)Window.Resources["Nav"] };
            profilesNav.Click += (s, e) => Navigate("Profiles"); calculatorNav.Click += (s, e) => Navigate("Calculator");
            int index = sidebar.Children.IndexOf(C<Button>("NavLibrary"));
            sidebar.Children.Insert(index, profilesNav); sidebar.Children.Insert(index + 1, calculatorNav);
            foundation.SyncCalculator(selectedGameProfile); RefreshProfileSummary();
        }
        private void ShowFoundation(string page)
        {
            InitializeFoundation();
            C<Grid>("Overview").Visibility = C<Grid>("AIPage").Visibility = C<ScrollViewer>("LibraryPage").Visibility = Visibility.Collapsed;
            foundation.ProfilesPage.Visibility = page == "Profiles" ? Visibility.Visible : Visibility.Collapsed;
            foundation.CalculatorPage.Visibility = page == "Calculator" ? Visibility.Visible : Visibility.Collapsed;
            foreach (string name in new[] { "NavOverview", "NavAI", "NavLibrary" }) C<Button>(name).Background = Brush("#00000000");
            profilesNav.Background = Brush(page == "Profiles" ? "#241F12" : "#00000000");
            calculatorNav.Background = Brush(page == "Calculator" ? "#241F12" : "#00000000");
            if (page == "Profiles") foundation.Reload(); else foundation.SyncCalculator(selectedGameProfile);
            Text("PageTitle", page == "Profiles" ? "Game profiles" : "Sensitivity calculator");
            Text("PageSubtitle", page == "Profiles" ? "Keep each game's sensitivity and preferred curve together." : "Reference conversions with your actual curve multiplier. Verify distances in-game.");
        }
        private void HideFoundation()
        {
            if (foundation == null) return;
            foundation.ProfilesPage.Visibility = foundation.CalculatorPage.Visibility = Visibility.Collapsed;
            profilesNav.Background = calculatorNav.Background = Brush("#00000000");
        }
        private void SelectGameProfile(GameProfile profile)
        {
            if (preview || busy) throw new InvalidOperationException("Profile selection is unavailable during preview or a running operation.");
            pendingAiRequest=null;
            JObject snapshot = ready ? JObject.Parse(DriverConfig.GetActive().ToJSON()) : (JObject)live.DeepClone();
            JObject curve = profile?.Curve == null ? null : TuningProposal.LoadCurve(snapshot, profile.Curve);
            state.SelectedGameProfileId = profile?.Id; state.Save();
            selectedGameProfile = profile?.Clone(); live = snapshot;
            pending = curve; expected = curve == null ? null : (JObject)snapshot.DeepClone();
            C<Button>("ApplyAI").IsEnabled = false;
            foundation.SyncCalculator(selectedGameProfile);
            Navigate("Overview"); Refresh(); RefreshProfileSummary();
            Status(profile == null ? "Game profile cleared. Driver settings unchanged." : curve == null ? "Game profile selected. No preferred curve attached; driver settings unchanged." : "Game profile selected. Its curve is in preview; click Apply curve to activate it.");
        }
        private void RefreshProfileSummary()
        {
            if (selectedGameProfile != null && C<Grid>("Overview").Visibility == Visibility.Visible)
                Text("PageSubtitle", selectedGameProfile.Name + "  ·  " + GameDefinition.Find(selectedGameProfile.GameId).Name + "  ·  Sens " + selectedGameProfile.Sensitivity.ToString("0.#####"));
        }
        private void AddGameContext(JObject context)
        {
            if (selectedGameProfile == null) return;
            context["gameSensitivity"] = selectedGameProfile.Sensitivity;
            context["gameProfile"] = JObject.FromObject(new { game = GameDefinition.Find(selectedGameProfile.GameId).Name,
                sensitivity = selectedGameProfile.Sensitivity, adsScopeValue = selectedGameProfile.AdsSensitivity, notes = selectedGameProfile.Notes });
        }
    }
}
