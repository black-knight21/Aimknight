using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace writer
{
    internal static class Program
    {
        // --validate never contacts the driver. --console never opens dialogs.
        private static int Main(string[] args)
        {
            bool console = args.Contains("--console");
            bool validate = args.Contains("--validate");
            var paths = args.Where(a => a != "--console" && a != "--validate").ToArray();
            try
            {
                if (paths.Length != 1)
                    throw new ArgumentException("Usage: writer.exe [--console] [--validate] <settings.json>");
                var result = DriverConfig.Convert(File.ReadAllText(paths[0]));
                if (result.Item2 != null) throw new ArgumentException("Bad settings: " + result.Item2);
                if (!validate)
                {
                    VersionHelper.ValidOrThrow();
                    result.Item1.Activate();
                }
                if (console) Console.WriteLine(validate ? "Settings are valid; driver was not changed." : "Settings applied.");
                return 0;
            }
            catch (Exception ex)
            {
                if (console) Console.Error.WriteLine(ex.Message);
                else MessageBox.Show(ex.Message, "Kaiden Accel writer");
                return 1;
            }
        }
    }
}
