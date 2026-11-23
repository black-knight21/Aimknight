using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using grapher.Models.Theming;

namespace grapher
{
    internal static class DriverSetupPayload
    {
        internal const string InstallerHash = "B3B4948DE9470A5727390ED016507B59D2C628463E42AAF1B75E906EE04F58A9";
        internal const string DriverHash = "8A62C4DEEF2774B43A7363B352EDA79897533A1080C9C26FFEFF0559E43358D7";

        internal static string Extract(string directory)
        {
            Directory.CreateDirectory(Path.Combine(directory, "driver"));
            ExtractFile("KaidenAccel.Setup.installer.exe", Path.Combine(directory, "installer.exe"), InstallerHash);
            ExtractFile("KaidenAccel.Setup.rawaccel.sys", Path.Combine(directory, "driver", "rawaccel.sys"), DriverHash);
            return Path.Combine(directory, "installer.exe");
        }

        private static void ExtractFile(string resource, string path, string expectedHash)
        {
            using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (input == null) throw new InvalidOperationException("The bundled driver setup is missing. Extract the complete Kaiden Accel package again.");
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
            }
            using (var sha = SHA256.Create())
            using (var file = File.OpenRead(path))
            {
                var actual = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                if (actual != expectedHash) throw new InvalidDataException("Bundled driver setup verification failed.");
            }
        }

        internal static void CleanUp(string directory)
        {
            // Only remove the two files we created; leave unexpected files intact.
            try {
                File.Delete(Path.Combine(directory, "installer.exe"));
                File.Delete(Path.Combine(directory, "driver", "rawaccel.sys"));
                Directory.Delete(Path.Combine(directory, "driver"), false);
                Directory.Delete(directory, false);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class DriverSetupForm : Form
    {
        private readonly Button install = new Button { Text = "Install included driver", AutoSize = true };
        private readonly Button retry = new Button { Text = "Check again", AutoSize = true };
        private readonly Button close = new Button { Text = "Close", AutoSize = true };
        private readonly Label status = new Label { Dock = DockStyle.Fill };
        private bool installing;

        internal DriverSetupForm(string reason)
        {
            Text = "Kaiden Accel — driver setup";
            Font = new Font("Segoe UI", 10F);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            ClientSize = new Size(620, 350);
            MinimumSize = new Size(620, 390);
            StartPosition = FormStartPosition.CenterScreen;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 95));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.Controls.Add(new Label { Text = "Set up Kaiden Accel", Font = new Font("Segoe UI", 18F, FontStyle.Bold), AutoSize = true }, 0, 0);
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Your mouse driver needs setup before Kaiden Accel can run. The signed Raw Accel driver is included inside this app.\r\n\r\nWindows will ask for administrator permission. Restart your PC after installation, then open Kaiden Accel again." }, 0, 1);
            status.Text = reason;
            layout.Controls.Add(status, 0, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill };
            actions.Controls.AddRange(new Control[] { install, retry, close });
            layout.Controls.Add(actions, 0, 3);
            layout.Controls.Add(new Label { Text = "Includes the original Raw Accel v1.7.1 driver. MIT license and credits retained.", AutoSize = true }, 0, 4);
            Controls.Add(layout);
            Theme.CurrentScheme = ColorScheme.LightTheme;
            Theme.Apply(this);
            install.Click += async (s, e) => await InstallAsync();
            retry.Click += (s, e) => {
                try { VersionHelper.ValidOrThrow(); DialogResult = DialogResult.OK; Close(); }
                catch (InteropException ex) { status.Text = ex.Message + "\r\nIf you just installed the driver, restart your PC first."; }
            };
            close.Click += (s, e) => Close();
            FormClosing += (s, e) => { if (installing) e.Cancel = true; };
        }

        private async Task InstallAsync()
        {
            if (installing) return;
            installing = true;
            install.Enabled = retry.Enabled = close.Enabled = false;
            string directory = Path.Combine(Path.GetTempPath(), "KaidenAccel-setup-" + Guid.NewGuid().ToString("N"));
            try
            {
                string executable = DriverSetupPayload.Extract(directory);
                status.Text = "Complete the driver installer window, then press a key there to close it. Restart your PC if installation completes.";
                using (var process = Process.Start(new ProcessStartInfo {
                    FileName = executable, WorkingDirectory = directory, UseShellExecute = true,
                    Verb = "runas", WindowStyle = ProcessWindowStyle.Normal
                }))
                {
                    if (process == null) throw new InvalidOperationException("Windows could not start driver setup.");
                    await Task.Run(() => process.WaitForExit());
                }
                // The upstream installer does not reliably signal failure in its exit code.
                status.Text = "The installer window closed. If it reported ‘Install complete’, restart your PC and open Kaiden Accel again. If it reported an error, resolve that error before retrying.";
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                status.Text = "Administrator permission was canceled. Click Install included driver when you are ready.";
            }
            catch (Exception ex) { status.Text = "Driver setup could not run: " + ex.Message; }
            finally
            {
                DriverSetupPayload.CleanUp(directory);
                installing = false;
                install.Enabled = retry.Enabled = close.Enabled = true;
            }
        }
    }
}
