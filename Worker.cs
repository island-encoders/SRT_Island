using System.Diagnostics;
using System.IO.Pipes;
using System.Text.RegularExpressions;

record WorkerStatus(string State, string Video, string Audio, bool HasPreview, bool Active, bool PlayingOut, string[] Log, string TimeSync = "");

sealed class Worker(Route route, EngineSettings engines, string restreamDirectory, NtpClock ntpClock)
{
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly Queue<string> log = new();
    public PreviewFeed Feed { get; } = new();
    private string state = "Starting";
    private string video = "";
    private string audio = "";
    private bool active = true;
    private bool playingOut;
    private string syncState = "";
    private bool SyncEnabled => route.Input == "srt" && route.NtpSync;
    private bool Bonded => route.Input == "srt" && route.Mode == "bonded";
    private bool UsesDisplay => DisplayOutput.IsGpu(route.Device);
    private bool UsesAja => AjaOutput.IsAja(route.Device);
    private string OutputSource => UsesDisplay ? "GPU display" : UsesAja ? "AJA" : "DeckLink";
    private int OutputChannels => UsesDisplay || route.Input == "pattern" || route.AudioPairs == 1 ? 2 : route.AudioPairs <= 4 ? 8 : 16;

    public Route Route => route;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool Active { get { lock (gate) return active; } }
    public void Start() => Completion = Task.Run(Run);

    public void Stop()
    {
        lock (gate)
        {
            if (!active) return;
            state = "Stopping";
            stop.Cancel();
        }
    }

    public WorkerStatus Status()
    {
        lock (gate)
        {
            var hasPreview = FreshPreview();
            return new(state == "Receiving" && !hasPreview ? "Waiting for video" : state,
                video, audio, hasPreview, active, playingOut && hasPreview, log.ToArray(), syncState);
        }
    }

    private bool FreshPreview() => Feed.Current() is not null;
    public byte[]? Preview() => Feed.Current();

