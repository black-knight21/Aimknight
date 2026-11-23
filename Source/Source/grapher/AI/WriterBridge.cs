using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace grapher.AI
{
    internal sealed class WriterBridge
    {
        private readonly Func<JObject> activeReader;
        private readonly string directory;
        internal string BackupPath { get; }
        internal WriterBridge(Func<JObject> activeReader = null, string directory = null, string backupPath = null)
        {
            this.activeReader = activeReader ?? (() => JObject.Parse(DriverConfig.GetActive().ToJSON()));
            this.directory = directory ?? AppDomain.CurrentDomain.BaseDirectory;
            BackupPath = backupPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaidenFlux", "previous-settings.json");
        }

        public async Task ApplyAsync(JObject expected, JObject proposal, bool backup)
        {
            var writer = Path.Combine(directory, "writer.exe");
            if (!File.Exists(writer)) throw new FileNotFoundException("Extract the entire Kaiden Flux package before opening the app.");
            var result = DriverConfig.Convert(proposal.ToString());
            if (result.Item2 != null) throw new ArgumentException(result.Item2);
            var current = activeReader();
            if (!JToken.DeepEquals(expected, current))
                throw new InvalidOperationException("Driver settings changed since this proposal. Generate a new proposal before applying.");
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath));
            var stagedBackup = BackupPath + ".pending-" + Guid.NewGuid().ToString("N");
            var staged = Path.Combine(Path.GetDirectoryName(BackupPath), "proposal-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
              if (backup) File.WriteAllText(stagedBackup, current.ToString());
              File.WriteAllText(staged, proposal.ToString());
              foreach (bool validateOnly in new[] { true, false })
              {
                if (!validateOnly && !JToken.DeepEquals(expected, activeReader()))
                    throw new InvalidOperationException("Driver settings changed during validation. Preview again before applying.");
                using (var process = new Process { StartInfo = new ProcessStartInfo {
                    FileName = writer, Arguments = "--console " + (validateOnly ? "--validate " : "") + "\"" + staged + "\"",
                    WorkingDirectory = directory,
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                } })
                {
                    process.Start();
                    var stderr = process.StandardError.ReadToEndAsync();
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    // Do not kill a writer during activation; await its definite outcome.
                    await Task.Run(() => process.WaitForExit());
                    await stdout;
                    var error = await stderr;
                    if (process.ExitCode != 0) throw new InvalidOperationException((validateOnly ? "Settings validation failed: " : "Writer failed: ") + error);
                }
              }
                if (backup)
                {
                    try { if (File.Exists(BackupPath)) File.Replace(stagedBackup, BackupPath, null); else File.Move(stagedBackup, BackupPath); }
                    catch (IOException ex) { throw new IOException("The curve was applied, but its undo backup could not be saved. " + ex.Message, ex); }
                }
            }
            finally {
                try { File.Delete(staged); File.Delete(stagedBackup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
