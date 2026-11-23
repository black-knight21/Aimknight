using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KaidenFlux
{
    internal sealed class FluxState
    {
        public int Schema = 1;
        public bool Completed, PendingApply, DriverInstalled;
        public int Dpi = 1600, Polling = 0;
        public double GameSensitivity = 0.14, PadCm = 30;
        public string Mouse = "My mouse", Style = "Arm", Starter = "Keep current";
        public string SelectedGameProfileId;
        [JsonIgnore] internal string LoadWarning;
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaidenFlux");
        public static string FilePath => Path.Combine(Folder, "setup.json");
        internal static FluxState Load()
            => LoadFrom(FilePath);
        internal static FluxState LoadFrom(string path)
        {
            if (!File.Exists(path)) return new FluxState();
            try { var loaded = JsonConvert.DeserializeObject<FluxState>(File.ReadAllText(path)) ?? throw new ArgumentException("Empty settings file."); loaded.Validate(); return loaded; }
            catch (Exception ex) when(ex is JsonException || ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException) {
                return new FluxState { LoadWarning="Saved setup could not be read. Defaults are shown; review Setup before saving. The existing file has not been changed." };
            }
        }
        internal void Save()
        {
            Validate();
            Directory.CreateDirectory(Folder);
            string staged = FilePath + "." + Guid.NewGuid().ToString("N") + ".new";
            try {
                File.WriteAllText(staged, JsonConvert.SerializeObject(this, Formatting.Indented));
                if (File.Exists(FilePath)) File.Replace(staged, FilePath, null); else File.Move(staged, FilePath);
            } finally { if(File.Exists(staged))File.Delete(staged); }
        }
        internal JObject Context() => JObject.FromObject(new { mouse = Mouse, hardwareDpi = Dpi, pollingRateHz = Polling,
            gameSensitivity = GameSensitivity, padWidthCm = PadCm, aimingStyle = Style });

        internal JObject Configure(JObject current)
        {
            Validate();
            var next = (JObject)current.DeepClone();
            var config = (JObject)next["defaultDeviceConfig"];
            double oldDpi = (double)config["DPI (normalizes input speed unit: counts/ms -> in/s)"];
            double baseMultiplier = (double)next["profiles"][0]["Output DPI"] / (oldDpi > 0 ? oldDpi : 1000);
            config["DPI (normalizes input speed unit: counts/ms -> in/s)"] = Dpi;
            config["Polling rate Hz (keep at 0 for automatic adjustment)"] = Polling;
            next["profiles"][0]["Output DPI"] = baseMultiplier * Dpi;
            // Existing per-device overrides are preserved. The overview tells users when they exist.
            if (Starter != "Keep current") ApplyPreset(next, Starter);
            ValidateConfig(next);
            return next;
        }
        internal void Validate()
        {
            if(Schema!=1)throw new ArgumentException("Unsupported setup file version.");
            if (Dpi < 50 || Dpi > 100000) throw new ArgumentException("Enter a DPI between 50 and 100,000.");
            if (Polling < 0 || Polling > 8000) throw new ArgumentException("Enter 0 for automatic polling, or a rate up to 8,000 Hz.");
            if (double.IsNaN(GameSensitivity) || double.IsInfinity(GameSensitivity) || GameSensitivity <= 0 || GameSensitivity > 100)
                throw new ArgumentException("Enter a positive in-game sensitivity up to 100.");
            if (double.IsNaN(PadCm) || double.IsInfinity(PadCm) || PadCm < 5 || PadCm > 200)
                throw new ArgumentException("Enter a pad width between 5 and 200 cm.");
            if (string.IsNullOrWhiteSpace(Mouse) || Mouse.Length > 120) throw new ArgumentException("Enter a short mouse name.");
            if (Style != "Arm" && Style != "Wrist" && Style != "Hybrid") throw new ArgumentException("Select your aiming style.");
            if (Starter != "Keep current" && Starter != "Quiet aim" && Starter != "Balanced" && Starter != "Flick boost")
                throw new ArgumentException("Choose a starter curve.");
        }
        internal static void ApplyPreset(JObject config, string name)
        {
            if (name != "Quiet aim" && name != "Balanced" && name != "Flick boost") throw new ArgumentException("Unknown preset.");
            var profile = config["profiles"][0];
            foreach (var key in new[] { "Whole or horizontal accel parameters", "Vertical accel parameters" })
            {
                var args = profile[key];
                args["mode"] = "classic";
                args["inputOffset"] = 2.0;
                args["acceleration"] = name == "Quiet aim" ? 0.04 : name == "Balanced" ? 0.12 : 0.3;
                args["exponentClassic"] = 2.0;
                args["Cap mode"] = "output";
                args["Cap / Jump"]["y"] = name == "Quiet aim" ? 1.3 : name == "Balanced" ? 1.65 : 2.0;
            }
        }
        internal static DriverConfig ValidateConfig(JObject config)
        {
            var converted = DriverConfig.Convert(config.ToString());
            if (converted.Item2 != null) throw new ArgumentException(converted.Item2);
            return converted.Item1;
        }
        internal static string Friendly(string message) => message.Replace("Raw Accel", "Kaiden Flux").Replace("RawAccel", "Kaiden Flux").Replace("rawaccel", "Kaiden Flux");
    }
}
