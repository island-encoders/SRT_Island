using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
var home = AppContext.BaseDirectory;
var headless = args.Contains("--headless") || args.Contains("--no-browser");
var portable = Path.GetFullPath(Path.Combine(home, "..", "data"));
var data = Directory.CreateDirectory(Environment.GetEnvironmentVariable("SRT_ISLAND_DATA")
    ?? (File.Exists(Path.Combine(home, "..", "Start SRT Island.cmd")) ? portable :
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SRT Island")));
var dataId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.FullName.ToUpperInvariant())));
using var instance = new Mutex(true, $"Local\\SrtIsland-{dataId}", out var firstInstance);
if (!firstInstance)
{
    if (!headless)
        System.Windows.Forms.MessageBox.Show("SRT Island is already open for this workspace.", "SRT Island");
    return;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = home });
var panelUrl = headless ? "http://127.0.0.1:8777" : "http://127.0.0.1:0";
const int remotePort = 8778;
var remoteToken = Environment.GetEnvironmentVariable("SRT_ISLAND_REMOTE_TOKEN");
var remoteDigest = remoteToken is { Length: >= 32 } ? SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + remoteToken)) : null;
builder.WebHost.UseUrls(remoteDigest is null ? [panelUrl] : [panelUrl, $"http://127.0.0.1:{remotePort}"]);
var app = builder.Build();
var station = new Station(data.FullName, home);
var encoder = new IslandEncoder();
var addressBook = new EncoderBook(data.FullName);
app.Use(async (context, next) =>
{
    if (context.Connection.LocalPort == remotePort)
    {
        if (remoteDigest is null || !context.Request.Path.StartsWithSegments("/remote/v1"))
        { context.Response.StatusCode = 404; return; }
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
        if (!CryptographicOperations.FixedTimeEquals(remoteDigest, supplied))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.StatusCode = 401;
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
        await next();
        return;
    }
    if (context.Request.Path.StartsWithSegments("/remote"))
    { context.Response.StatusCode = 404; return; }
    if (context.Request.Host.Host != "127.0.0.1" ||
        (context.Request.Headers.TryGetValue("Origin", out var origin) &&
         origin != $"http://127.0.0.1:{context.Connection.LocalPort}"))
    {
        context.Response.StatusCode = 403;
        return;
    }
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
var hlsDirectory = Directory.CreateDirectory(Path.Combine(data.FullName, "HLS")).FullName;
var hlsTypes = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
hlsTypes.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
hlsTypes.Mappings[".m4s"] = "video/iso.segment";
hlsTypes.Mappings[".ts"] = "video/mp2t";
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/hls",
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(hlsDirectory),
    ContentTypeProvider = hlsTypes
});
app.MapGet("/api/routes", () => station.Snapshot());
app.MapGet("/api/remote-control", () => new { enabled = remoteDigest is not null, port = remotePort });
app.MapGet("/remote/v1/slots", station.RemoteSlots);
app.MapPost("/remote/v1/slots/{slot:int}/start", (int slot) => Change(() => station.ControlSlot(slot, true)));
app.MapPost("/remote/v1/slots/{slot:int}/stop", (int slot) => Change(() => station.ControlSlot(slot, false)));
app.MapGet("/api/dashboard", () => new
{
    uptimeSeconds = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
    memoryBytes = Environment.WorkingSet,
    savedEncoders = addressBook.List().Length,
    addresses = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(adapter => adapter.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
        .Select(item => item.Address).Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && !IPAddress.IsLoopback(address)).Select(address => address.ToString()).Distinct().ToArray()
});
app.MapGet("/api/settings", () => station.Settings);
app.MapGet("/api/displays", () => DisplayOutput.Displays().Select(display => new
{
    display.Device, display.Name, display.WindowsName, display.Primary, display.Mirrored,
    display.Width, display.Height, display.Connector, display.Available
}));
app.MapGet("/api/gpus", DisplayOutput.Inventory);
app.MapGet("/api/aja-devices", (CancellationToken token) =>
    EncoderRequest(async () => await AjaOutput.Discover(station.Settings.AjaPlayer, token)));
app.MapGet("/api/display-audio", (CancellationToken token) =>
    EncoderRequest(async () => await DisplayOutput.Audio(station.Settings.DisplayPlayer, token)));
app.MapPost("/api/displays/identify", async (IdentifyDisplay request, CancellationToken token) =>
{
    try { await station.Identify(request.Device, token); return Results.NoContent(); }
    catch (InvalidOperationException error) { return Results.BadRequest(new { error = error.Message }); }
});
app.MapPost("/api/displays/settings", () => Change(() =>
    Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true })));
