using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using KaidenFlux;
using KaidenFlux.Hardware;
using grapher.AI;

class StudioTests
{
    static int passed;
    static void Check(bool value, string name) { if (!value) throw new Exception("FAILED: " + name); passed++; Console.WriteLine("PASS: " + name); }
    static void Reject(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception("Accepted invalid input: " + name); }
    static void Near(double value, double expected, string name) => Check(Math.Abs(value - expected) < 0.00001, name);
    [STAThread]
    static void Main(string[] args)
    {
        var config = new FluxState { Starter = "Flick boost" }.Configure(JObject.Parse(DriverConfig.GetDefault().ToJSON()));
        var snapshot = config.DeepClone();
        var edited = CurveHistory.Edit(config, "x", 1.1, 0.15, 3, 1.7);
        Check(JToken.DeepEquals(config, snapshot), "curve editing leaves the active snapshot immutable");
        Near((double)edited["profiles"][0]["Output DPI"], 1760, "precise sensitivity maps through physical DPI");
        Check(JToken.DeepEquals(edited["profiles"][0]["Vertical accel parameters"], config["profiles"][0]["Vertical accel parameters"]), "X edits preserve Y acceleration parameters");
        Check(JToken.DeepEquals(edited["defaultDeviceConfig"], config["defaultDeviceConfig"]) && JToken.DeepEquals(edited["devices"], config["devices"]), "editor preserves device assignments and hardware defaults");
        var linked = CurveHistory.Edit(config, "both", 1, 0.12, 4, 1.8);
        Near((double)linked["profiles"][0]["Vertical accel parameters"]["acceleration"], 0.12, "linked editing explicitly updates both axes");
        var y = CurveHistory.Edit(config, "y", 1, 0.1, 5, 1.5);
        Check(JToken.DeepEquals(y["profiles"][0]["Whole or horizontal accel parameters"], config["profiles"][0]["Whole or horizontal accel parameters"]), "Y edits preserve whole/horizontal parameters");
        var history = new CurveHistory(); history.Load(config, config); history.Set(edited);
        Check(history.Undo() && JToken.DeepEquals(history.Current, config), "editor Undo restores the previous preview");
        Check(history.Redo() && JToken.DeepEquals(history.Current, edited), "editor Redo restores the edited preview");
        history.Set(linked, false); history.CommitDrag(edited);
        Check(history.Undo() && JToken.DeepEquals(history.Current, edited), "a drag gesture commits as one undo step");
        history.Load(config, config); Check(!history.Undo() && !history.Redo(), "discard/load clears edit history without driver writes");
        var samples = CurveHistory.Samples(config, "x");
        Check(samples.Length == 121 && samples.All(v => !double.IsInfinity(v) && !double.IsNaN(v)), "curve graph samples the native engine");
        Check(CurveHistory.Samples(config, "y").Length == 121, "vertical overlay uses the native vertical output");
        Reject(() => CurveHistory.Edit(config, "invented", 1, 0.1, 2, 2), "unsupported editor axes rejected");
        Reject(() => CurveHistory.Edit(config, "x", double.NaN, 0.1, 2, 2), "non-finite editor inputs rejected");
        Reject(() => CurveHistory.Edit(config, "x", 1, -1, 2, 2), "invalid Classic parameters rejected");
        var off = JObject.Parse(DriverConfig.GetDefault().ToJSON()); Reject(() => CurveHistory.Edit(off, "x", 1, 0.1, 2, 2), "editor does not silently change unsupported modes");
        foreach(string mode in new[]{"Linear","Classic","Natural","Power","Jump","Synchronous","Off"}) {
            var changed=CurveHistory.SetMode(config,"x",mode);
            Check(CurveHistory.ModeName(changed["profiles"][0][CurveHistory.AxisKey("x")])==mode && CurveHistory.Samples(changed,"x").All(v=>!double.IsNaN(v)&&!double.IsInfinity(v)),mode+" selects a real native curve with finite graph output");
            Check(JToken.DeepEquals(changed["profiles"][0][CurveHistory.AxisKey("y")],config["profiles"][0][CurveHistory.AxisKey("y")]),mode+" mode selection preserves other axis");
        }
        var natural=CurveHistory.SetMode(config,"x","Natural");
        var naturalEdited=CurveHistory.EditControls(natural,"x",new[]{1.1,0.2,3.0,1.8},2);
        Near((double)naturalEdited["profiles"][0][CurveHistory.AxisKey("x")]["decayRate"],0.2,"Natural control maps to decay rate");
        Near((double)naturalEdited["profiles"][0][CurveHistory.AxisKey("x")]["limit"],1.8,"Natural control maps to limit instead of Classic cap");
        Reject(()=>CurveHistory.EditControls(natural,"both",new[]{1.0,0.2,3.0,1.8},2),"linked editing rejects mixed native curve modes");
        Reject(()=>CurveHistory.EditControls(natural,"x",new[]{1.0,0.2,3.0,1.0},2),"Natural singular limit is rejected before staging");
        var option=CurveHistory.SetOption(config,"x","Gain / Velocity",new JValue(false));
        Check(!(bool)option["profiles"][0][CurveHistory.AxisKey("x")]["Gain / Velocity"] && (bool)config["profiles"][0][CurveHistory.AxisKey("x")]["Gain / Velocity"],"explicit gain switch does not mutate original configuration");
        option=CurveHistory.SetOption(config,"x","Cap mode",new JValue("in_out"));
        Check((string)option["profiles"][0][CurveHistory.AxisKey("x")]["Cap mode"]=="in_out","both-cap control maps to native cap enum");
        option=CurveHistory.SetOption(config,"x","inputCap",new JValue(25));
        Near((double)option["profiles"][0][CurveHistory.AxisKey("x")]["Cap / Jump"]["x"],25,"input cap control maps to speed cap component");
        option=CurveHistory.SetOption(config,"x","whole",new JValue(false));
        Check(!(bool)option["profiles"][0]["Input speed calculation parameters"]["Whole/combined accel (set false for 'by component' mode)"],"explicit component switch enables separate native X/Y curves");
        Reject(()=>CurveHistory.SetOption(config,"x","rotation",new JValue(4)),"curve options cannot edit arbitrary protected fields");
        Reject(()=>CurveHistory.SetOption(config,"x","inputCap",new JValue(double.NaN)),"non-finite advanced curve options rejected");
        var power=CurveHistory.EditControls(CurveHistory.SetMode(config,"x","Power"),"x",new[]{1.0,1.0,0.0,2.0},0.5);
        Near((double)power["profiles"][0][CurveHistory.AxisKey("x")]["exponentPower"],0.5,"Power exponent maps independently from Classic");
        var sync=CurveHistory.EditControls(CurveHistory.SetMode(config,"x","Synchronous"),"x",new[]{1.0,1.0,5.0,1.8},0.7);
        Near((double)sync["profiles"][0][CurveHistory.AxisKey("x")]["smooth"],0.7,"Synchronous extra control maps to smoothing");
        var flick = new AimSession { Mode = "Flick", DurationSeconds = 30, Completed = true, TargetRadiusDip = 18, CurveHash = AimSession.Hash(config), ProfileName = "Test profile" };
        flick.Attempts.Add(new AimAttempt { AtSeconds = 1, AcquisitionMs = 100, ErrorDip = 2, Hit = true });
        flick.Attempts.Add(new AimAttempt { AtSeconds = 2, AcquisitionMs = 300, ErrorDip = 3, Hit = true });
        flick.Attempts.Add(new AimAttempt { AtSeconds = 3, AcquisitionMs = 200, ErrorDip = 30, Hit = false, MissDirection = "Overshoot" });
        flick.Attempts.Add(new AimAttempt { AtSeconds = 4, AcquisitionMs = 400, ErrorDip = 40, Hit = false, MissDirection = "Undershoot" });
        flick.Validate(); var summary = flick.Summary();
        Near((double)summary["accuracyPercent"], 50, "flick accuracy comes from measured hit/click counts");
        Near((double)summary["missRatePercent"], 50, "miss rate is measured separately");
        Near((double)summary["meanAcquisitionMs"], 200, "acquisition time averages successful targets only");
        Near((double)summary["acquisitionStdDevMs"], 100, "acquisition consistency uses real standard deviation");
        Check((int)summary["endpointOvershoots"] == 1 && (int)summary["endpointUndershoots"] == 1, "miss endpoint direction is kept separate from gameplay inference");
        var track = new AimSession { Mode = "Tracking", DurationSeconds = 30, Completed = true, TargetRadiusDip = 18, CurveHash = AimSession.Hash(config), ProfileName = "Test profile" };
        track.Tracking.Add(new TrackingSample { AtSeconds = 6, IntervalSeconds = 6, DistanceDip = 5, OnTarget = true });
        track.Tracking.Add(new TrackingSample { AtSeconds = 12, IntervalSeconds = 6, DistanceDip = 25 });
        track.Tracking.Add(new TrackingSample { AtSeconds = 30, IntervalSeconds = 18, DistanceDip = null });
        track.Validate(); var tracking = track.Summary();
        Near((double)tracking["trackingOnTargetPercent"], 20, "tracking accuracy is time-weighted and includes known outside time");
        Near((double)tracking["meanDeviationDip"], 15, "deviation uses only observed pointer distances");
        Near((double)tracking["deviationStdDevDip"], 10, "tracking stability is measured distance standard deviation");
        track.Tracking[2].Observed = false;
        Near((double)track.Summary()["trackingOnTargetPercent"], 50, "unobserved rendering gaps do not become invented misses");
        Near((double)track.Summary()["trackingCoveragePercent"], 40, "coverage exposes how much time was actually observed");
        var empty = new AimSession { Mode = "Micro", DurationSeconds = 2, TargetRadiusDip = 5, CurveHash = AimSession.Hash(config) };
        Check(empty.Summary()["accuracyPercent"].Type == JTokenType.Null && empty.Summary()["meanAcquisitionMs"].Type == JTokenType.Null, "empty sessions display unavailable metrics rather than fake scores");
        empty.Completed = true; Reject(() => empty.Validate(), "interrupted sessions cannot masquerade as complete 30-second tests"); empty.Completed = false;
        empty.Attempts.Add(new AimAttempt { AtSeconds = 1, AcquisitionMs = 100, ErrorDip = 20, Hit = true }); Reject(() => empty.Validate(), "inconsistent hit measurements rejected");
        Check(AimSession.Hash(config) == AimSession.Hash((JObject)config.DeepClone()) && AimSession.Hash(config) != AimSession.Hash(edited), "sessions associate with stable curve fingerprints");
        string folder = Path.Combine(Path.GetFullPath(args[0]), "aim-tests-" + Guid.NewGuid().ToString("N"));
        var store = new AimSessionStore(folder); store.Save(flick); store.Save(track);
        Check(store.Load().Count == 2, "versioned aim sessions persist atomically");
        File.WriteAllText(Path.Combine(folder, "bad.json"), "bad"); Check(store.Load().Count == 2, "corrupted optional session files are skipped");
        var explanation = new JObject { ["changes"] = new JArray(), ["modes"] = new JArray(), ["caps"] = new JArray(), ["explanation"] = "Only endpoint overshoots were measured; use a small preview adjustment." };
        Check(JToken.DeepEquals(TuningProposal.ApplyChanges(config, explanation), config) && TuningProposal.Explanation(explanation).Length > 0, "AI rationale is displayed separately and never serialized into driver settings");
        explanation["explanation"] = new string('x', 1201); Reject(() => TuningProposal.ApplyChanges(config, explanation), "oversized AI rationale rejected");
        Check(AttackSharkX11Protocol.IsCandidate(0x1D57, 0xFA60) && AttackSharkX11Protocol.IsCandidate(0x1D57, 0xFA55), "only documented USB family identifiers are candidates");
        Check(!AttackSharkX11Protocol.IsCandidate(0xFFFF, 0xFA60), "unrelated vendor devices are not treated as X11");
        Check(AttackSharkX11Protocol.Battery(new byte[] { 3, 0x55, 0x40, 1, 73 }, 5) == 73, "documented passive battery packets are parsed");
        Check(AttackSharkX11Protocol.Battery(new byte[] { 3, 0x55, 0x40, 1, 255 }, 5) == null, "out-of-range battery values are not displayed");
        Check(AttackSharkX11Protocol.Battery(new byte[] { 3, 0x55, 0x10, 1, 50 }, 5) == null, "DPI-button events are not mistaken for battery readings");
        Check(AttackSharkX11Protocol.Battery(new byte[] { 3, 0x55 }, 2) == null, "truncated HID packets are ignored");
        var device = new MouseDeviceState { Path = "hid#vid_1d57&pid_fa60&mi_02", VendorId = 0x1D57, ProductId = 0xFA60, UsagePage = 0xFF01, InputReportBytes = 8 };
        device.Capabilities.PassiveBattery = AttackSharkX11Protocol.CanListen(device);
        Check(AttackSharkX11Protocol.CanListen(new MouseDeviceState { Path = "hid#vid_1d57&pid_fa55&mi_02", VendorId = 0x1D57, ProductId = 0xFA55, UsagePage = 0x000A, Usage = 0, InputReportBytes = 5 }), "observed five-byte Windows status collection is eligible for passive reading only");
        Check(device.Capabilities.PassiveBattery && !device.Capabilities.HardwareWrites, "passive report capability never enables unverified writes");
        HardwareTests(device).GetAwaiter().GetResult();
        try {
            var actual = new WindowsHidMouseDevice(); var detected = actual.Enumerate();
            Console.WriteLine("Read-only Windows enumeration smoke check: " + detected.Count + " matching collection(s). This does not verify physical X11 communication.");
            if (args.Contains("--hardware-read")) {
                foreach (var d in detected) Console.WriteLine(d.Product + " | " + d.Connection + " | VID/PID " + d.VendorId.ToString("X4") + "/" + d.ProductId.ToString("X4") + " | descriptor " + d.Firmware.ToString("X4") + " | usage " + d.UsagePage.ToString("X4") + ":" + d.Usage.ToString("X4") + " | input bytes " + d.InputReportBytes + " | passive status " + d.Capabilities.PassiveBattery);
                using (var realService = new MouseHardwareService(actual)) {
                    realService.RefreshAsync().GetAwaiter().GetResult();
                    try { realService.ReadStatusAsync(CancellationToken.None).GetAwaiter().GetResult(); Console.WriteLine("Passive battery packet: " + (realService.BatteryPercent?.ToString() ?? "unavailable")); }
                    catch (Exception ex) { Console.WriteLine("Passive battery read unavailable: " + ex.Message); }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("Read-only enumeration unavailable in this environment: " + ex.Message); }
        Console.WriteLine(passed + " studio checks passed. No driver writes, HID writes, or live API calls.");
    }
    static async Task HardwareTests(MouseDeviceState device)
    {
        var fake = new FakeMouse(); fake.Devices.Add(device);
        using (var service = new MouseHardwareService(fake)) {
            await service.RefreshAsync(); await service.ReadStatusAsync(CancellationToken.None);
            Check(service.BatteryPercent == 42, "mocked status reads populate measured battery values");
            fake.Devices.Clear(); await service.RefreshAsync(); Check(service.BatteryPercent == null && service.Devices.Count == 0, "disconnect clears stale battery status");
            Reject(() => service.WriteDpiAsync(1600).GetAwaiter().GetResult(), "unverified hardware writes are rejected before device access");
            Reject(() => service.ReadStatusAsync(CancellationToken.None).GetAwaiter().GetResult(), "status reads fail safely when disconnected");
            fake.Devices.Add(device); await service.RefreshAsync(); fake.Read = token => { throw new IOException("simulated read error"); };
            Reject(() => service.ReadStatusAsync(CancellationToken.None).GetAwaiter().GetResult(), "failed hardware reads are surfaced");
            Check(service.BatteryPercent == null, "failed reads do not leave fabricated or stale battery values");
            fake.Read = async token => { await Task.Delay(-1, token); return null; };
            using (var cancel = new CancellationTokenSource()) { cancel.CancelAfter(10); try { await service.ReadStatusAsync(cancel.Token); throw new Exception("Read did not cancel"); } catch (OperationCanceledException) { Check(true, "pending hardware reads honor cancellation"); } }
            var result = new TaskCompletionSource<int?>(); fake.Read = token => result.Task;
            var read = service.ReadStatusAsync(CancellationToken.None); fake.Devices.Clear(); await service.RefreshAsync(); result.SetResult(80);
            try { await read; throw new Exception("Disconnected read accepted"); } catch (IOException) { Check(service.BatteryPercent == null, "late read results cannot repopulate a disconnected device"); }
        }
    }
    sealed class FakeMouse : IMouseDevice
    {
        internal readonly List<MouseDeviceState> Devices = new List<MouseDeviceState>();
        internal Func<CancellationToken, Task<int?>> Read = token => Task.FromResult((int?)42);
        public List<MouseDeviceState> Enumerate() => Devices.ToList();
        public Task<int?> ReadBatteryAsync(MouseDeviceState device, CancellationToken token) => Read(token);
    }
}
