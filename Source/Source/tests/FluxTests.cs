using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using KaidenFlux;
using System.IO;
using grapher.AI;

class FluxTests
{
    static int passed;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); passed++; }
    static void Reject(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception("Accepted invalid input: " + name); }
    static void Main(string[] args)
    {
        string prefs=Path.Combine(Path.GetTempPath(),"kaiden-setup-test-"+Guid.NewGuid().ToString("N")+".json");
        try {
            Check(!FluxState.LoadFrom(prefs).Completed,"missing setup starts with safe defaults");
            File.WriteAllText(prefs,"{broken");
            Check(FluxState.LoadFrom(prefs).LoadWarning!=null && File.ReadAllText(prefs)=="{broken","corrupt setup reports recovery without overwriting the file");
            File.WriteAllText(prefs,"{\"Schema\":99}");
            Check(FluxState.LoadFrom(prefs).LoadWarning!=null,"unsupported setup versions do not crash startup");
            File.WriteAllText(prefs,"{\"Schema\":1,\"Dpi\":800}");
            Check(FluxState.LoadFrom(prefs).Dpi==800,"valid existing setup preferences remain compatible");
            using(var locked=new FileStream(prefs,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                Check(FluxState.LoadFrom(prefs).LoadWarning!=null,"locked setup file is handled without startup failure");
            Check(File.ReadAllText(prefs).Contains("800"),"failed setup reads leave existing preferences unchanged");
        } finally { if(File.Exists(prefs))File.Delete(prefs); }
        var original = JObject.Parse(DriverConfig.GetDefault().ToJSON());
        var snapshot = original.DeepClone();
        var state = new FluxState { Dpi = 1600, Polling = 1000 };
        var configured = state.Configure(original);
        Check((int)configured["defaultDeviceConfig"]["DPI (normalizes input speed unit: counts/ms -> in/s)"] == 1600, "DPI reaches driver configuration");
        Check((int)configured["defaultDeviceConfig"]["Polling rate Hz (keep at 0 for automatic adjustment)"] == 1000, "polling reaches driver configuration");
        Check((double)configured["profiles"][0]["Output DPI"] == 1600, "first DPI normalization preserves 1x base sensitivity");
        Check(JToken.DeepEquals(original, snapshot), "setup does not mutate live baseline");
        Check((string)configured["profiles"][0]["Whole or horizontal accel parameters"]["mode"] == "noaccel", "keep-current preserves curve mode");
        configured["profiles"][0]["Output DPI"] = 3200;
        state.Dpi = 800;
        var rescaled = state.Configure(configured);
        Check((double)rescaled["profiles"][0]["Output DPI"] == 1600, "changing hardware DPI preserves existing 2x base sensitivity");
        foreach (string preset in new[] { "Quiet aim", "Balanced", "Flick boost" })
        {
            state.Starter = preset;
            var next = state.Configure(original);
            Check(FluxState.ValidateConfig(next) != null, preset + " passes native validation");
        }
        state.Starter = "Keep current";
        state.GameSensitivity = 0.37; state.PadCm = 48;
        var metadata = state.Configure(original);
        Check((double)metadata["profiles"][0]["Output DPI"] == 800 && (double)state.Context()["gameSensitivity"] == 0.37, "game sensitivity stays AI context, not driver multiplier");
        state.Dpi = 0; Reject(() => state.Configure(original), "zero hardware DPI rejected"); state.Dpi = 1600;
        state.Polling = 9000; Reject(() => state.Validate(), "invalid polling rate rejected"); state.Polling = 0;
        state.GameSensitivity = double.NaN; Reject(() => state.Validate(), "non-finite game sensitivity rejected"); state.GameSensitivity = 0.14;
        state.PadCm = 2; Reject(() => state.Validate(), "invalid pad width rejected"); state.PadCm = 30;
        state.Style = "unknown"; Reject(() => state.Validate(), "invalid aiming style rejected"); state.Style = "Arm";
        var filters = FluxInstaller.BuildFilters(new[] { "vendorfilter", "mouclass", "otherfilter" });
        Check(filters.SequenceEqual(new[] { "rawaccel", "vendorfilter", "mouclass", "otherfilter" }), "installer preserves unrelated mouse filters and their order");
        Check(FluxInstaller.BuildFilters(new[] { "rawaccel", "mouclass", "RAWACCEL" }).Count(s => s.Equals("rawaccel", StringComparison.OrdinalIgnoreCase)) == 1, "installer avoids duplicate mouse filter entries");
        Reject(() => FluxInstaller.BuildFilters(new[] { "vendorfilter" }), "installer refuses an unverifiable mouse class configuration");
        Reject(() => FluxInstaller.BuildFilters(null), "installer refuses missing mouse filter configuration");
        Check(FluxInstaller.Payload().Length == 57032, "embedded signed driver bytes pass pinned SHA-256 verification");
        Check(!FluxState.Friendly("Raw Accel / rawaccel / RawAccel").Contains("Raw"), "backend errors use product branding");
        string directory = Path.GetFullPath(args[0]);
        string backup = Path.Combine(directory, "previous.json");
        string failure = Path.Combine(directory, "fail.marker");
        File.WriteAllText(backup, "old successful backup"); File.WriteAllText(failure, "");
        var bridge = new WriterBridge(() => (JObject)original.DeepClone(), directory, backup);
        Reject(() => bridge.ApplyAsync(original, configured, true).GetAwaiter().GetResult(), "writer failure is reported");
        Check(File.ReadAllText(backup) == "old successful backup", "failed apply preserves the last successful undo backup");
        File.Delete(failure);
        bridge.ApplyAsync(original, configured, true).GetAwaiter().GetResult();
        Check(JToken.DeepEquals(JObject.Parse(File.ReadAllText(backup)), original), "successful apply commits the previous live configuration as backup");
        Check(!Directory.GetFiles(directory).Any(f => Path.GetFileName(f).StartsWith("proposal-") || Path.GetFileName(f).Contains(".pending-")), "staged proposal and backup files are cleaned up");
        Reject(() => bridge.ApplyAsync(configured, original, true).GetAwaiter().GetResult(), "stale proposals are rejected before writer launch");
        Console.WriteLine(passed + " Flux checks passed. No installation, driver writes, API calls, or preference writes performed.");
    }
}