app.MapPut("/api/settings", (EngineSettings settings) => Change(() => station.Configure(settings)));
app.MapGet("/api/devices", async (CancellationToken token) =>
{
    try { return Results.Ok(await station.Devices(token)); }
    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
    { return Results.BadRequest(new { error = error.Message }); }
});
app.MapPost("/api/routes", (Route route) => Change(() => station.Save(route)));
app.MapDelete("/api/routes/{id:guid}", (Guid id) => Change(() => station.Remove(id)));
app.MapPost("/api/routes/{id:guid}/start", (Guid id) => Change(() => station.Start(id)));
app.MapPost("/api/routes/{id:guid}/stop", (Guid id) => Change(() => station.Stop(id)));
app.MapGet("/api/routes/{id:guid}/preview", (Guid id) => station.Preview(id) is { } frame
    ? Results.Bytes(frame, "image/jpeg") : Results.NotFound());
app.MapGet("/api/routes/{id:guid}/live-preview", async (Guid id, HttpContext context) =>
{
    if (station.PreviewFeed(id) is not { } feed) { context.Response.StatusCode = 404; return; }
    try { await feed.Stream(context); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
});
app.MapPost("/api/encoder/connect", (EncoderLogin login, CancellationToken token) =>
    EncoderRequest(() => encoder.Connect(login, token)));
app.MapGet("/api/encoder", (CancellationToken token) => EncoderRequest(() => encoder.Status(token)));
app.MapGet("/api/encoder/tailnet", (string? tag, CancellationToken token) =>
    EncoderRequest(() => encoder.Peers(tag, token)));
app.MapPut("/api/encoder/channels/{id:int}", (int id, EncoderChannel channel, CancellationToken token) =>
    EncoderRequest(() => encoder.Configure(id, channel, token)));
app.MapPost("/api/encoder/disconnect", async (CancellationToken token) =>
{
    await encoder.Disconnect(token);
    return Results.NoContent();
});
app.MapGet("/api/encoders", () => addressBook.List().Select(entry => new
{
    entry.Id, entry.Name, entry.Address, entry.TailnetAddress, entry.Username, hasPassword = !string.IsNullOrEmpty(entry.Secret)
}));
app.MapPost("/api/encoders", (EncoderEntryEdit entry) => Change(() => addressBook.Save(entry)));
app.MapPost("/api/encoders/{id:guid}/connect", (Guid id, EncoderConnection choice, CancellationToken token) =>
    EncoderRequest(() => encoder.Connect(addressBook.Login(id, choice.Tailnet), token)));
app.MapDelete("/api/encoders/{id:guid}", (Guid id) => Change(() => addressBook.Remove(id)));
app.MapGet("/api/tailnet", (string? tag, CancellationToken token) =>
    EncoderRequest(() => EncoderBook.Tailnet(tag, token)));
app.Lifetime.ApplicationStopping.Register(station.Shutdown);
if (headless) app.Run();
else
{
    try
    {
        app.StartAsync().GetAwaiter().GetResult();
        DesktopWindow.Run(app.Urls.Select(url => new Uri(url)).Single(url => url.Port != remotePort),
            data.FullName, app.Lifetime.ApplicationStopping);
    }
    catch (Exception error) when (error is IOException or InvalidOperationException)
    {
        System.Windows.Forms.MessageBox.Show(error.Message, "SRT Island",
            System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
    }
    finally
    {
        app.StopAsync().GetAwaiter().GetResult();
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

static IResult Change(Action apply)
{
    try { apply(); return Results.NoContent(); }
    catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
    { return Results.BadRequest(new { error = error.Message }); }
}

static async Task<IResult> EncoderRequest(Func<Task<object>> request)
{
    try { return Results.Ok(await request()); }
    catch (Exception error) when (error is InvalidOperationException or HttpRequestException or JsonException or TaskCanceledException or System.ComponentModel.Win32Exception)
    { return Results.BadRequest(new { error = error.Message }); }
}

record IdentifyDisplay(string Device);
record EngineSettings(string Receiver, string DeckLink, string Recordings, string BondReceiver = "", string DisplayPlayer = "",
    string AjaPlayer = "", string NtpServer = "time.windows.com", int NtpDelayMs = 3000);

record Route(Guid Id, string Name, string Input, string Mode, string Host, int Port,
    int Latency, string StreamId, string File, string Device, string Rate, int AudioTrack,
    bool Record, bool Reconnect, string Decoder = "cpu", string AudioLayout = "tracks",
    int AudioPairs = 1, string BondPassphrase = "", string DisplayAudio = "mute", bool ExclusiveDisplayAudio = false,
    int Slot = 0, Destination[]? Destinations = null, bool NtpSync = false);

sealed class Station
{
    internal static readonly string[] Rates = ["24000/1001", "24", "25", "30000/1001", "30", "50", "60000/1001", "60"];
    private readonly object gate = new();
    private readonly string root;
    private readonly Dictionary<Guid, Route> routes;
    private readonly Dictionary<Guid, Worker> workers = new();
    private readonly HashSet<string> identifyingDisplays = new(StringComparer.OrdinalIgnoreCase);
    private EngineSettings settings;
    private NtpClock ntpClock;

    public Station(string root, string home)
    {
        this.root = root;
        var search = new[]
        {
            Path.Combine(home, "tools"),
            Path.Combine(root, "tools"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SRT Island", "tools"),
            Path.GetFullPath(Path.Combine(home, "..", "tools")),
            Path.GetFullPath(Path.Combine(home, "..", "..", "..", "tools"))
        };
        string Find(string executable) => search.Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, executable, SearchOption.AllDirectories)).FirstOrDefault() ?? "";
        settings = Read<EngineSettings>("engines.json") ?? new(
            Find("ffmpeg.exe"), Find("ffmpeg-decklink.exe"), Path.Combine(root, "Recordings"), Find("srt-island-bond.exe"));
        ntpClock = new NtpClock(settings.NtpServer);
        settings = settings with
        {
            Receiver = File.Exists(settings.Receiver) ? settings.Receiver : Find("ffmpeg.exe"),
            DeckLink = File.Exists(settings.DeckLink) ? settings.DeckLink : Find("ffmpeg-decklink.exe"),
            BondReceiver = File.Exists(settings.BondReceiver) ? settings.BondReceiver : Find("srt-island-bond.exe"),
            DisplayPlayer = File.Exists(settings.DisplayPlayer) ? settings.DisplayPlayer : Find("mpv.com")
        };
        routes = (Read<Route[]>("routes.json") ?? []).ToDictionary(route => route.Id);
        foreach (var route in routes.Values.Where(route => route.Slot == 0).ToArray())
            if (AvailableSlot() is var slot && slot != 0) routes[route.Id] = route with { Slot = slot };
    }

    private int AvailableSlot() => Enumerable.Range(1, 8).FirstOrDefault(slot => routes.Values.All(route => route.Slot != slot));

    public EngineSettings Settings { get { lock (gate) return settings; } }

    private T? Read<T>(string file)
    {
        var path = Path.Combine(root, file);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : default;
    }

    private void Persist<T>(string file, T value)
    {
        var path = Path.Combine(root, file);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }

    public void Configure(EngineSettings value)
    {
        lock (gate)
        {
            ValidateExecutable(value.Receiver, "SRT receiver");
            if (!string.IsNullOrWhiteSpace(value.DeckLink)) ValidateExecutable(value.DeckLink, "DeckLink engine");
            if (!string.IsNullOrWhiteSpace(value.BondReceiver)) ValidateExecutable(value.BondReceiver, "SRT bonding receiver");
            if (!string.IsNullOrWhiteSpace(value.AjaPlayer)) ValidateExecutable(value.AjaPlayer, "AJA GStreamer launcher");
            if (!string.IsNullOrWhiteSpace(value.DisplayPlayer) &&
                (!Path.IsPathFullyQualified(value.DisplayPlayer) || !File.Exists(value.DisplayPlayer) ||
                 !new[] { ".exe", ".com" }.Contains(Path.GetExtension(value.DisplayPlayer), StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Choose a valid mpv executable.");
            if (string.IsNullOrWhiteSpace(value.Recordings) || !Path.IsPathFullyQualified(value.Recordings))
                throw new InvalidOperationException("Choose an absolute recordings folder.");
            if (Uri.CheckHostName(value.NtpServer) == UriHostNameType.Unknown || value.NtpDelayMs is < 250 or > 10000)
                throw new InvalidOperationException("Enter an NTP server hostname or IP and a sync delay of 250–10000 ms.");
            Persist("engines.json", value);
            if (!value.NtpServer.Equals(settings.NtpServer, StringComparison.OrdinalIgnoreCase)) ntpClock = new NtpClock(value.NtpServer);
            settings = value;
        }
    }

    public async Task<string[]> Devices(CancellationToken token)
    {
        var executable = RequireDeckLink(Settings);
        using var process = Process.Start(Worker.ProcessInfo(executable, ["-hide_banner", "-sinks", "decklink"]))
            ?? throw new InvalidOperationException("Could not launch the DeckLink engine.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var errors = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return Regex.Matches(await output + await errors, @"(?m)^\s*\*?\s*([^\s\[]+)\s+\[([^\r\n]+?)\]")
                .Select(match => match.Groups[1].Value + " | " + match.Groups[2].Value).Distinct().ToArray();
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new InvalidOperationException("DeckLink enumeration timed out."); }
        finally { Worker.Kill(process); }
    }

    public object[] Snapshot()
    {
        lock (gate)
            return routes.Values.Select(route => (object)new
            {
                route, status = workers.GetValueOrDefault(route.Id)?.Status() ?? new WorkerStatus("Stopped", "", "", false, false, false, [])
            }).ToArray();
    }

    public object[] RemoteSlots()
    {
        lock (gate)
            return routes.Values.Where(route => route.Slot > 0).OrderBy(route => route.Slot).Select(route =>
            {
                var status = workers.GetValueOrDefault(route.Id)?.Status();
                return (object)new { route.Slot, route.Name, state = status?.State ?? "Stopped",
                    receiving = status?.HasPreview ?? false, playingOut = status?.PlayingOut ?? false, timeSync = status?.TimeSync ?? "" };
            }).ToArray();
    }

    public void ControlSlot(int slot, bool start)
    {
        lock (gate)
        {
            var route = routes.Values.FirstOrDefault(route => route.Slot == slot && slot > 0)
                ?? throw new InvalidOperationException("Configure this slot on the station first.");
            if (start) Start(route.Id);
            else Stop(route.Id);
        }
    }

    public async Task Identify(string device, CancellationToken token)
    {
        lock (gate)
        {
            if (workers.Values.Any(worker => worker.Active && worker.Route.Device.Equals(device, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Stop playout on this display before identifying it.");
            if (!identifyingDisplays.Add(device))
                throw new InvalidOperationException("This display is already showing its identification label.");
        }
        try { await DisplayOutput.Show(device, true, token); }
        finally
        {
            lock (gate) identifyingDisplays.Remove(device);
        }
    }

    public byte[]? Preview(Guid id)
    {
        Worker? worker;
        lock (gate) worker = workers.GetValueOrDefault(id);
        return worker?.Preview();
    }

    public PreviewFeed? PreviewFeed(Guid id)
    {
        lock (gate) return workers.GetValueOrDefault(id)?.Feed;
    }

    public void Save(Route route)
    {
        if (string.IsNullOrWhiteSpace(route.Name) || route.Name.Length > 100 ||
            route.Input is not ("srt" or "file" or "pattern") ||
            route.Mode is not ("caller" or "listener" or "rendezvous" or "bonded") ||
            route.Port is < 1 or > 65535 || route.Latency is < 20 or > 8000 ||
            route.AudioTrack is < 1 or > 32 || !Rates.Contains(route.Rate) ||
            route.Decoder is not ("cpu" or "auto" or "cuda" or "d3d11va" or "dxva2") ||
            route.AudioLayout is not ("tracks" or "interleaved") || route.AudioPairs is < 1 or > 8 ||
            route.BondPassphrase is null || (route.BondPassphrase.Length != 0 && route.BondPassphrase.Length is < 10 or > 79) ||
            route.StreamId is null || route.StreamId.Length > 512 ||
            route.Device is null || route.Device.Length > 256 || route.Device.StartsWith('-') ||
            route.DisplayAudio is null || route.DisplayAudio.Length > 256 ||
            (route.DisplayAudio != "mute" && !route.DisplayAudio.StartsWith("wasapi/", StringComparison.Ordinal)))
            throw new InvalidOperationException("Check the route name, SRT settings, audio track and output format.");
        if (route.Input == "srt")
        {
            var hosts = route.Mode == "bonded" ? (route.Host ?? "").Split(',', StringSplitOptions.TrimEntries) : [route.Host ?? ""];
            if (hosts.Length > 8 || hosts.Any(host => Uri.CheckHostName(host.Trim('[', ']')) == UriHostNameType.Unknown &&
                                                      !IPAddress.TryParse(host, out _)))
                throw new InvalidOperationException("Enter a valid address. A bonded listener accepts up to eight comma-separated bind addresses.");
        }
        if (AjaOutput.IsAja(route.Device)) AjaOutput.Parse(route.Device);
        Restream.Validate(route.Destinations ?? []);
        if ((route.Destinations?.Length ?? 0) > 0 && route.Input != "srt")
            throw new InvalidOperationException("Restream destinations require an incoming SRT feed.");
        if (route.Input == "file" && (string.IsNullOrWhiteSpace(route.File) || !Path.IsPathFullyQualified(route.File)))
            throw new InvalidOperationException("Enter an absolute path for the test clip.");
        lock (gate)
        {
            var id = route.Id == Guid.Empty ? Guid.NewGuid() : route.Id;
            var slot = route.Slot > 0 ? route.Slot : routes.GetValueOrDefault(id)?.Slot ?? AvailableSlot();
            if (slot is < 0 or > 8 || (slot > 0 && routes.Values.Any(item => item.Id != id && item.Slot == slot)) ||
                (slot == 0 && !routes.ContainsKey(id)))
                throw new InvalidOperationException("Choose an available slot from 1 to 8.");
            route = route with { Slot = slot };
            if (workers.GetValueOrDefault(id)?.Active == true)
                throw new InvalidOperationException("Stop the route before editing it.");
            var updated = routes.Values.Where(item => item.Id != id).Append(route with { Id = id }).ToArray();
            Persist("routes.json", updated);
            routes[id] = route with { Id = id };
            workers.Remove(id);
        }
    }

    public void Remove(Guid id)
    {
        lock (gate)
        {
            if (workers.GetValueOrDefault(id)?.Active == true)
                throw new InvalidOperationException("Stop the route before deleting it.");
            Persist("routes.json", routes.Values.Where(route => route.Id != id).ToArray());
            routes.Remove(id);
            workers.Remove(id);
        }
    }

    public void Start(Guid id)
    {
        lock (gate)
        {
            if (!routes.TryGetValue(id, out var route)) throw new InvalidOperationException("Route not found.");
            if (workers.GetValueOrDefault(id)?.Active == true) return;
            ValidateExecutable(settings.Receiver, "SRT receiver");
            if (route.Input == "srt" && route.Mode == "bonded") ValidateExecutable(settings.BondReceiver, "SRT bonding receiver");
            if (DisplayOutput.IsGpu(route.Device))
            {
                if (!File.Exists(settings.DisplayPlayer)) throw new InvalidOperationException("Choose an mpv display player in Settings.");
                if (identifyingDisplays.Contains(route.Device))
                    throw new InvalidOperationException("Wait for the display identification label to disappear before starting playout.");
                DisplayOutput.Select(route.Device, true);
                if (route.DisplayAudio != "mute" && workers.Values.Any(worker => worker.Active &&
                    DisplayOutput.IsGpu(worker.Route.Device) && worker.Route.DisplayAudio == route.DisplayAudio))
                    throw new InvalidOperationException("That display audio endpoint is already assigned to a running route.");
            }
            else if (AjaOutput.IsAja(route.Device)) ValidateExecutable(settings.AjaPlayer, "AJA GStreamer launcher");
            else if (route.Device.Length > 0) RequireDeckLink(settings);
            if (route.Input == "file" && !File.Exists(route.File)) throw new InvalidOperationException("Test clip not found.");
            if (workers.Values.Any(worker => worker.Active && Conflicts(route, worker.Route)))
                throw new InvalidOperationException("A running route already uses this listener port or hardware output.");
            var worker = new Worker(route, settings, Path.Combine(root, "HLS", route.Id.ToString()), ntpClock);
            workers[id] = worker;
            worker.Start();
        }
    }

    private static bool Conflicts(Route first, Route second) =>
        (first.Device.Length > 0 && DisplayOutput.Key(first.Device).Equals(DisplayOutput.Key(second.Device), StringComparison.OrdinalIgnoreCase)) ||
        AjaOutput.Conflicts(first.Device, second.Device) ||
        (first.Input == "srt" && second.Input == "srt" && first.Mode is ("listener" or "bonded") &&
         second.Mode is ("listener" or "bonded") && first.Port == second.Port);

    public void Stop(Guid id) { lock (gate) workers.GetValueOrDefault(id)?.Stop(); }

    public void Shutdown()
    {
        Worker[] active;
        lock (gate) { active = workers.Values.ToArray(); foreach (var worker in active) worker.Stop(); }
        Task.WhenAll(active.Select(worker => worker.Completion)).Wait(TimeSpan.FromSeconds(5));
    }

    private static string RequireDeckLink(EngineSettings value)
    {
        ValidateExecutable(value.DeckLink, "DeckLink engine");
        return value.DeckLink;
    }

    private static void ValidateExecutable(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidOperationException($"Choose a valid {label} executable in Settings.");
    }

}
