using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

class SetupTests
{
    static string Hash(string file)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(file)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }
    static void Main(string[] args)
    {
        var assembly = Assembly.LoadFrom(args[0]);
        var type = assembly.GetType("grapher.DriverSetupPayload", true);
        var extract = type.GetMethod("Extract", BindingFlags.Static | BindingFlags.NonPublic);
        var cleanup = type.GetMethod("CleanUp", BindingFlags.Static | BindingFlags.NonPublic);
        string root = Path.Combine(Path.GetFullPath(args[1]), "setup-test-" + Guid.NewGuid().ToString("N"));
        string installer = (string)extract.Invoke(null, new object[] { root });
        string driver = Path.Combine(root, "driver", "rawaccel.sys");
        if (Hash(installer) != "B3B4948DE9470A5727390ED016507B59D2C628463E42AAF1B75E906EE04F58A9") throw new Exception("Installer payload changed.");
        if (Hash(driver) != "8A62C4DEEF2774B43A7363B352EDA79897533A1080C9C26FFEFF0559E43358D7") throw new Exception("Driver payload changed.");
        Console.WriteLine("PASS: installer and driver are embedded and extract byte-for-byte.");
        bool rejected = false;
        try { extract.Invoke(null, new object[] { root }); }
        catch (TargetInvocationException ex) { rejected = ex.InnerException is IOException; }
        if (!rejected) throw new Exception("Existing payload files were overwritten.");
        Console.WriteLine("PASS: extraction refuses to overwrite existing files.");
        string sentinel = Path.Combine(root, "unrelated.txt");
        File.WriteAllText(sentinel, "keep");
        cleanup.Invoke(null, new object[] { root });
        if (File.Exists(installer) || File.Exists(driver) || !File.Exists(sentinel)) throw new Exception("Cleanup was incorrect.");
        Console.WriteLine("PASS: cleanup removes only known setup files.");
        File.Delete(sentinel); Directory.Delete(root, false);
        Console.WriteLine("Setup tests passed. Installer was not launched.");
    }
}
