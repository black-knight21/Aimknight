using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;

namespace KaidenFlux.Hardware
{
    internal sealed class MouseDeviceCapabilities
    {
        public bool PassiveBattery;
        // Writes are intentionally absent until physical firmware/protocol compatibility is verified.
        public bool HardwareWrites => false;
    }
    internal sealed class MouseDeviceState
    {
        public string Path, Product, Connection;
        public ushort VendorId, ProductId, Firmware, UsagePage, Usage;
        public int InputReportBytes;
        public MouseDeviceCapabilities Capabilities = new MouseDeviceCapabilities();
    }
    internal interface IMouseDevice
    {
        List<MouseDeviceState> Enumerate();
        Task<int?> ReadBatteryAsync(MouseDeviceState device, CancellationToken cancel);
    }
    internal static class AttackSharkX11Protocol
    {
        internal static bool IsCandidate(ushort vendor, ushort product) => vendor == 0x1D57 && (product == 0xFA60 || product == 0xFA55);
        internal static int? Battery(byte[] report, int count)
        {
            if (report == null || count < 5 || count > report.Length || report[0] != 0x03 || report[1] != 0x55 || report[2] != 0x40 || report[4] > 100) return null;
            return report[4];
        }
        internal static bool CanListen(MouseDeviceState device) => IsCandidate(device.VendorId, device.ProductId) && device.Path.IndexOf("mi_02", StringComparison.OrdinalIgnoreCase) >= 0 &&
            ((device.UsagePage >= 0xFF00 && device.InputReportBytes >= 5 && device.InputReportBytes <= 128) || (device.UsagePage == 0x000A && device.Usage == 0 && device.InputReportBytes == 5));
    }
    internal sealed class WindowsHidMouseDevice : IMouseDevice
    {
        public List<MouseDeviceState> Enumerate()
        {
            var devices = new List<MouseDeviceState>(); Guid guid; HidD_GetHidGuid(out guid);
            IntPtr set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12);
            if (set == new IntPtr(-1)) throw new IOException("Windows HID devices could not be enumerated.");
            try {
                for (uint index = 0; ; index++) {
                    var data = new InterfaceData { Size = Marshal.SizeOf(typeof(InterfaceData)) };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data)) { int error = Marshal.GetLastWin32Error(); if (error == 259) break; throw new IOException("A Windows device enumeration error occurred (" + error + ")."); }
                    int required; SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, IntPtr.Zero);
                    if (required <= 8 || required > 65536) continue;
                    IntPtr detail = Marshal.AllocHGlobal(required);
                    try {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, IntPtr.Zero)) continue;
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (path == null || path.IndexOf("vid_1d57", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        using (var handle = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero)) {
                            if (handle.IsInvalid) continue;
                            var attr = new Attributes { Size = Marshal.SizeOf(typeof(Attributes)) };
                            if (!HidD_GetAttributes(handle, ref attr) || !AttackSharkX11Protocol.IsCandidate(attr.Vendor, attr.Product)) continue;
                            string product = "X11-family USB device"; IntPtr text = Marshal.AllocHGlobal(512);
                            try { if (HidD_GetProductString(handle, text, 512)) product = Marshal.PtrToStringUni(text) ?? product; } finally { Marshal.FreeHGlobal(text); }
                            var state = new MouseDeviceState { Path = path, Product = product, Connection = attr.Product == 0xFA60 ? "2.4 GHz receiver" : "Wired USB", VendorId = attr.Vendor, ProductId = attr.Product, Firmware = attr.Version };
                            IntPtr preparsed;
                            if (HidD_GetPreparsedData(handle, out preparsed)) try {
                                var caps = new HidCaps { Reserved = new ushort[17] };
                                if (HidP_GetCaps(preparsed, ref caps) == 0x00110000) { state.UsagePage = caps.UsagePage; state.Usage = caps.Usage; state.InputReportBytes = caps.InputBytes; }
                            } finally { HidD_FreePreparsedData(preparsed); }
                            state.Capabilities.PassiveBattery = AttackSharkX11Protocol.CanListen(state); devices.Add(state);
                        }
                    } finally { Marshal.FreeHGlobal(detail); }
                }
            } finally { SetupDiDestroyDeviceInfoList(set); }
            return devices;
        }
        public async Task<int?> ReadBatteryAsync(MouseDeviceState device, CancellationToken cancel)
        {
            if (!AttackSharkX11Protocol.CanListen(device)) throw new NotSupportedException("Passive status reading is unavailable for this interface. Firmware compatibility is not verified.");
            using (var handle = CreateFile(device.Path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero))
            {
                if (handle.IsInvalid) throw new IOException("The status interface is unavailable, busy, or disconnected.");
                using (var stream = new FileStream(handle, FileAccess.Read, device.InputReportBytes, true))
                using (cancel.Register(() => stream.Dispose()))
                {
                    var bytes = new byte[device.InputReportBytes];
                    while (true) {
                        cancel.ThrowIfCancellationRequested(); int read;
                        try { read = await stream.ReadAsync(bytes, 0, bytes.Length, cancel).ConfigureAwait(false); }
                        catch (Exception ex) when (cancel.IsCancellationRequested && (ex is IOException || ex is ObjectDisposedException)) { throw new OperationCanceledException(cancel); }
                        if (read == 0) throw new IOException("The mouse disconnected while reading status.");
                        var value = AttackSharkX11Protocol.Battery(bytes, read); if (value.HasValue) return value;
                    }
                }
            }
        }
        [StructLayout(LayoutKind.Sequential)] private struct InterfaceData { public int Size; public Guid Class; public int Flags; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)] private struct Attributes { public int Size; public ushort Vendor, Product, Version; }
        [StructLayout(LayoutKind.Sequential)] private struct HidCaps { public ushort Usage, UsagePage, InputBytes, OutputBytes, FeatureBytes; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved; public ushort LinkNodes, InputButtons, InputValues, InputIndices, OutputButtons, OutputValues, OutputIndices, FeatureButtons, FeatureValues, FeatureIndices; }
        [DllImport("hid.dll")] private static extern void HidD_GetHidGuid(out Guid guid);
        [DllImport("hid.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetProductString(SafeFileHandle handle, IntPtr buffer, int size);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
        [DllImport("hid.dll")] [return: MarshalAs(UnmanagedType.U1)] private static extern bool HidD_FreePreparsedData(IntPtr data);
        [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, ref HidCaps caps);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, int size, out int required, IntPtr info);
        [DllImport("setupapi.dll")] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    }
    internal sealed class MouseHardwareService : IDisposable
    {
        private readonly IMouseDevice backend;
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        internal List<MouseDeviceState> Devices { get; private set; } = new List<MouseDeviceState>();
        internal int? BatteryPercent { get; private set; }
        internal DateTime? BatteryReadUtc { get; private set; }
        private string statusPath;
        internal MouseHardwareService(IMouseDevice backend) { this.backend = backend; }
        internal async Task RefreshAsync()
        {
            var found = await Task.Run(() => backend.Enumerate(), closing.Token);
            if (statusPath == null || !found.Any(d => d.Path == statusPath)) { BatteryPercent = null; BatteryReadUtc = null; statusPath = null; }
            Devices = found;
        }
        internal async Task ReadStatusAsync(CancellationToken token)
        {
            var device = Devices.Where(d => d.Capabilities.PassiveBattery).OrderBy(d => d.ProductId == 0xFA55 ? 0 : 1).FirstOrDefault();
            if (device == null) throw new NotSupportedException("No supported passive status interface is available. Battery and charging status remain unavailable.");
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, closing.Token)) {
                deadline.CancelAfter(3000);
                try {
                    var value = await backend.ReadBatteryAsync(device, deadline.Token);
                    if (!Devices.Any(d => d.Path == device.Path)) throw new IOException("The status device disconnected while the read was pending.");
                    if (value.HasValue && (value < 0 || value > 100)) throw new InvalidDataException("Invalid battery report.");
                    BatteryPercent = value; BatteryReadUtc = value.HasValue ? (DateTime?)DateTime.UtcNow : null; statusPath = value.HasValue ? device.Path : null;
                } catch { BatteryPercent = null; BatteryReadUtc = null; statusPath = null; throw; }
            }
        }
        internal Task WriteDpiAsync(int dpi) { throw new NotSupportedException("Hardware writes are unavailable until physical X11 firmware compatibility and readback are verified. Use Mouse setup for driver normalization; this does not program the mouse."); }
        public void Dispose() { closing.Cancel(); closing.Dispose(); }
    }
    internal sealed class MouseControlView : IDisposable
    {
        internal readonly StackPanel Root = new StackPanel();
        private readonly TextBlock devices = StudioUi.Text("Scanning USB devices…", 12);
        private readonly TextBlock battery = StudioUi.Text("—", 28);
        private readonly TextBlock descriptors = StudioUi.Text("No descriptor data yet.", 11);
        private readonly TextBlock dpi = StudioUi.Text("—", 28), polling = StudioUi.Text("—", 22);
        private readonly TextBlock selection = StudioUi.Text("Select a numbered button to inspect its position.", 12);
        private readonly ProgressBar power = new ProgressBar { Minimum = 0, Maximum = 100, Height = 5, Value = 0, Foreground = StudioUi.Ink("#B593F8"), Background = StudioUi.Ink("#332840"), BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 12) };
        private readonly Button[] markers = new Button[5];
        private readonly MouseHardwareService service;
        private readonly Action<string> status;
        private readonly Func<int> getDpi, getPolling;
        private readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        private bool reading, scanning, disposed;
        private readonly bool preview;
        internal event Action<string> StatusChanged;
        internal MouseControlView(Window window, MouseHardwareService service, Action<string> status, bool preview,
            Func<int> getDpi = null, Func<int> getPolling = null, Action<int> setup = null, Action profiles = null)
        {
            this.service = service; this.status = status; this.preview = preview; this.getDpi = getDpi; this.getPolling = getPolling;
            var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) }); layout.ColumnDefinitions.Add(new ColumnDefinition()); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(245) }); Root.Children.Add(layout);
            var left = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; layout.Children.Add(left);
            var bindings = Panel("BUTTON EXPLORER", 16);
            string[] names = { "Left button", "Right button", "Scroll / middle", "Front side button", "Rear side button" };
            for (int i = 0; i < names.Length; i++) { int id = i; var button = StudioUi.Button(window, (i + 1) + "   " + names[i], () => SelectButton(id, names), status); button.Padding = new Thickness(10, 10, 10, 10); button.HorizontalAlignment = HorizontalAlignment.Stretch; bindings.Children.Add(button); }
            bindings.Children.Add(selection); bindings.Children.Add(StudioUi.Text("Position guide · current hardware assignments are not read back.", 11));
            var remap = StudioUi.Button(window, "Remapping locked", () => { }, status); remap.IsEnabled = false; bindings.Children.Add(remap); left.Children.Add(Card(window, bindings));
            var profile = Panel("YOUR GAME SETUP", 14); profile.Children.Add(StudioUi.Text("Keep game sensitivity and acceleration curves together.", 12));
            var profileButton = StudioUi.Button(window, "Game profiles →", () => profiles?.Invoke(), status); profileButton.IsEnabled = profiles != null; profile.Children.Add(profileButton); left.Children.Add(Card(window, profile));
            var center = new StackPanel { Margin = new Thickness(0, 0, 12, 0) }; Grid.SetColumn(center, 1); layout.Children.Add(center);
            var hero = Panel("ATTACK SHARK", 12); hero.Children.Add(StudioUi.Text("X11", 36)); hero.Children.Add(devices);
            var drawing = new Canvas { Width = 260, Height = 370 };
            var body = new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M 64,22 C 28,40 25,99 24,173 L 23,251 C 21,325 57,356 130,359 C 203,356 239,325 237,251 L 236,173 C 235,99 232,40 196,22 C 171,11 89,11 64,22 Z"), Fill = new System.Windows.Media.LinearGradientBrush(System.Windows.Media.Color.FromRgb(89,65,123), System.Windows.Media.Color.FromRgb(31,23,47), 90), Stroke = StudioUi.Ink("#A182C5"), StrokeThickness = 2 };
            drawing.Children.Add(body);
            var seams = new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse("M 130,19 L 130,190 M 25,176 Q 130,207 235,176 M 46,98 L 44,198"), Stroke = StudioUi.Ink("#AE8BD0"), StrokeThickness = 1.5 }; drawing.Children.Add(seams);
            var wheel = new Border { Width = 22, Height = 53, CornerRadius = new CornerRadius(10), Background = StudioUi.Ink("#100B1B"), BorderBrush = StudioUi.Ink("#C2A4E6"), BorderThickness = new Thickness(2) }; Canvas.SetLeft(wheel,119); Canvas.SetTop(wheel,54); drawing.Children.Add(wheel);
            var logo = StudioUi.Text("K", 32); logo.Foreground = StudioUi.Ink("#B593F8"); Canvas.SetLeft(logo,117); Canvas.SetTop(logo,262); drawing.Children.Add(logo);
            double[] xs = { 65, 166, 116, 9, 8 }, ys = { 99, 99, 115, 168, 217 };
            for(int i=0;i<5;i++) { int id=i; var marker=StudioUi.Button(window,(i+1).ToString(),()=>SelectButton(id,names),status); marker.Width=marker.Height=29; marker.Padding=new Thickness(0); marker.Margin=new Thickness(0); marker.ToolTip=names[i]; markers[i]=marker; Canvas.SetLeft(marker,xs[i]); Canvas.SetTop(marker,ys[i]); drawing.Children.Add(marker); }
            hero.Children.Add(new Viewbox { Child = drawing, Stretch = System.Windows.Media.Stretch.Uniform, MaxHeight = 280, Margin = new Thickness(0, 4, 0, 14) });
            hero.Children.Add(StudioUi.Text("X11 layout guide · select a button above", 11));
            var actions=new WrapPanel(); var refresh=StudioUi.Button(window,"Rescan",()=>{},status); refresh.Click+=async(s,e)=>await ScanAsync(); actions.Children.Add(refresh);
            var windows=StudioUi.Button(window,"Windows mouse",()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe","main.cpl") { UseShellExecute=true }),status); actions.Children.Add(windows); hero.Children.Add(actions); center.Children.Add(Card(window,hero));
            var right=new StackPanel(); Grid.SetColumn(right,2); layout.Children.Add(right);
            var dpiPanel=Panel("DPI · MANUAL SETUP",12); dpiPanel.Children.Add(dpi); dpiPanel.Children.Add(StudioUi.Text("Manual normalization value. Set sensor DPI in your mouse software.",11));
            var editDpi=StudioUi.Button(window,"Edit DPI setup →",()=>setup?.Invoke(1),status,true); editDpi.IsEnabled=setup!=null; dpiPanel.Children.Add(editDpi);
            var stages=StudioUi.Button(window,"DPI stages locked",()=>{},status); stages.IsEnabled=false; dpiPanel.Children.Add(stages); right.Children.Add(Card(window,dpiPanel));
            var hzPanel=Panel("POLLING · CONFIGURED",12); hzPanel.Children.Add(polling);
            hzPanel.Children.Add(StudioUi.Text("Driver timing value, not measured frequency.",11)); var editHz=StudioUi.Button(window,"Edit polling setup →",()=>setup?.Invoke(2),status); editHz.IsEnabled=setup!=null; hzPanel.Children.Add(editHz); right.Children.Add(Card(window,hzPanel));
            var powerPanel=Panel("POWER",12); powerPanel.Children.Add(battery); powerPanel.Children.Add(power); powerPanel.Children.Add(StudioUi.Text("Actual received battery reports only. Charging status unavailable.",11));
            var read=StudioUi.Button(window,"Read battery status",()=>{},status); read.Click+=async(s,e)=>{
                if(reading)return; reading=true; read.IsEnabled=false;
                try { if(preview)throw new InvalidOperationException("Hardware access is disabled in design preview."); await service.ReadStatusAsync(CancellationToken.None); Update(); }
                catch(OperationCanceledException){Update();status("No battery packet arrived within three seconds. Battery remains unavailable.");}
                catch(Exception ex){Update();status(ex.Message);}
                finally{reading=false;if(!disposed)read.IsEnabled=true;}
            }; powerPanel.Children.Add(read); right.Children.Add(Card(window,powerPanel));
            var more=new Expander { Header="Device details & hardware support", Foreground=StudioUi.Ink("#B9A3D5"), Margin=new Thickness(0,0,0,16) };
            var details=new StackPanel { Margin=new Thickness(4,14,4,0) }; details.Children.Add(descriptors); details.Children.Add(StudioUi.Text("DPI stages, hardware polling writes, lighting, remapping and onboard profiles need verified firmware commands/readback. They are unavailable. No feature or output reports are sent. Manual DPI remains usable without a connected X11. Receiver presence does not prove a wireless mouse is online.",12)); more.Content=details; Root.Children.Add(more);
            poll.Tick+=async(s,e)=>await ScanAsync(); if(!preview)poll.Start(); else devices.Text="Design preview · no device scan";
            RefreshSetup();
        }
        private static Border Card(Window window, UIElement content) { var card=StudioUi.Card(window,content); card.Padding=new Thickness(14); return card; }
        private static StackPanel Panel(string title,int size) { var panel=new StackPanel(); var heading=StudioUi.Text(title,size); heading.Foreground=StudioUi.Ink("#BDA2DC"); panel.Children.Add(heading); return panel; }
        private void SelectButton(int id,string[] names)
        {
            for(int i=0;i<markers.Length;i++) if(markers[i]!=null) { markers[i].Background=StudioUi.Ink(i==id?"#B593F8":"#39254F"); markers[i].Foreground=StudioUi.Ink(i==id?"#160F25":"#F3EDFF"); }
            selection.Text=names[id]+" selected. Hardware remapping is not verified.";
        }
        internal void RefreshSetup() { dpi.Text=getDpi==null?"Not configured":getDpi().ToString("N0"); polling.Text=getPolling==null?"Not configured":getPolling()==0?"Automatic":getPolling().ToString("N0")+" Hz"; }
        internal async Task ScanAsync()
        {
            RefreshSetup(); if(preview||disposed||scanning||reading)return; scanning=true;
            try{await service.RefreshAsync();if(!disposed)Update();}
            catch(Exception ex){if(!disposed){devices.Text="Scan unavailable · manual setup retained";status(ex.Message);}}
            finally{scanning=false;}
        }
        private void Update()
        {
            if(disposed)return;
            var kinds=service.Devices.Select(d=>d.Connection).Distinct().ToArray();
            devices.Text=kinds.Length==0?"No X11 USB detected":string.Join(" · ",kinds)+" detected";
            descriptors.Text=service.Devices.Count==0?"No matching USB descriptor data.":string.Join("\n",service.Devices.GroupBy(d=>new {d.ProductId,d.Firmware}).Select(g=>"VID 1D57 / PID "+g.Key.ProductId.ToString("X4")+" · descriptor "+g.Key.Firmware.ToString("X4")+" · "+g.Count()+" HID collections\n"+string.Join(", ",g.Select(d=>d.UsagePage.ToString("X4")+":"+d.Usage.ToString("X4")).Distinct())));
            battery.Text=service.BatteryPercent.HasValue?service.BatteryPercent.Value+"%":"Unavailable"; power.Value=service.BatteryPercent??0;
            battery.ToolTip=service.BatteryReadUtc.HasValue?"Last report: "+service.BatteryReadUtc.Value.ToLocalTime().ToString("T"):"No verified battery report received";
            StatusChanged?.Invoke((kinds.Length==0?"X11 USB: not detected":"X11-family USB: "+string.Join(" / ",kinds))+" · Battery: "+(service.BatteryPercent.HasValue?service.BatteryPercent.Value+"% (last report)":"unavailable")+" · DPI: manual, not hardware-verified");
        }
        public void Dispose(){disposed=true;poll.Stop();service.Dispose();}
    }
}
