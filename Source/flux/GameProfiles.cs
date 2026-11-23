using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KaidenFlux
{
    internal sealed class GameDefinition
    {
        public string Id, Name;
        public double? Yaw;
        public override string ToString() => Name;
        internal static readonly GameDefinition[] Games = {
            new GameDefinition { Id = "valorant", Name = "Valorant", Yaw = 0.06996 },
            new GameDefinition { Id = "cs2", Name = "Counter-Strike 2", Yaw = 0.022 },
            new GameDefinition { Id = "apex", Name = "Apex Legends", Yaw = 0.022 },
            new GameDefinition { Id = "overwatch2", Name = "Overwatch 2", Yaw = 0.0066 },
            new GameDefinition { Id = "custom", Name = "Custom game", Yaw = null }
        };
        internal static GameDefinition Find(string id) => Games.FirstOrDefault(g => g.Id == id) ?? throw new ArgumentException("Choose a supported game or Custom game.");
    }
    internal sealed class GameProfile
    {
        public int Schema = 1;
        public string Id = Guid.NewGuid().ToString("N"), Name = "My profile", GameId = "valorant", Notes = "";
        public double Sensitivity = 0.14;
        public double? AdsSensitivity, CustomYaw;
        public DateTime CreatedUtc = DateTime.UtcNow, ModifiedUtc = DateTime.UtcNow;
        public JObject Curve;
        internal double Yaw => CustomYaw ?? GameDefinition.Find(GameId).Yaw ?? throw new ArgumentException("Enter your custom game's yaw value to calculate distance.");
        internal void Validate()
        {
            Guid parsed;
            if (!Guid.TryParseExact(Id, "N", out parsed)) throw new ArgumentException("Invalid profile identifier.");
            if (Schema != 1) throw new ArgumentException("Unsupported profile file version.");
            if (string.IsNullOrWhiteSpace(Name) || Name.Length > 80) throw new ArgumentException("Use a profile name between 1 and 80 characters.");
            GameDefinition.Find(GameId);
            SensitivityMath.Positive(Sensitivity, "Game sensitivity");
            if (AdsSensitivity.HasValue) SensitivityMath.Positive(AdsSensitivity.Value, "ADS/scope sensitivity");
            if (CustomYaw.HasValue) SensitivityMath.Positive(CustomYaw.Value, "Custom yaw");
            if (Notes == null || Notes.Length > 2000) throw new ArgumentException("Keep notes under 2,000 characters.");
            if (Curve != null) FluxState.ValidateConfig(Curve);
        }
        internal GameProfile Clone() => JsonConvert.DeserializeObject<GameProfile>(JsonConvert.SerializeObject(this));
        public override string ToString() => Name + "  ·  " + GameDefinition.Find(GameId).Name;
    }
    internal static class AtomicJson
    {
        internal static void Write(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                File.WriteAllText(staged, JsonConvert.SerializeObject(value, Formatting.Indented));
                if (File.Exists(path)) File.Replace(staged, path, null); else File.Move(staged, path);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }
    }
    internal sealed class GameProfileStore
    {
        private readonly string folder;
        internal GameProfileStore(string folder) { this.folder = Path.GetFullPath(folder); }
        private string FilePath(string id)
        {
            Guid guid;
            if (!Guid.TryParseExact(id, "N", out guid)) throw new ArgumentException("Invalid profile identifier.");
            return Path.Combine(folder, guid.ToString("N") + ".json");
        }
        internal List<GameProfile> LoadAll(out int skipped)
        {
            skipped = 0; var result = new List<GameProfile>();
            if (!Directory.Exists(folder)) return result;
            string[] paths;
            try { paths = Directory.GetFiles(folder, "*.json"); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { skipped = 1; return result; }
            foreach (string path in paths)
                try {
                    if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException();
                    var profile = JsonConvert.DeserializeObject<GameProfile>(File.ReadAllText(path));
                    if (profile == null || !string.Equals(Path.GetFileName(path), Path.GetFileName(FilePath(profile.Id)), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                    profile.Validate(); result.Add(profile);
                } catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is IOException || ex is UnauthorizedAccessException) { skipped++; }
            return result.OrderByDescending(p => p.ModifiedUtc).ToList();
        }
        internal void Save(GameProfile profile)
        {
            profile.Validate(); profile.ModifiedUtc = DateTime.UtcNow;
            AtomicJson.Write(FilePath(profile.Id), profile);
        }
        internal GameProfile Duplicate(GameProfile source)
        {
            var copy = source.Clone(); copy.Id = Guid.NewGuid().ToString("N");
            copy.Name = source.Name.Substring(0, Math.Min(source.Name.Length, 73)) + " copy";
            copy.CreatedUtc = copy.ModifiedUtc = DateTime.UtcNow; Save(copy); return copy;
        }
        internal void Delete(string id)
        {
            string source = FilePath(id);
            string trash = Path.Combine(folder, "trash"); Directory.CreateDirectory(trash);
            if (File.Exists(source)) File.Move(source, Path.Combine(trash, id + "-" + Guid.NewGuid().ToString("N") + ".json"));
        }
    }
    internal sealed class SensitivityResult
    {
        public double Edpi, EffectiveEdpi, Inches360, Cm360, PadDegrees;
    }
    internal static class SensitivityMath
    {
        internal static void Positive(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0 || value > 1000000) throw new ArgumentException(name + " must be a finite positive number.");
        }
        internal static SensitivityResult Calculate(double dpi, double sensitivity, double yaw, double multiplier, double padCm)
        {
            Positive(dpi, "DPI"); Positive(sensitivity, "Sensitivity"); Positive(yaw, "Yaw"); Positive(multiplier, "Multiplier"); Positive(padCm, "Pad width");
            double inches = 360 / (dpi * sensitivity * yaw * multiplier);
            if (double.IsInfinity(inches) || double.IsNaN(inches) || inches <= 0) throw new ArgumentException("These inputs produce an invalid result.");
            return new SensitivityResult { Edpi = dpi * sensitivity, EffectiveEdpi = dpi * sensitivity * multiplier,
                Inches360 = inches, Cm360 = inches * 2.54, PadDegrees = padCm / (inches * 2.54) * 360 };
        }
        internal static double Convert(double sensitivity, double fromYaw, double toYaw, double fromDpi, double toDpi)
        {
            Positive(sensitivity, "Sensitivity"); Positive(fromYaw, "Source yaw"); Positive(toYaw, "Target yaw"); Positive(fromDpi, "Source DPI"); Positive(toDpi, "Target DPI");
            double result = sensitivity * fromYaw / toYaw * fromDpi / toDpi;
            Positive(result, "Converted sensitivity"); return result;
        }
    }
}
