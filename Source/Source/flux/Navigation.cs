using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace KaidenFlux
{
    internal sealed partial class FluxShell
    {
        private readonly Dictionary<string,Button> categoryButtons = new Dictionary<string,Button>();
        private readonly Dictionary<string,string> categoryPages = new Dictionary<string,string> { {"Home","Overview"},{"Tuning","Profiles"},{"Practice","Aim"} };
        private WrapPanel categoryTabs;
        private bool navigationReady;
        private TextBlock homePreviewDetails;
        private void InitializeNavigation()
        {
            if(navigationReady)return;
            var sidebar=(StackPanel)C<Button>("NavLibrary").Parent;
            foreach(var button in sidebar.Children.OfType<Button>())button.Visibility=Visibility.Collapsed;
            ((TextBlock)sidebar.Children[0]).Text="YOUR WORKSPACE";
            int index=1;
            string[] hints={"Status and pending changes","Game profiles, curves and sensitivity","Aim tests and assisted tuning"};
            int i=0;
            foreach(string name in new[]{"Home","Tuning","Practice"}) {
                string selected=name;
                var b=new Button { Content=name,Style=(Style)Window.Resources["Nav"],Padding=new Thickness(18,15,18,15),ToolTip=hints[i++] };
                b.Click+=(s,e)=>Navigate(categoryPages[selected]); categoryButtons.Add(name,b); sidebar.Children.Insert(index++,b);
            }
            var parent=(Grid)C<Grid>("Overview").Parent;
            parent.RowDefinitions.Insert(1,new RowDefinition{Height=GridLength.Auto});
            foreach(FrameworkElement child in parent.Children)if(Grid.GetRow(child)>=1)Grid.SetRow(child,Grid.GetRow(child)+1);
            categoryTabs=new WrapPanel { Margin=new Thickness(0,0,0,12) }; Grid.SetRow(categoryTabs,1);parent.Children.Add(categoryTabs);
            // Keep the home graph and explicit Apply, but put precise parameters in Tuning.
            var apply=C<Button>("ApplyCurve");var actions=(StackPanel)apply.Parent;var dock=(DockPanel)actions.Parent;
            var old=dock.Children.OfType<ScrollViewer>().FirstOrDefault(); if(old!=null)old.Visibility=Visibility.Collapsed;
            // Home is a status/preview dashboard; precise tuning belongs in the Editor.
            var guide=new StackPanel { Margin=new Thickness(0,0,0,2) };
            guide.Children.Add(StudioUi.Text("Preview & apply",18));
            var previewInfo=new TextBlock { Name="HomePreviewDetails", Text="No changes pending. Your active curve is shown.", Foreground=Brush("#B9AACD"), FontSize=12, TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,12,0,14) };
            guide.Children.Add(previewInfo);
            guide.Children.Add(StudioUi.Button(Window,"Open curve editor →",()=>Navigate("Editor"),Status));
            guide.Children.Add(StudioUi.Text("A curve is only sent to the driver after you confirm Apply.",11));
            dock.Children.Add(new ScrollViewer { Content=guide,VerticalScrollBarVisibility=ScrollBarVisibility.Auto });
            homePreviewDetails=previewInfo;
            // Replace the duplicated shortcut row with a single first-run guide.
            var overview=C<Grid>("Overview");
            var row=overview.Children.OfType<StackPanel>().FirstOrDefault(c=>c.Children.Contains(hardwareOverview));
            if(row!=null) {
                row.Children.Clear(); row.Children.Add(hardwareOverview);
                var line=new WrapPanel();line.Children.Add(StudioUi.Button(Window,"Setup",()=>OpenCategorySetup(),Status));
                line.Children.Add(StudioUi.Button(Window,"Choose a game",()=>Navigate("Profiles"),Status));
                line.Children.Add(StudioUi.Button(Window,"Try an aim test",()=>Navigate("Aim"),Status));row.Children.Add(line);
            }
            var setupButton=StudioUi.Button(Window,"Setup",OpenCategorySetup,Status); setupButton.ToolTip="DPI, polling rate and mouse preferences"; sidebar.Children.Insert(index,setupButton);
            navigationReady=true;SyncNavigation("Overview");Refresh();
        }
        private void OpenCategorySetup()
        {
            if(busy)throw new InvalidOperationException("Wait for the current operation before editing setup.");
            OpenWizard();
        }
        private void SyncNavigation(string page)
        {
            if(!navigationReady)return;
            string category=page=="Overview"?"Home":page=="Aim"||page=="AI"?"Practice":"Tuning";
            categoryPages[category]=page;
            foreach(var pair in categoryButtons)pair.Value.Background=Brush(pair.Key==category?"#37303F":"#00000000");
            categoryTabs.Children.Clear();categoryTabs.Visibility=category=="Home"?Visibility.Collapsed:Visibility.Visible;
            Action<string,string> tab=(label,destination)=>{
                var b=StudioUi.Button(Window,label,()=>Navigate(destination),Status);b.Background=Brush(page==destination?"#51465F":"#2B2932");categoryTabs.Children.Add(b);
            };
            if(category=="Tuning") {
                tab("Game profiles","Profiles");tab("Curve editor","Editor");tab("Saved curves","Library");tab("Sensitivity calculator","Calculator");
            } else if(category=="Practice") {
                tab("Aim tests","Aim");tab("AI assistant","AI");
            }
        }
    }
}
