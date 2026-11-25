using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32;

namespace KaidenFlux
{
    // Uses the original signed driver unchanged. Only this user-mode setup flow is new.
    internal static class FluxInstaller
    {
        private const string DriverHash = "8A62C4DEEF2774B43A7363B352EDA79897533A1080C9C26FFEFF0559E43358D7";
        private const string MouseClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96f-e325-11ce-bfc1-08002be10318}";
        internal static string[] BuildFilters(string[] original)
        {
            if (original == null || !original.Any(s => string.Equals(s, "mouclass", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The Windows mouse filter configuration could not be verified. Setup stopped without changing it.");
            return new[] { "rawaccel" }.Concat(original.Where(s => !string.Equals(s, "rawaccel", StringComparison.OrdinalIgnoreCase))).ToArray();
        }
        internal static byte[] Payload()
        {
            using (var stream = typeof(FluxInstaller).Assembly.GetManifestResourceStream("KaidenAccel.Setup.rawaccel.sys"))
            using (var memory = new MemoryStream())
            {
                if (stream == null) throw new IOException("The embedded driver is missing. Extract the full package again.");
                stream.CopyTo(memory);
                byte[] bytes = memory.ToArray();
                using (var sha = SHA256.Create())
                    if (BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "") != DriverHash)
                        throw new IOException("The embedded driver did not pass its integrity check.");
                return bytes;
            }
        }
        internal static void Install()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                    throw new UnauthorizedAccessException("Windows administrator permission is required.");
            byte[] payload = Payload();
            using (var key = Registry.LocalMachine.OpenSubKey(MouseClass, true))
            {
                if (key == null) throw new IOException("Windows mouse settings could not be opened.");
                var original = key.GetValue("UpperFilters") as string[];
                var filters = BuildFilters(original);
                string target = Path.Combine(Environment.SystemDirectory, "drivers", "rawaccel.sys");
                string backup = target + ".kaiden-" + Guid.NewGuid().ToString("N") + ".bak";
                bool oldMoved = false, newWritten = false, created = false;
                IntPtr manager = OpenSCManager(null, null, 0xF003F);
                if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows driver services could not be opened.");
                IntPtr service = IntPtr.Zero;
                try
                {
                    service = OpenService(manager, "rawaccel", 0xF01FF);
                    if (service == IntPtr.Zero)
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error != 1060) throw new Win32Exception(error, "The mouse driver service could not be inspected.");
                        service = CreateService(manager, "rawaccel", "AimKnight Mouse Driver", 0xF01FF, 1, 3, 1,
                            @"\SystemRoot\System32\drivers\rawaccel.sys", null, IntPtr.Zero, null, null, null);
                        if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "The mouse driver service could not be created.");
                        created = true;
                    }
                    else
                    {
                        // Do not redirect an existing service with an unexpected binary path.
                        using (var existing = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\rawaccel"))
                        {
                            string imagePath = (string)existing?.GetValue("ImagePath") ?? "";
                            var normalized = Environment.ExpandEnvironmentVariables(imagePath).Trim('"');
                            if (normalized.StartsWith(@"\??\", StringComparison.Ordinal)) normalized = normalized.Substring(4);
                            if (!normalized.Equals(target, StringComparison.OrdinalIgnoreCase) &&
                                !normalized.Equals(@"\SystemRoot\System32\drivers\rawaccel.sys", StringComparison.OrdinalIgnoreCase) &&
                                !normalized.Equals(@"System32\drivers\rawaccel.sys", StringComparison.OrdinalIgnoreCase))
                                throw new IOException("An existing mouse driver has an unexpected location. Setup stopped without replacing it.");
                        }
                    }
                    if (File.Exists(target))
                    {
                        if (!MoveFileEx(target, backup, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "The existing driver could not be backed up.");
                        oldMoved = true;
                    }
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                        newWritten = true;
                        output.Write(payload, 0, payload.Length);
                    }
                    key.SetValue("UpperFilters", filters, RegistryValueKind.MultiString);
                    if (oldMoved) MoveFileEx(backup, null, 4); // remove backup at reboot
                }
                catch
                {
                    if (newWritten) { try { File.Delete(target); } catch { } }
                    if (oldMoved) MoveFileEx(backup, target, 1);
                    if (created && service != IntPtr.Zero) DeleteService(service);
                    throw;
                }
                finally
                {
                    if (service != IntPtr.Zero) CloseServiceHandle(service);
                    CloseServiceHandle(manager);
                }
            }
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateService(IntPtr manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string group, IntPtr tag, string dependencies, string account, string password);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DeleteService(IntPtr service);
        [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool MoveFileEx(string oldName, string newName, uint flags);
    }
}