    private async Task Run()
    {
        using var surfaceStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        Task? surface = null;
        nint window = 0;
        try
        {
            if (UsesDisplay)
            {
                if (route.DisplayAudio != "mute" &&
                    !(await DisplayOutput.Audio(engines.DisplayPlayer, stop.Token)).Any(audio => audio.Id == route.DisplayAudio))
                    throw new InvalidOperationException("The selected Windows audio endpoint is unavailable.");
                var ready = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
                surface = DisplayOutput.RunWindow(route.Device, false, handle => ready.TrySetResult(handle), surfaceStop.Token);
                if (await Task.WhenAny(ready.Task, surface) == surface)
                {
                    await surface;
                    return;
                }
                window = await ready.Task;
            }
            do
            {
                lock (gate)
                {
                    if (stop.IsCancellationRequested) break;
                    state = route.Input == "srt" ? "Waiting for video" : "Starting";
                    video = "";
                    audio = "";
                    playingOut = false;
                    syncState = SyncEnabled ? "Waiting for SRT" : "";
                }
                try
                {
                    using var receiveStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    var receive = Receive(window, receiveStop.Token);
                    if (surface is not null && await Task.WhenAny(receive, surface) == surface)
                    {
                        receiveStop.Cancel();
                        try { await receive; } catch (OperationCanceledException) { }
                        await surface;
                        break;
                    }
                    await receive;
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                { AddLog("Route", error.Message); }
                finally { Feed.Publish(null); }
                if (stop.IsCancellationRequested || surface?.IsCompleted == true || route.Input != "srt" || !route.Reconnect) break;
                lock (gate) state = "Reconnecting";
                await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);
            } while (!stop.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        { AddLog("GPU display", error.Message); }
        finally
        {
            surfaceStop.Cancel();
            if (surface is not null)
            {
                try { await surface; }
                catch (Exception error) when (error is InvalidOperationException or OperationCanceledException) { }
            }
            lock (gate) { state = stop.IsCancellationRequested ? "Stopped" : "Ended"; active = false; }
        }
    }

    private async Task Receive(nint window, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var pipeName = $"srt-island-{Guid.NewGuid():N}";
        var destinations = route.Destinations ?? [];
        var relayName = destinations.Length > 0 ? $"srt-island-relay-{Guid.NewGuid():N}" : null;
        using var relayPipe = relayName is null ? null : new NamedPipeServerStream(relayName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var previewStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var input = new Process { StartInfo = ProcessInfo(engines.Receiver, ReceiverArguments(pipeName, relayName), redirectInput: Bonded || SyncEnabled) };
        using var transport = Bonded ? new Process
        {
            StartInfo = ProcessInfo(engines.BondReceiver,
                [string.Join(',', route.Host.Split(',', StringSplitOptions.TrimEntries).Select(host => host.Trim('[', ']'))),
                 route.Port.ToString(), route.Latency.ToString()])
        } : SyncEnabled ? new Process
        {
            StartInfo = ProcessInfo(engines.Receiver,
                ["-hide_banner", "-nostdin", "-nostats", "-f", "data", "-i", SrtUrl(),
                 "-map", "0:0", "-c", "copy", "-flush_packets", "1", "-f", "data", "pipe:1"])
        } : null;
        using var output = route.Device.Length == 0 ? null : new Process
        {
            StartInfo = UsesDisplay ? ProcessInfo(engines.DisplayPlayer,
                DisplayOutput.PlayerArguments(window, route.DisplayAudio, route.ExclusiveDisplayAudio), redirectInput: true) :
                UsesAja ? ProcessInfo(engines.AjaPlayer, AjaOutput.Parse(route.Device).Arguments(), redirectInput: true) :
                    ProcessInfo(engines.DeckLink, OutputArguments(), redirectInput: true)
        };
        Task? inputLog = null;
        Task? outputLog = null;
        Task? outputTerminal = null;
        Task? bridge = null;
        Task? bondLog = null;
        Task? bondBridge = null;
        token = previewStop.Token;
        var previewReader = Feed.Read(pipe, previewStop.Token);
        var relay = relayPipe is null ? null : new Restream(engines.Receiver, destinations, restreamDirectory, AddLog)
            .Run(relayPipe, previewStop.Token);
        try
        {
            if (output is not null)
            {
                output.Start();
                outputLog = ReadLog(output.StandardError, OutputSource, token);
                if (UsesDisplay || UsesAja) outputTerminal = ReadLog(output.StandardOutput, OutputSource, token);
            }
            input.Start();
            inputLog = ReadLog(input.StandardError, "Receiver", token);
            if (transport is not null)
            {
                if (Bonded) transport.StartInfo.Environment["SRT_ISLAND_BOND_PASSPHRASE"] = route.BondPassphrase;
                transport.Start();
                bondLog = ReadLog(transport.StandardError, Bonded ? "Bond" : "SRT transport", token);
                bondBridge = SyncEnabled ? PumpSynchronized(transport, input, token) : Pump(transport, input, token);
            }
            var inputExit = input.WaitForExitAsync(token);
            if (output is null)
            {
                var first = await Task.WhenAny(inputExit, bondBridge ?? inputExit);
                await first;
                if (first == bondBridge) await inputExit.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
            else
            {
                var outputExit = output.WaitForExitAsync(token);
                bridge = Pump(input, output, token);
                var first = await Task.WhenAny(bridge, outputExit, inputExit, bondBridge ?? inputExit);
                if (first == inputExit && input.ExitCode == 0)
                {
                    await bridge.WaitAsync(TimeSpan.FromSeconds(5), token);
                    await outputExit.WaitAsync(TimeSpan.FromSeconds(5), token);
                }
                else if (first == bondBridge)
                {
                    await first;
                    await bridge.WaitAsync(TimeSpan.FromSeconds(5), token);
                    await outputExit.WaitAsync(TimeSpan.FromSeconds(5), token);
                }
                else await first;
                if (output.HasExited) AddLog(OutputSource, $"Process exited with code {output.ExitCode}.");
            }
            if (input.HasExited) AddLog("Receiver", $"Process exited with code {input.ExitCode}.");
        }
        catch (TimeoutException error) { AddLog("Route", error.Message); }
        finally
        {
            Kill(input);
            if (output is not null) Kill(output);
            if (transport is not null) Kill(transport);
            previewStop.Cancel();
            foreach (var task in new[] { inputLog, outputLog, outputTerminal, bridge, bondLog, bondBridge, previewReader, relay }.OfType<Task>())
            {
                try { await task; }
                catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
        }
    }

    private async Task PumpSynchronized(Process input, Process output, CancellationToken token)
    {
        var sync = new TimeSync(ntpClock, engines.NtpDelayMs, value => { lock (gate) syncState = value; });
        try { await sync.Run(input.StandardOutput.BaseStream, output.StandardInput.BaseStream, token); }
        finally { output.StandardInput.Close(); }
    }

    private string SrtUrl()
    {
        var host = route.Host.Trim('[', ']');
        if (host.Contains(':')) host = $"[{host}]";
        var listen = route.Mode == "listener" ? "&listen_timeout=10000000" : "";
        var id = route.StreamId.Length == 0 ? "" : $"&streamid={Uri.EscapeDataString(route.StreamId)}";
        return $"srt://{host}:{route.Port}?mode={route.Mode}&latency={route.Latency * 1000}&timeout=5000000{listen}{id}";
    }

    private static async Task Pump(Process input, Process output, CancellationToken token)
    {
        try { await input.StandardOutput.BaseStream.CopyToAsync(output.StandardInput.BaseStream, 262144, token); }
        finally { output.StandardInput.Close(); }
    }

    internal static ProcessStartInfo ProcessInfo(string executable, IEnumerable<string> args, bool redirectInput = false)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = redirectInput, RedirectStandardError = true, RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    internal static void Kill(Process process)
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(2000); } }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private IEnumerable<string> ReceiverArguments(string pipeName, string? relayName)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-nostats", "-y", "-filter_threads", "2" };
        switch (route.Input)
        {
            case "pattern":
                args.AddRange(["-re", "-f", "lavfi", "-i", $"smptehdbars=size=1920x1080:rate={route.Rate}",
                    "-re", "-f", "lavfi", "-i", "sine=frequency=1000:sample_rate=48000"]);
                break;
            case "file":
                args.AddRange(["-re", "-stream_loop", "-1", "-i", route.File]);
                break;
            default:
                if (route.Decoder != "cpu") args.AddRange(["-hwaccel", route.Decoder]);
                else args.AddRange(["-threads", "0"]);
                if (Bonded || SyncEnabled)
                {
                    args.AddRange(["-f", "mpegts", "-probesize", SyncEnabled ? "32768" : "2000000",
                        "-analyzeduration", SyncEnabled ? "1" : "1500000", "-i", "pipe:0"]);
                    break;
                }
                args.AddRange(["-probesize", "2000000", "-analyzeduration", "1500000", "-i", SrtUrl()]);
                break;
        }
        var audio = route.Input == "pattern" ? "1:a:0?" : $"0:a:{route.AudioTrack - 1}?";
        args.AddRange(["-map", "0:v:0", "-an", "-vf", "scale=800:-2", "-fps_mode", "passthrough", "-c:v", "mjpeg",
            "-threads", "1", "-q:v", "5", "-flush_packets", "1", "-f", "image2pipe", @"\\.\pipe\" + pipeName]);
        if (route.Record)
        {
            var folder = Directory.CreateDirectory(engines.Recordings).FullName;
            var file = Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{route.Id:N}_{Guid.NewGuid():N}.ts");
            if (route.Input != "pattern")
                args.AddRange(["-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-f", "mpegts", file]);
        }
        if (relayName is not null)
        {
            var relayAudio = route.Input == "pattern" ? "1:a?" : "0:a?";
            args.AddRange(["-map", "0:v:0", "-map", relayAudio, "-c", "copy", "-flush_packets", "1",
                "-f", "nut", @"\\.\pipe\" + relayName]);
        }
        if (route.Device.Length > 0)
        {
            const string scale = "scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1";
            const string deinterlace = "bwdif=mode=send_frame:deint=interlaced";
            args.AddRange(["-map", "0:v:0", "-vf", UsesDisplay ? deinterlace : $"{deinterlace},{scale},fps={route.Rate}",
                "-c:v", "rawvideo", "-threads:v", "1", "-pix_fmt", UsesDisplay ? "yuv420p" : "uyvy422"]);
            if (OutputChannels == 2)
                args.AddRange(["-map", audio, "-af", "aresample=async=1000:first_pts=0"]);
            else
                args.AddRange(["-filter_complex", MultiPairFilter(), "-map", "[embedded]"]);
            args.AddRange(["-c:a", "pcm_s16le", "-ac", OutputChannels.ToString(), "-ar", "48000",
                "-f", "nut", "pipe:1"]);
        }
        return args;
    }

    private string MultiPairFilter()
    {
        // Keep source order and reserve the unused positions in the DeckLink frame as silence.
        var count = route.AudioPairs * 2;
        var mapping = string.Join('|', Enumerable.Range(0, count).Select(channel => $"c{channel}=c{channel}"));
        var pad = $"pan={OutputChannels}c|{mapping},aresample=async=1000:first_pts=0[embedded]";
        var start = route.AudioTrack - 1;
        if (route.AudioLayout == "interleaved") return $"[0:a:{start}]{pad}";
        var pairs = Enumerable.Range(0, route.AudioPairs).ToArray();
        var normalize = string.Concat(pairs.Select(pair => $"[0:a:{start + pair}]aformat=sample_rates=48000:channel_layouts=stereo[p{pair}];"));
        return normalize + string.Concat(pairs.Select(pair => $"[p{pair}]")) + $"amerge=inputs={route.AudioPairs},{pad}";
    }

    private string[] OutputArguments() =>
    [
        "-hide_banner", "-nostdin", "-nostats", "-filter_threads", "1",
        "-probesize", "32768", "-analyzeduration", "1", "-f", "nut", "-i", "pipe:0",
        "-map", "0:v:0", "-map", "0:a:0?", "-pix_fmt", "uyvy422",
        "-r", route.Rate, "-c:a", "pcm_s16le", "-ar", "48000", "-ac", OutputChannels.ToString(),
        "-preroll", "0.25", "-level_a", "true", "-f", "decklink", route.Device.Split(" | ")[0]
    ];

    private async Task ReadLog(StreamReader reader, string source, CancellationToken token)
    {
        var readingInput = false;
        var audioIndex = 0;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (source == "Receiver")
            {
                if (line.StartsWith("Input #0,") || (route.Input == "pattern" && line.StartsWith("Input #1,"))) readingInput = true;
                if (line.StartsWith("Stream mapping:") || (route.Input != "pattern" && line.StartsWith("Input #1,"))) readingInput = false;
                if (readingInput)
                {
                    var match = Regex.Match(line, @"\b(Video|Audio):\s*(.+)$");
                    if (match.Success)
                    {
                        lock (gate)
                        {
                            if (match.Groups[1].Value == "Video" && video.Length == 0) video = match.Groups[2].Value;
                            else if (match.Groups[1].Value == "Audio")
                            {
                                audioIndex++;
                                var first = route.Input == "pattern" ? 1 : route.AudioTrack;
                                var tracks = UsesDisplay || route.Input == "pattern" || route.AudioLayout == "interleaved" ? 1 : route.AudioPairs;
                                if (audioIndex >= first && audioIndex < first + tracks)
                                    audio += (audio.Length == 0 ? "" : " · ") + match.Groups[2].Value;
                            }
                        }
                    }
                }
                if (line.StartsWith("Output #0,") && !token.IsCancellationRequested)
                    lock (gate) if (!token.IsCancellationRequested) state = "Receiving";
            }
            if (source == "DeckLink" && line.StartsWith("Output #0, decklink,"))
                lock (gate) playingOut = true;
            if (source == "GPU display" && line.TrimStart().StartsWith("VO:"))
                lock (gate) playingOut = true;
            if (source == "AJA" && line.Contains("GstAjaSink:playout.GstPad:sink: caps ="))
                lock (gate) playingOut = true;
            AddLog(source, line);
        }
    }

    private void AddLog(string source, string line)
    {
        line = Regex.Replace(line, @"(?i)(?:srt|rtmps?|https?)://[^\s'""\]]+", "[endpoint]");
        lock (gate)
        {
            log.Enqueue($"{DateTime.Now:HH:mm:ss}  {source}  {line.Trim()}");
            while (log.Count > 80) log.Dequeue();
        }
    }
}
