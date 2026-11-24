using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

record GpuDisplay(string Device, string Name, string WindowsName, bool Primary, bool Mirrored,
    int Width, int Height, Rectangle Bounds, string Connector, bool Available);
record GpuAudio(string Id, string Name);

static class DisplayOutput
{
    internal static bool IsGpu(string device) => device.StartsWith("gpu|", StringComparison.OrdinalIgnoreCase);
    internal static string Key(string device) => IsGpu(device) ? device : device.Split(" | ")[0];

    internal static GpuDisplay[] Displays()
    {
        var paths = ActiveTargets().ToLookup(target => target.Source, StringComparer.OrdinalIgnoreCase);
        return Screen.AllScreens.Select(screen =>
        {
            var targets = paths[screen.DeviceName];
            var first = targets.FirstOrDefault();
            var id = string.IsNullOrWhiteSpace(first?.Path) ? screen.DeviceName : first.Path;
            var name = string.IsNullOrWhiteSpace(first?.Name) ? screen.DeviceName : first.Name;
            var mirrored = targets.Skip(1).Any();
            return new GpuDisplay("gpu|" + id, name, screen.DeviceName, screen.Primary, mirrored,
                screen.Bounds.Width, screen.Bounds.Height, screen.Bounds, first?.Connector ?? "",
                !string.IsNullOrWhiteSpace(first?.Path) && !screen.Primary && !mirrored);
        }).ToArray();
    }

    internal static GpuDisplay Select(string device, bool forPlayout)
    {
        var display = Displays().FirstOrDefault(item => item.Device.Equals(device, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The selected display is disconnected. Connect it and extend the Windows desktop.");
        if (forPlayout && !display.Available)
            throw new InvalidOperationException("Choose an extended secondary display. Set your operator screen as the main display in Windows Settings.");
        return display;
    }

    internal static async Task<GpuAudio[]> Audio(string player, CancellationToken token)
    {
        if (!File.Exists(player)) return [];
        using var process = Process.Start(Worker.ProcessInfo(player, ["--no-config", "--terminal=yes", "--audio-device=help"]))
            ?? throw new InvalidOperationException("Could not start the display player.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return Regex.Matches(await stdout + await stderr, @"(?m)^\s*'(wasapi/[^']+)'\s*\(([^\r\n]+)\)")
                .Select(match => new GpuAudio(match.Groups[1].Value, match.Groups[2].Value)).ToArray();
        }
        finally { Worker.Kill(process); }
    }

    internal static async Task Show(string device, bool identify, CancellationToken token)
    {
        await RunWindow(device, identify, null, token);
    }

    internal static Task RunWindow(string device, bool identify, Action<nint>? ready, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var display = Select(device, !identify);
                using var form = new OutputForm { Bounds = identify ? Center(display.Bounds) : display.Bounds };
                if (identify)
                {
                    form.BackColor = Color.FromArgb(49, 27, 82);
                    var label = new Label
                    {
                        Text = $"{display.WindowsName.Split('\\').Last()}\n{display.Name}",
                        ForeColor = Color.Gold, Font = new Font("Segoe UI", 24), TextAlign = ContentAlignment.MiddleCenter,
                        Dock = DockStyle.Fill
                    };
                    form.Controls.Add(label);
                }
                using var timer = new System.Windows.Forms.Timer { Interval = identify ? 2200 : 500 };
                Exception? disconnect = null;
                timer.Tick += (_, _) =>
                {
                    if (identify) { form.Close(); return; }
                    try
                    {
                        var current = Select(device, true);
                        if (form.Bounds != current.Bounds) form.Bounds = current.Bounds;
                    }
                    catch (InvalidOperationException error) { disconnect = error; form.Close(); }
                };
                form.Shown += (_, _) => { ready?.Invoke(form.Handle); timer.Start(); };
                _ = form.Handle;
                using var registration = token.Register(() =>
                {
                    try { form.BeginInvoke(form.Close); }
                    catch (InvalidOperationException) { }
                });
                System.Windows.Forms.Application.Run(form);
                if (disconnect is not null) throw disconnect;
                completion.TrySetResult();
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = identify ? "GPU display identification" : "GPU playout surface" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static string[] PlayerArguments(nint window, string audio, bool exclusive) =>
    [
        "--no-config", "--terminal=yes", "--quiet", "--no-osc", "--osd-level=0", "--input-default-bindings=no",
        "--input-cursor=no", "--cursor-autohide=always", "--no-input-media-keys", "--no-input-vo-keyboard",
        "--sid=no", "--no-cache", "--vd-lavc-threads=1", "--audio-channels=stereo",
        "--audio-fallback-to-null=no", $"--audio-exclusive={(exclusive ? "yes" : "no")}",
        $"--wid={window}", audio == "mute" ? "--no-audio" : $"--audio-device={audio}", "-"
    ];

    private static Rectangle Center(Rectangle screen)
    {
        var width = Math.Min(450, screen.Width);
        var height = Math.Min(180, screen.Height);
        return new Rectangle(screen.X + (screen.Width - width) / 2, screen.Y + (screen.Height - height) / 2, width, height);
    }

    private sealed class OutputForm : Form
    {
        public OutputForm()
        {
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.Black;
            TopMost = true;
            ShowInTaskbar = false;
            Text = "SRT Island GPU Output";
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000080; return value; }
        }
    }

