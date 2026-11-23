using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using KaidenFlux;
using grapher.AI;

class FoundationTests
{
    static int passed;
    static void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS: " + name); passed++; }
    static void Reject(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception("Accepted invalid input: " + name); }
    static void Near(double actual, double expected, string name) => Check(Math.Abs(actual - expected) < 0.000001, name);
    static void Main(string[] args)
    {
        string folder = Path.Combine(Path.GetFullPath(args[0]), "profiles-test-" + Guid.NewGuid().ToString("N"));
        var store = new GameProfileStore(folder);
        string memoryPath=Path.Combine(folder,"memory-test","history.json");var memory=new LocalTuningMemory(memoryPath);
        memory.Add("proposal","1600 DPI, 0.14, slower aim","Try a bounded flick cap","x.acceleration: 0.1");
        Check(new LocalTuningMemory(memoryPath).Entries.Count==1,"local AI history survives app restart");
        Check(new LocalTuningMemory(memoryPath).Context().ToString().Contains("0.14"),"remembered preferences are available as bounded coaching context");
        const string fakeKey="AIza123456789012345678901234567890test";
        memory.Add("proposal","secret "+fakeKey,fakeKey,"",fakeKey);
        Check(!File.ReadAllText(memoryPath).Contains(fakeKey),"API keys are redacted from local tuning history");
        memory.SetEnabled(false);memory.Add("proposal","not stored","","");
        Check(memory.Entries.Count==2 && memory.Context().Count==0 && !new LocalTuningMemory(memoryPath).Enabled,"memory opt-out persists and suppresses storage and context");
        memory.SetEnabled(true);for(int i=0;i<55;i++)memory.Add("proposal","request "+i,"","");
        Check(memory.Entries.Count==50 && memory.Context().Count==5,"local history and outbound memory context are bounded");
        memory.Clear();Check(new LocalTuningMemory(memoryPath).Entries.Count==0,"clear history persists across restart");
        File.WriteAllText(memoryPath,"{broken");Check(!new LocalTuningMemory(memoryPath).Enabled && new LocalTuningMemory(memoryPath).Warning!=null,"corrupt history safely disables memory without crashing");
        string keyPath=Path.Combine(folder,"key.dat");
        var aes=System.Security.Cryptography.Aes.Create();aes.GenerateKey();aes.GenerateIV();
        Func<byte[],byte[]> encrypt=data=>{using(var crypto=aes.CreateEncryptor())return crypto.TransformFinalBlock(data,0,data.Length);};
        Func<byte[],byte[]> decrypt=data=>{using(var crypto=aes.CreateDecryptor())return crypto.TransformFinalBlock(data,0,data.Length);};
        var keys=new SecureApiKeyStore(keyPath,encrypt,decrypt);keys.Save(fakeKey);
        Check(keys.Read()==fakeKey,"encrypted credential storage roundtrips using a test encryption provider");
        Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(keyPath)).Contains(fakeKey),"saved API credential is encrypted rather than plaintext");
        byte[] preserved=File.ReadAllBytes(keyPath);
        var failingKeys=new SecureApiKeyStore(keyPath,data=>{throw new System.Security.Cryptography.CryptographicException("simulated account encryption failure");},decrypt);
        Reject(()=>failingKeys.Save("replacement-key"),"encryption failure never writes plaintext fallback");
        Check(File.ReadAllBytes(keyPath).SequenceEqual(preserved),"encryption failure preserves the prior saved credential");
        keys.Forget();Check(!File.Exists(keyPath) && keys.Read()==null,"forget key removes the encrypted credential");
        File.WriteAllBytes(keyPath,new byte[]{1,2,3});Check(keys.Read()==null && keys.Warning!=null,"unreadable encrypted key is handled without startup failure");
        aes.Dispose();
        try{var nativeKeys=new SecureApiKeyStore(Path.Combine(folder,"dpapi-key.dat"));nativeKeys.Save(fakeKey);Check(nativeKeys.Read()==fakeKey,"native Windows DPAPI roundtrip succeeds");nativeKeys.Forget();}
        catch(System.Security.Cryptography.CryptographicException){Console.WriteLine("SKIP: native Windows DPAPI roundtrip unavailable in this restricted user-profile context; no plaintext fallback.");}
        var baseline = JObject.Parse(DriverConfig.GetDefault().ToJSON());
        var profile = new GameProfile { Name = "Valorant arm", Curve = (JObject)baseline.DeepClone(), AdsSensitivity = 1.2, Notes = "Test notes" };
        string id = profile.Id; DateTime created = profile.CreatedUtc;
        store.Save(profile);
        int skipped; var loaded = store.LoadAll(out skipped);
        Check(loaded.Count == 1 && skipped == 0 && loaded[0].Id == id, "game profile persists with a stable identifier");
        Check(JToken.DeepEquals(loaded[0].Curve, baseline) && loaded[0].AdsSensitivity == 1.2, "preferred curve and optional ADS value round-trip");
        profile.Name = "Renamed profile"; store.Save(profile);
        loaded = store.LoadAll(out skipped);
        Check(loaded.Count == 1 && loaded[0].Name == profile.Name && loaded[0].Id == id && loaded[0].CreatedUtc == created, "renaming preserves ID and creation date");
        Check(!Directory.GetFiles(folder).Any(f => f.EndsWith(".tmp")), "atomic profile saves leave no staged files");
        var copy = store.Duplicate(profile);
        Check(copy.Id != profile.Id && store.LoadAll(out skipped).Count == 2, "duplicate creates a separate persisted profile");
        copy.Curve["profiles"][0]["Output DPI"] = 1300;
        Check((double)profile.Curve["profiles"][0]["Output DPI"] == 1000, "duplicated curve data is independent");
        File.WriteAllText(Path.Combine(folder, "corrupt.json"), "{bad");
        Check(store.LoadAll(out skipped).Count == 2 && skipped == 1, "corrupted optional profile files are skipped");
        var future = new GameProfile { Schema = 99 }; File.WriteAllText(Path.Combine(folder, future.Id + ".json"), JsonConvert.SerializeObject(future));
        Check(store.LoadAll(out skipped).Count == 2 && skipped == 2, "unknown profile versions do not crash loading");
        store.Delete(copy.Id);
        Check(store.LoadAll(out skipped).Count == 1 && Directory.GetFiles(Path.Combine(folder, "trash")).Length == 1, "delete moves a profile to recoverable local trash");
        Reject(() => store.Delete("../../outside"), "profile IDs cannot escape the storage folder");
        Reject(() => store.Save(new GameProfile { Sensitivity = double.NaN }), "non-finite profile sensitivity rejected");
        Reject(() => store.Save(new GameProfile { GameId = "invented" }), "unknown game IDs rejected");
        Reject(() => store.Save(new GameProfile { AdsSensitivity = -1 }), "negative optional ADS value rejected");
        var source = (JObject)baseline.DeepClone(); source["defaultDeviceConfig"]["DPI (normalizes input speed unit: counts/ms -> in/s)"] = 800;
        source["profiles"][0]["Output DPI"] = 1600; source["profiles"][0]["name"] = "saved-name";
        source["profiles"][0]["Stretches domain for horizontal vs vertical inputs"]["x"] = 1.2;
        var current = (JObject)baseline.DeepClone(); current["defaultDeviceConfig"]["DPI (normalizes input speed unit: counts/ms -> in/s)"] = 1600;
        var preview = TuningProposal.LoadCurve(current, source);
        Near((double)preview["profiles"][0]["Output DPI"], 3200, "loading a saved curve preserves its effective base multiplier at a new DPI");
        Check((string)preview["profiles"][0]["name"] == "default", "loading a curve preserves the destination profile identity");
        Check(JToken.DeepEquals(preview["profiles"][0]["Stretches domain for horizontal vs vertical inputs"], current["profiles"][0]["Stretches domain for horizontal vs vertical inputs"]), "loading a curve preserves protected directional fields");
        Check(JToken.DeepEquals(preview["defaultDeviceConfig"], current["defaultDeviceConfig"]) && JToken.DeepEquals(preview["devices"], current["devices"]), "loading a curve preserves current device configuration");
        var sourceResult = SensitivityMath.Calculate(800, 1, 0.022, 1, 30);
        Near(sourceResult.Cm360, 51.95454545454545, "Source reference cm/360 formula");
        Near(sourceResult.Inches360, sourceResult.Cm360 / 2.54, "centimeters and inches agree");
        Near(sourceResult.Edpi, 800, "within-game eDPI calculation");
        Near(sourceResult.PadDegrees, 30 / sourceResult.Cm360 * 360, "pad distance comparison");
        Near(SensitivityMath.Convert(1, 0.06996, 0.022, 800, 800), 3.18, "Valorant reference conversion uses its own yaw scale");
        Near(SensitivityMath.Convert(1.5, 0.022, 0.0066, 800, 800), 5, "Overwatch conversion uses a different yaw scale");
        double converted = SensitivityMath.Convert(0.14, 0.06996, 0.022, 1600, 800);
        Near(SensitivityMath.Calculate(1600, 0.14, 0.06996, 1, 30).Cm360, SensitivityMath.Calculate(800, converted, 0.022, 1, 30).Cm360, "conversion preserves physical turn distance across different DPIs");
        Near(SensitivityMath.Calculate(800, 1, 0.022, 2, 30).Cm360, sourceResult.Cm360 / 2, "accelerated multiplier changes turn distance rather than game sensitivity");
        Reject(() => SensitivityMath.Calculate(0, 1, 0.022, 1, 30), "zero DPI rejected by calculator");
        Reject(() => SensitivityMath.Convert(1, double.NaN, 0.022, 800, 800), "invalid conversion yaw rejected");
        Reject(() => new GameProfile { GameId = "custom" }.Yaw.ToString(), "custom games require an explicit yaw for calculations");
        Console.WriteLine(passed + " foundation checks passed. Test files: " + folder);
    }
}
