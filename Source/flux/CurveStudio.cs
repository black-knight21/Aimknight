using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Newtonsoft.Json.Linq;

namespace KaidenFlux
{
    internal static class StudioUi
    {
        internal static Brush Ink(string value) => (Brush)new BrushConverter().ConvertFromString(value);
        internal static TextBlock Text(string value, int size = 13) => new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        internal static Button Button(Window window, string text, Action action, Action<string> error, bool primary = false)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(13, 10, 13, 10) };
            if (primary) b.Style = (Style)window.Resources["Primary"];
            b.Click += (s, e) => { try { action(); } catch (Exception ex) { error(ex.Message); } }; return b;
        }
        internal static Border Card(Window window, UIElement content) => new Border { Style = (Style)window.Resources["Card"], Child = content, Margin = new Thickness(0, 0, 0, 12) };
    }
    internal sealed class CurveHistory
    {
        private readonly List<JObject> undo = new List<JObject>(), redo = new List<JObject>();
        internal JObject Baseline { get; private set; }
        internal JObject Current { get; private set; }
        internal void Load(JObject baseline, JObject initial) { Baseline = (JObject)baseline.DeepClone(); Current = (JObject)initial.DeepClone(); undo.Clear(); redo.Clear(); }
        internal void Set(JObject value, bool record = true)
        {
            FluxState.ValidateConfig(value);
            if (JToken.DeepEquals(Current, value)) return;
            if (record) { undo.Add((JObject)Current.DeepClone()); if (undo.Count > 100) undo.RemoveAt(0); redo.Clear(); }
            Current = (JObject)value.DeepClone();
        }
        internal void CommitDrag(JObject original) { if (!JToken.DeepEquals(original, Current)) { undo.Add((JObject)original.DeepClone()); if (undo.Count > 100) undo.RemoveAt(0); redo.Clear(); } }
        internal bool Undo() { if (undo.Count == 0) return false; redo.Add(Current); Current = undo[undo.Count - 1]; undo.RemoveAt(undo.Count - 1); return true; }
        internal bool Redo() { if (redo.Count == 0) return false; undo.Add(Current); Current = redo[redo.Count - 1]; redo.RemoveAt(redo.Count - 1); return true; }
        internal static string AxisKey(string axis) => axis == "y" ? "Vertical accel parameters" : "Whole or horizontal accel parameters";
        internal static string ModeName(JToken args) => (string)args["mode"] == "classic" ? ((double)args["exponentClassic"] == 2 ? "Linear" : "Classic") : (string)args["mode"] == "noaccel" ? "Off" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase((string)args["mode"]);
        internal static string[] Fields(string mode) => mode == "natural" ? new[] { "decayRate", "inputOffset", "limit" } : mode == "power" ? new[] { "scale", "outputOffset", "cap.y" } : mode == "synchronous" ? new[] { "gamma", "syncSpeed", "motivity" } : mode == "jump" ? new[] { "smooth", "cap.x", "cap.y" } : new[] { "acceleration", "inputOffset", "cap.y" };
        internal static JToken Parameter(JToken args,string field) => field.StartsWith("cap.") ? args["Cap / Jump"][field.Substring(4)] : args[field];
        internal static void Parameter(JToken args,string field,double value) { if(field.StartsWith("cap."))args["Cap / Jump"][field.Substring(4)]=value;else args[field]=value; }
        internal static JObject SetMode(JObject source,string axis,string name)
        {
            if(axis!="x"&&axis!="y"&&axis!="both")throw new ArgumentException("Choose an axis.");
            if(!new[]{"Linear","Classic","Natural","Power","Synchronous","Jump","Off"}.Contains(name))throw new ArgumentException("Unsupported mode.");
            var next=(JObject)source.DeepClone();
            foreach(string a in axis=="both"?new[]{"x","y"}:new[]{axis}) {
                var args=next["profiles"][0][AxisKey(a)]; args["mode"]=name=="Linear"||name=="Classic"?"classic":name=="Off"?"noaccel":name.ToLowerInvariant();
                if(name=="Linear")args["exponentClassic"]=2;
                if(name=="Classic"&&(double)args["exponentClassic"]==2)args["exponentClassic"]=2.5;
                if(name=="Linear"||name=="Classic"||name=="Power")args["Cap mode"]="output";
            }
            FluxState.ValidateConfig(next);return next;
        }
        internal static JObject SetOption(JObject source,string axis,string field,JToken value)
        {
            if(axis!="x"&&axis!="y"&&axis!="both")throw new ArgumentException("Choose an axis.");
            if(field=="Cap mode") {if(value.Type!=JTokenType.String||!new[]{"input","output","in_out"}.Contains((string)value))throw new ArgumentException("Unsupported cap mode.");}
            else if(field=="Gain / Velocity"||field=="whole") {if(value.Type!=JTokenType.Boolean)throw new ArgumentException("Use a boolean switch.");}
            else if(field=="inputCap") {if(value.Type!=JTokenType.Float&&value.Type!=JTokenType.Integer||double.IsNaN((double)value)||double.IsInfinity((double)value)||(double)value<0)throw new ArgumentException("Input cap must be finite and nonnegative.");}
            else throw new ArgumentException("Unsupported curve option.");
            var next=(JObject)source.DeepClone();if(field=="whole")next["profiles"][0]["Input speed calculation parameters"]["Whole/combined accel (set false for 'by component' mode)"]=value.DeepClone();
            else foreach(string a in axis=="both"?new[]{"x","y"}:new[]{axis}) {var args=next["profiles"][0][AxisKey(a)];if(field=="inputCap")args["Cap / Jump"]["x"]=value.DeepClone();else args[field]=value.DeepClone();}
            FluxState.ValidateConfig(next);return next;
        }
        internal static JObject EditControls(JObject source,string axis,double[] values,double exponent)
        {
            if(axis!="x"&&axis!="y"&&axis!="both")throw new ArgumentException("Choose an axis.");
            if(axis=="both"&&(string)source["profiles"][0][AxisKey("x")]["mode"]!=(string)source["profiles"][0][AxisKey("y")]["mode"])throw new ArgumentException("Choose a curve type for both axes before linked editing.");
            if(values.Length!=4||values.Any(v=>double.IsNaN(v)||double.IsInfinity(v))||double.IsNaN(exponent)||double.IsInfinity(exponent))throw new ArgumentException("Use finite numbers.");
            if(values[0]<=0||values[0]>10)throw new ArgumentException("Base sensitivity must be above 0 and at most 10.");
            var next=(JObject)source.DeepClone();next["profiles"][0]["Output DPI"]=values[0]*Dpi(source);
            foreach(string a in axis=="both"?new[]{"x","y"}:new[]{axis}) {
                var args=next["profiles"][0][AxisKey(a)];string mode=(string)args["mode"];
                if(mode=="noaccel")continue;
                if(!new[]{"classic","natural","power","synchronous","jump"}.Contains(mode))throw new ArgumentException("Existing LUT curves remain read-only in this editor.");
                var fields=Fields(mode);for(int i=0;i<3;i++)Parameter(args,fields[i],values[i+1]);
                if(mode=="classic")args["exponentClassic"]=exponent;
                if(mode=="power")args["exponentPower"]=exponent;
                if(mode=="synchronous")args["smooth"]=exponent;
                if(mode=="natural"&&values[3]<=1)throw new ArgumentException("Use a Natural acceleration limit above 1.");
            }
            FluxState.ValidateConfig(next);return next;
        }
        internal static double Dpi(JObject config) { double dpi = (double)config["defaultDeviceConfig"]["DPI (normalizes input speed unit: counts/ms -> in/s)"]; return dpi > 0 ? dpi : 1000; }
        internal static JObject Edit(JObject source, string axis, double sensitivity, double acceleration, double offset, double cap)
        {
            foreach (var v in new[] { sensitivity, acceleration, offset, cap }) if (double.IsNaN(v) || double.IsInfinity(v)) throw new ArgumentException("Use finite numeric values.");
            if (sensitivity <= 0 || sensitivity > 10 || acceleration < 0 || acceleration > 5 || offset < 0 || offset > 1000 || cap < 1 || cap > 10) throw new ArgumentException("Use sensitivity 0–10×, acceleration 0–5, offset 0–1000, and cap 1–10×.");
            if (axis != "x" && axis != "y" && axis != "both") throw new ArgumentException("Choose X, Y, or linked editing.");
            var next = (JObject)source.DeepClone(); next["profiles"][0]["Output DPI"] = sensitivity * Dpi(source);
            foreach (var a in axis == "both" ? new[] { "x", "y" } : new[] { axis })
            {
                var args = next["profiles"][0][AxisKey(a)];
                if ((string)args["mode"] != "classic" || (string)args["Cap mode"] != "output") throw new ArgumentException("These controls require Classic mode with an output cap. Select a supported curve type to preview it explicitly.");
                args["acceleration"] = acceleration; args["inputOffset"] = offset; args["Cap / Jump"]["y"] = cap;
            }
            FluxState.ValidateConfig(next); return next;
        }
        internal static double[] Samples(JObject json, string axis, int count = 121)
        {
            var config = FluxState.ValidateConfig(json); double dpi = (double)json["defaultDeviceConfig"]["DPI (normalizes input speed unit: counts/ms -> in/s)"];
            double factor = dpi > 0 ? 1000 / dpi : 1; var output = new double[count];
            using (var native = config.accels[0].CreateStatelessCopy())
                for (int i = 0; i < count; i++) {
                    double speed = Math.Max(0.05, 60.0 * i / (count - 1));
                    var value = native.Accelerate(axis == "y" ? 0 : 100, axis == "y" ? 100 : 0, factor, 100 * factor / speed);
                    output[i] = (axis == "y" ? value.Item2 : value.Item1) / 100;
                    if (double.IsNaN(output[i]) || double.IsInfinity(output[i])) throw new ArgumentException("The curve contains invalid output.");
                }
            return output;
        }
    }
    internal sealed class CurveEditorView
    {
        internal readonly Grid Root = new Grid();
        internal readonly CurveHistory History = new CurveHistory();
        private readonly Canvas canvas = new Canvas { Background = StudioUi.Ink("#09090F"), MinHeight = 180, ClipToBounds = true };
        private readonly TextBox[] numbers = Enumerable.Range(0, 4).Select(i => new TextBox { Margin = new Thickness(0, 0, 0, 14) }).ToArray();
        private readonly TextBlock[] labels = new TextBlock[4];
        private readonly TextBox exponentBox = new TextBox { Margin = new Thickness(0,0,0,14) };
        private readonly StackPanel exponentPanel = new StackPanel();
        private readonly TextBlock extraLabel = StudioUi.Text("Exponent");
        private readonly Dictionary<string,Button> modeButtons = new Dictionary<string,Button>();
        private readonly Dictionary<string,Button> capButtons = new Dictionary<string,Button>();
        private readonly TextBox inputCapBox = new TextBox();
        private Button gainButton;
        private Button axesButton;
        private readonly TextBlock axisNotice=StudioUi.Text("",11);
        private readonly TextBlock inspect = StudioUi.Text("Hover the graph to inspect native-engine output.");
        private readonly Action<JObject, JObject> stage;
        private readonly Action<string> status;
        private string axis = "x", dragging;
        private Point dragStart;
        private double[] dragValues;
        private JObject dragOriginal;
        private double graphMax = 2;
        private double[] cachedBefore, cachedAfter;
        private readonly Window window;
        internal CurveEditorView(Window window, Action<JObject, JObject> stage, Func<Task> apply, Action save, Action<string> status)
        {
            this.window = window; this.stage = stage; this.status = status;
            Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Root.RowDefinitions.Add(new RowDefinition());
            var tools = new WrapPanel();
            Action<string, Action> add = (text, action) => tools.Children.Add(StudioUi.Button(window, text, action, status));
            add("X / whole", () => { axis = "x"; Refresh(); }); add("Y / vertical", () => { axis = "y"; Refresh(); }); add("Link X & Y", () => { axis = "both"; Refresh(); });
            add("Undo", () => { if (History.Undo()) Changed(); }); add("Redo", () => { if (History.Redo()) Changed(); });
            add("Reset", () => { History.Set(History.Baseline); Changed(); });
            add("Discard", () => { History.Load(History.Baseline, History.Baseline); Changed(); });
            add("Save curve", () => { CaptureNumbers(); save(); });
            var applyButton = StudioUi.Button(window, "Apply preview", () => { }, status, true);
            applyButton.Click += async (s, e) => { Root.IsEnabled = false; try {
                CaptureNumbers();
                await apply();
            } catch (Exception ex) { status(ex.Message); } finally { Root.IsEnabled = true; } }; tools.Children.Add(applyButton); Root.Children.Add(tools);
            var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(275) }); Grid.SetRow(body, 1); Root.Children.Add(body);
            var plot = new DockPanel(); var legend = StudioUi.Text("CURRENT · muted purple     PREVIEW · lavender\nDrag handles for Linear, Classic or Natural; use precise controls for all supported types."); DockPanel.SetDock(legend, Dock.Top); plot.Children.Add(legend);
            DockPanel.SetDock(inspect, Dock.Bottom); inspect.Margin = new Thickness(0, 12, 0, 0); plot.Children.Add(inspect); plot.Children.Add(canvas);
            var card = StudioUi.Card(window, plot); card.Margin = new Thickness(0, 0, 15, 0); body.Children.Add(card);
            var panel = new StackPanel(); panel.Children.Add(StudioUi.Text("Precise controls", 20));
            panel.Children.Add(StudioUi.Text("Curve type",12));var modes=new WrapPanel();
            foreach(string mode in new[]{"Off","Linear","Natural","Classic","Power","Jump","Synchronous"}) { string selected=mode;var button=StudioUi.Button(window,mode,()=>{History.Set(CurveHistory.SetMode(History.Current,axis,selected));Changed();},status);button.Padding=new Thickness(10,8,10,8);modeButtons.Add(mode,button);modes.Children.Add(button); }panel.Children.Add(modes);
            string[] titles = { "Base sensitivity · multiplier", "Classic acceleration", "Input offset · normalized speed", "Output cap · multiplier" };
            string[] tips = { "Changes global base sensitivity. Existing vertical and directional ratios are preserved.", "Controls how quickly Classic acceleration rises. This is a driver parameter, not an invented speed setting.", "Input-speed threshold before acceleration begins.", "Limits the Classic acceleration output scale." };
            for (int i = 0; i < 4; i++) { labels[i]=StudioUi.Text(titles[i]);panel.Children.Add(labels[i]); numbers[i].ToolTip = tips[i]; panel.Children.Add(numbers[i]); }
            exponentPanel.Children.Add(extraLabel);exponentPanel.Children.Add(exponentBox);panel.Children.Add(exponentPanel);
            var advancedFields=new StackPanel {Margin=new Thickness(0,12,0,0)};
            gainButton=StudioUi.Button(window,"Gain",()=>{var args=History.Current["profiles"][0][CurveHistory.AxisKey(axis=="y"?"y":"x")];History.Set(CurveHistory.SetOption(History.Current,axis,"Gain / Velocity",new JValue(!(bool)args["Gain / Velocity"])));Changed();},status);gainButton.ToolTip="Switch between the driver's gain and legacy velocity calculation.";advancedFields.Children.Add(gainButton);
            axesButton=StudioUi.Button(window,"Axes",()=>{bool whole=(bool)History.Current["profiles"][0]["Input speed calculation parameters"]["Whole/combined accel (set false for 'by component' mode)"];History.Set(CurveHistory.SetOption(History.Current,axis,"whole",new JValue(!whole)));Changed();},status);advancedFields.Children.Add(axesButton);advancedFields.Children.Add(axisNotice);
            advancedFields.Children.Add(StudioUi.Text("Cap mode · Linear / Classic / Power",11));var caps=new WrapPanel();
            foreach(string cap in new[]{"input","output","in_out"}) {string selected=cap;var b=StudioUi.Button(window,cap=="in_out"?"Both":CultureInfo.InvariantCulture.TextInfo.ToTitleCase(cap),()=>{History.Set(CurveHistory.SetOption(History.Current,axis,"Cap mode",new JValue(selected)));Changed();},status);b.Padding=new Thickness(9,8,9,8);capButtons.Add(cap,b);caps.Children.Add(b);}advancedFields.Children.Add(caps);
            advancedFields.Children.Add(StudioUi.Text("Input cap · speed",11));advancedFields.Children.Add(inputCapBox);
            panel.Children.Add(new Expander { Header="Advanced curve controls",Content=advancedFields,Foreground=StudioUi.Ink("#D4AF37"),Margin=new Thickness(0,0,0,12) });
            panel.Children.Add(StudioUi.Button(window, "Update preview", () => EditNumbers(), status, true));

            panel.Children.Add(StudioUi.Text("X and Y are separate unless Link X & Y is selected. All changes stay in preview. Ratios, rotation, snapping and device assignments are preserved. Advanced controls change only the options you select."));
            var scroll = new ScrollViewer { Content = StudioUi.Card(window, panel), VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
            canvas.SizeChanged += (s, e) => Draw();
            canvas.MouseMove += Move;
            canvas.MouseLeftButtonUp += (s, e) => EndDrag(); canvas.LostMouseCapture += (s, e) => { if (dragging != null) EndDrag(); };
        }
        internal void Load(JObject baseline, JObject initial) { if (History.Current == null || !JToken.DeepEquals(History.Baseline, baseline) || !JToken.DeepEquals(History.Current, initial)) History.Load(baseline, initial); Refresh(); }
        private double[] Values()
        {
            var profile = History.Current["profiles"][0]; var args = profile[CurveHistory.AxisKey(axis == "y" ? "y" : "x")];
            var fields=CurveHistory.Fields((string)args["mode"]);
            return new[] { (double)profile["Output DPI"] / CurveHistory.Dpi(History.Current), (double)CurveHistory.Parameter(args,fields[0]), (double)CurveHistory.Parameter(args,fields[1]), (double)CurveHistory.Parameter(args,fields[2]) };
        }
        private void EditNumbers()
        {
            var v = numbers.Select(n => double.Parse(n.Text, CultureInfo.InvariantCulture)).ToArray();
            var next=CurveHistory.EditControls(History.Current, axis, v, double.Parse(exponentBox.Text,CultureInfo.InvariantCulture));
            string mode=(string)next["profiles"][0][CurveHistory.AxisKey(axis=="y"?"y":"x")]["mode"];
            if(mode=="classic"||mode=="power")next=CurveHistory.SetOption(next,axis,"inputCap",new JValue(double.Parse(inputCapBox.Text,CultureInfo.InvariantCulture)));
            History.Set(next); Changed();
        }
        private void CaptureNumbers()
        {
            var args = History.Current["profiles"][0][CurveHistory.AxisKey(axis == "y" ? "y" : "x")];
            if ((string)args["mode"] != "lut") EditNumbers();
        }
        private void Changed() { stage(History.Baseline, History.Current); Refresh(); }
        internal void Refresh() {
            if (History.Current == null) return; var args=History.Current["profiles"][0][CurveHistory.AxisKey(axis=="y"?"y":"x")];string mode=(string)args["mode"];
            string[] names=mode=="natural"?new[]{"Decay rate","Input offset","Limit · multiplier"}:mode=="power"?new[]{"Scale","Output offset","Output cap"}:mode=="synchronous"?new[]{"Gamma","Synchronous speed","Motivity"}:mode=="jump"?new[]{"Smooth · 0 to 1","Jump input speed","Jump output multiplier"}:new[]{"Acceleration","Input offset","Output cap"};
            var values=Values();for(int i=0;i<4;i++){numbers[i].Text=values[i].ToString("0.######",CultureInfo.InvariantCulture);numbers[i].IsEnabled=i==0||mode!="noaccel"&&mode!="lut";if(i>0){labels[i].Text=names[i-1];numbers[i].ToolTip="Native "+mode+" parameter: "+names[i-1];}}
            exponentBox.Text=((double)args[mode=="power"?"exponentPower":mode=="synchronous"?"smooth":"exponentClassic"]).ToString("0.######",CultureInfo.InvariantCulture);exponentPanel.Visibility=mode=="classic"||mode=="power"||mode=="synchronous"?Visibility.Visible:Visibility.Collapsed;
            extraLabel.Text=mode=="power"?"Power exponent":mode=="synchronous"?"Smooth · 0 to 1":"Classic exponent · Linear = 2";
            gainButton.Content=(bool)args["Gain / Velocity"]?"Calculation: Gain":"Calculation: Velocity";
            bool whole=(bool)History.Current["profiles"][0]["Input speed calculation parameters"]["Whole/combined accel (set false for 'by component' mode)"];axesButton.Content=whole?"Axes: Combined":"Axes: By component";axisNotice.Text=whole?"Combined uses the whole/X curve. Switch to By component to activate a separate Y curve.":"X and Y curves are active separately. Link edits only when you want both changed.";
            inputCapBox.Text=((double)args["Cap / Jump"]["x"]).ToString("0.######",CultureInfo.InvariantCulture);inputCapBox.IsEnabled=mode=="classic"||mode=="power";
            foreach(var pair in capButtons){pair.Value.IsEnabled=mode=="classic"||mode=="power";pair.Value.Background=StudioUi.Ink((string)args["Cap mode"]==pair.Key?"#332B16":"#181820");}
            foreach(var pair in modeButtons)pair.Value.Background=StudioUi.Ink(pair.Key==CurveHistory.ModeName(args)?"#332B16":"#181820");Draw();
        }
        private void Draw()
        {
            if (History.Current == null || canvas.ActualWidth < 90 || canvas.ActualHeight < 90) return;
            canvas.Children.Clear();
            try {
                string a = axis == "y" ? "y" : "x"; var before = CurveHistory.Samples(History.Baseline, a); var after = CurveHistory.Samples(History.Current, a);
                cachedBefore = before; cachedAfter = after;
                graphMax = Math.Max(1.5, Math.Max(before.Max(), after.Max()) * 1.25);
                double w = canvas.ActualWidth - 55, h = canvas.ActualHeight - 45;
                for (int i = 0; i <= 4; i++) {
                    double y = 15 + h * i / 4; canvas.Children.Add(new Line { X1 = 35, X2 = 35 + w, Y1 = y, Y2 = y, Stroke = StudioUi.Ink("#1E1C24") });
                    var label = StudioUi.Text((graphMax * (1 - i / 4.0)).ToString("0.0"), 10); Canvas.SetTop(label, y - 6); canvas.Children.Add(label);
                }
                for (int i = 0; i <= 3; i++) { var label = StudioUi.Text((i * 20).ToString(), 10); Canvas.SetLeft(label, 32 + w * i / 3); Canvas.SetTop(label, 18 + h); canvas.Children.Add(label); }
                foreach (var item in new[] { Tuple.Create(before, "#5A5A68"), Tuple.Create(after, "#D4AF37") }) {
                    var line = new Polyline { Stroke = StudioUi.Ink(item.Item2), StrokeThickness = 3, IsHitTestVisible = false };
                    for (int i = 0; i < item.Item1.Length; i++) line.Points.Add(new Point(35 + w * i / 120, 15 + h * (1 - item.Item1[i] / graphMax)));
                    canvas.Children.Add(line);
                }
                var values = Values(); var args = History.Current["profiles"][0][CurveHistory.AxisKey(a)];
                if ((string)args["mode"] == "natural" || (string)args["mode"] == "classic" && (string)args["Cap mode"] == "output")
                {
                    Handle("base", 0, values[0], "Base sensitivity · drag vertically");
                    Handle("offset", Math.Min(58, values[2]), values[0] + (values[2] < 1 ? graphMax * 0.08 : 0), "Input offset · drag horizontally");
                    int index = Math.Min(120, (int)((values[2] + 10) * 2)); Handle("acceleration", index / 2.0, after[index], ((string)args["mode"]=="natural"?"Decay rate":"Acceleration")+" · drag vertically");
                    Handle("cap", 55, after[110], ((string)args["mode"]=="natural"?"Limit":"Output cap")+" · drag vertically");
                }
            } catch (Exception ex) { status(ex.Message); }
        }
        private void Handle(string id, double speed, double multiplier, string tip)
        {
            var dot = new Ellipse { Width = 14, Height = 14, Fill = StudioUi.Ink("#F4D06F"), Stroke = StudioUi.Ink("#D4AF37"), StrokeThickness = 2, Cursor = Cursors.SizeAll, ToolTip = tip };
            Canvas.SetLeft(dot, 35 + (canvas.ActualWidth - 55) * speed / 60 - 7); Canvas.SetTop(dot, 15 + (canvas.ActualHeight - 45) * (1 - multiplier / graphMax) - 7);
            dot.MouseLeftButtonDown += (s, e) => { dragging = id; dragStart = e.GetPosition(canvas); dragValues = Values(); dragOriginal = (JObject)History.Current.DeepClone(); canvas.CaptureMouse(); e.Handled = true; }; canvas.Children.Add(dot);
        }
        private void Move(object sender, MouseEventArgs e)
        {
            if (History.Current == null) return;
            var point = e.GetPosition(canvas); double speed = Math.Max(0, Math.Min(60, (point.X - 35) / Math.Max(1, canvas.ActualWidth - 55) * 60));
            if (dragging != null) try {
                var values = (double[])dragValues.Clone(); double delta = (dragStart.Y - point.Y) / Math.Max(1, canvas.ActualHeight - 45);
                if (dragging == "offset") values[2] = Math.Max(0, Math.Min(60, speed));
                if (dragging == "base") values[0] = Math.Max(0.05, Math.Min(10, values[0] + delta * graphMax));
                if (dragging == "acceleration") values[1] = Math.Max(0, Math.Min(5, values[1] + delta * 1.5));
                if (dragging == "cap") values[3] = Math.Max(1.01, Math.Min(10, values[3] + delta * graphMax));
                double exponent=(double)dragOriginal["profiles"][0][CurveHistory.AxisKey(axis=="y"?"y":"x")]["exponentClassic"];
                History.Set(CurveHistory.EditControls(dragOriginal, axis, values, exponent), false); Changed();
            } catch (Exception ex) { status(ex.Message); }
            else {
                if (cachedBefore == null || cachedAfter == null) return; int i = Math.Min(120, (int)Math.Round(speed * 2));
                bool normalized = (double)History.Current["defaultDeviceConfig"]["DPI (normalizes input speed unit: counts/ms -> in/s)"] > 0;
                inspect.Text = (axis == "y" ? "Y" : "X / whole") + " · speed " + speed.ToString("0.0") + (normalized ? " in/s" : " counts/ms") + " · current " + cachedBefore[i].ToString("0.000") + "× · preview " + cachedAfter[i].ToString("0.000") + "×";
            }
        }
        private void EndDrag() { if (dragging == null) return; History.CommitDrag(dragOriginal); dragging = null; canvas.ReleaseMouseCapture(); Refresh(); }
    }
}