    private record Target(long Adapter, uint Id, string Source, string Name, string Path, string Connector,
        bool Active, bool Connected);

    internal static object[] Inventory()
    {
        var adapterNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (uint index = 0; index < 64; index++)
        {
            var device = new NativeDisplayDevice { Size = Marshal.SizeOf<NativeDisplayDevice>() };
            if (!EnumDisplayDevices(null, index, ref device, 0)) break;
            adapterNames[device.Name] = device.Description;
        }
        var active = ActiveTargets();
        var destinations = active.Concat(ReadTargets(1)).GroupBy(target => (target.Adapter, target.Id))
            .Select(group => group.OrderByDescending(target => target.Active).ThenByDescending(target => target.Connected).First());
        var screens = Screen.AllScreens.ToDictionary(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase);
        return destinations.GroupBy(target => target.Adapter).Select((gpu, index) => (object)new
        {
            name = gpu.OrderByDescending(target => target.Active).Select(target => adapterNames.GetValueOrDefault(target.Source))
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? $"Display adapter {index + 1}",
            outputs = gpu.OrderByDescending(target => target.Active).ThenBy(target => target.Id).Select(target =>
            {
                var screen = screens.GetValueOrDefault(target.Source);
                var mirrored = target.Active && active.Count(item => item.Source == target.Source) > 1;
                var state = mirrored ? "Mirrored desktop" : target.Active && screen?.Primary == true ? "Primary desktop" :
                    target.Active ? "Extended desktop" : target.Connected ? "Connected · disabled in Windows" : "Inactive · no display detected";
                return new { target.Id, target.Name, target.Connector, target.Active, state };
            }).ToArray()
        }).ToArray();
    }

    private static Target[] ActiveTargets() => ReadTargets(2);

    private static Target[] ReadTargets(uint queryFlags)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(queryFlags, out var count, out var modeCount) != 0) return [];
            var paths = new DisplayPath[count];
            // DISPLAYCONFIG_MODE_INFO is a 16-byte header and a 48-byte union.
            var modes = Marshal.AllocHGlobal(checked((int)modeCount * 64));
            try
            {
                var result = QueryDisplayConfig(queryFlags, ref count, paths, ref modeCount, modes, 0);
                if (result == 122) continue;
                if (result != 0) return [];
                var targets = new List<Target>();
                foreach (var path in paths.Take((int)count))
                {
                    var source = new SourceName
                    {
                        Header = new DeviceHeader(1, Marshal.SizeOf<SourceName>(), path.SourceAdapter, path.SourceId)
                    };
                    if (GetSourceName(ref source) != 0 && queryFlags == 2) continue;
                    var target = new TargetName
                    {
                        Header = new DeviceHeader(2, Marshal.SizeOf<TargetName>(), path.Target.Adapter, path.Target.Id)
                    };
                    GetTargetName(ref target);
                    var connector = path.Target.Technology switch
                    {
                        0 => "VGA", 4 => "DVI", 5 => "HDMI", 10 or 18 => "DisplayPort",
                        11 => "Internal DisplayPort", 16 => "Indirect display", 17 => "Virtual display", _ => ""
                    };
                    targets.Add(new Target(path.Target.Adapter, path.Target.Id, source.Name ?? "", target.Name ?? "",
                        target.Path ?? "", connector, (path.Flags & 1) != 0, path.Target.Available != 0));
                }
                return targets.ToArray();
            }
            finally { Marshal.FreeHGlobal(modes); }
        }
        return [];
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly record struct DeviceHeader(int Type, int Size, long Adapter, uint Id);

    [StructLayout(LayoutKind.Explicit, Pack = 4, Size = 48)]
    private struct PathTarget
    {
        [FieldOffset(0)] public long Adapter;
        [FieldOffset(8)] public uint Id;
        [FieldOffset(16)] public int Technology;
        [FieldOffset(40)] public int Available;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct DisplayPath
    {
        public long SourceAdapter;
        public uint SourceId, SourceMode, SourceStatus;
        public PathTarget Target;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct SourceName
    {
        public DeviceHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 4)]
    private struct TargetName
    {
        public DeviceHeader Header;
        public uint Flags;
        public int Technology;
        public ushort Manufacturer, Product;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Path;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeDisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint count, [Out] DisplayPath[] paths, ref uint modeCount, nint modes, nint topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetSourceName(ref SourceName request);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int GetTargetName(ref TargetName request);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref NativeDisplayDevice result, uint flags);
}
