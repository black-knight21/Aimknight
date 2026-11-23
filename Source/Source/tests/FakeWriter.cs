using System;
using System.IO;
class FakeWriter
{
    static int Main(string[] args)
    {
        if (File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fail.marker"))) { Console.Error.WriteLine("Simulated writer failure."); return 1; }
        Console.WriteLine("Mock writer completed; no driver access."); return 0;
    }
}
