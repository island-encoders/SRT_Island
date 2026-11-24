using System.Diagnostics;
using System.IO.Pipes;
using System.Threading.Channels;

record Destination(string Name, string Protocol, string Url, string Mode, string Codec, int VideoKbps, int AudioKbps,
    int AudioTrack = 1);

sealed class Restream(string executable, Destination[] destinations, string directory, Action<string, string> log)
{
    private const string Prefix = "Restream";

    internal static void Validate(Destination[] destinations)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in destinations)
        {
            if (target is null || string.IsNullOrWhiteSpace(target.Name) || target.Name.Length > 64 ||
                !names.Add(target.Name) || target.Mode is not ("copy" or "encode") ||
                target.Codec is not ("h264" or "h265" or "av1") || target.VideoKbps is < 128 or > 100000 ||
                target.AudioKbps is < 32 or > 512 || target.AudioTrack is < 1 or > 32 ||
                target.Protocol is not ("rtmp" or "enhanced" or "srt" or "srt-mkv" or "hls" or "hls-put"))
                throw new InvalidOperationException("Check the restream destination names, protocol, codec and bitrates.");
            if (target.Protocol == "rtmp" && target.Mode == "encode" && target.Codec != "h264")
                throw new InvalidOperationException("Classic RTMP encoding requires H.264. Use Enhanced RTMP for HEVC or AV1.");
            if (target.Protocol == "srt" && target.Mode == "encode" && target.Codec == "av1")
                throw new InvalidOperationException("Use Enhanced RTMP or HLS for AV1 encoding. SRT output uses MPEG-TS for interoperability.");
            if (target.Protocol == "hls") continue;
            if (target.Url is null || target.Url.Length > 4096 || !Uri.TryCreate(target.Url, UriKind.Absolute, out var endpoint) ||
                string.IsNullOrEmpty(endpoint.Host) || endpoint.Fragment.Length > 0 ||
                (target.Protocol is ("rtmp" or "enhanced") && endpoint.Scheme is not ("rtmp" or "rtmps")) ||
                (target.Protocol is ("srt" or "srt-mkv") && (endpoint.Scheme != "srt" || endpoint.Port < 1)) ||
                (target.Protocol == "hls-put" && (endpoint.Scheme is not ("http" or "https") ||
                    !endpoint.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException($"Check the URL for {target.Name}.");
        }
    }

    public async Task Run(NamedPipeServerStream pipe, CancellationToken token)
    {
        var outputs = destinations.Select(target => new Output(target)).ToArray();
        var tasks = outputs.Select((item, index) => Send(item, index, token)).ToArray();
        try
        {
            await pipe.WaitForConnectionAsync(token);
            var buffer = new byte[65536];
            int count;
            while ((count = await pipe.ReadAsync(buffer, token)) > 0)
            {
                var chunk = buffer.AsSpan(0, count).ToArray();
                foreach (var output in outputs)
                {
                    if (output.Closed || output.Queue.Writer.TryWrite(chunk)) continue;
                    output.Close();
                    Report(output, "Stopped because its output queue filled.");
                }
            }
        }
        finally
        {
            foreach (var output in outputs) output.Queue.Writer.TryComplete();
            await Task.WhenAll(tasks);
            foreach (var output in outputs) output.Dispose();
        }
    }

    private sealed class Output(Destination destination) : IDisposable
    {
        public Destination Destination { get; } = destination;
        public Channel<byte[]> Queue { get; } = Channel.CreateBounded<byte[]>(48);
        public CancellationTokenSource Overflow { get; } = new();
        public bool Closed => Overflow.IsCancellationRequested;
        public void Close()
        {
            Queue.Writer.TryComplete();
            Overflow.Cancel();
        }
        public void Dispose() => Overflow.Dispose();
    }

    private async Task Send(Output output, int index, CancellationToken stop)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop, output.Overflow.Token);
        using var process = new Process();
        var target = output.Destination;
        using var av1 = target is { Mode: "encode", Codec: "av1" } ? new Process() : null;
        try
        {
            process.StartInfo = Worker.ProcessInfo(executable, Arguments(target, index, av1 is not null), redirectInput: true);
            process.Start();
            if (av1 is not null)
            {
                // libaom only exposes AV1 sequence data with its first encoded packet.
                // Probing a NUT hop gives live muxers the header before they open the destination.
                var arguments = InputArguments(target.AudioTrack);
                AddEncodeArguments(arguments, target);
                arguments.AddRange(["-f", "nut", "-flush_packets", "1", "pipe:1"]);
                av1.StartInfo = Worker.ProcessInfo(executable, arguments, redirectInput: true);
                av1.Start();
            }
            var token = lifetime.Token;
            using var cancellation = token.Register(() => _ = Task.Run(() =>
            {
                Worker.Kill(process);
                if (av1 is not null) Worker.Kill(av1);
            }));
            var errors = ReadLog(process.StandardError, output, token);
            var encoderErrors = av1 is not null ? ReadLog(av1.StandardError, output, token) : Task.CompletedTask;
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            try
            {
                var feed = av1 is null ? Feed(output, process, token) : FeedThroughEncoder(output, av1, process, token);
                var exit = process.WaitForExitAsync(token);
                if (await Task.WhenAny(feed, exit) == exit) lifetime.Cancel();
                await feed;
                if (!process.HasExited)
                    await exit.WaitAsync(TimeSpan.FromSeconds(3), token);
                if (!token.IsCancellationRequested || process.HasExited)
                    Report(output, $"Encoder exited with code {process.ExitCode}.");
            }
            finally
            {
                Worker.Kill(process);
                if (av1 is not null) Worker.Kill(av1);
                try { await Task.WhenAll(errors, encoderErrors, stdout); }
                catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        { Report(output, error.Message); }
        finally
        {
            Worker.Kill(process);
            if (av1 is not null) Worker.Kill(av1);
            output.Close();
        }
    }

    private static async Task Feed(Output output, Process process, CancellationToken token)
    {
        await foreach (var packet in output.Queue.Reader.ReadAllAsync(token))
            await process.StandardInput.BaseStream.WriteAsync(packet, token);
        process.StandardInput.Close();
    }

    private static async Task FeedThroughEncoder(Output output, Process encoder, Process muxer, CancellationToken token)
    {
        var input = Feed(output, encoder, token);
        try { await encoder.StandardOutput.BaseStream.CopyToAsync(muxer.StandardInput.BaseStream, token); }
        finally
        {
            if (!input.IsCompleted) Worker.Kill(encoder);
            try { await input; }
            finally { muxer.StandardInput.Close(); }
        }
    }

    private async Task ReadLog(StreamReader reader, Output output, CancellationToken token)
    {
        while (await reader.ReadLineAsync(token) is { } line)
            if (line.Contains("Output #0") || line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) || line.Contains("recover", StringComparison.OrdinalIgnoreCase))
                Report(output, line);
    }

    private void Report(Output output, string message)
    {
        if (!string.IsNullOrEmpty(output.Destination.Url))
            message = message.Replace(output.Destination.Url, "[destination]", StringComparison.Ordinal);
        log($"{Prefix} · {output.Destination.Name}", message);
    }

    private static List<string> InputArguments(int track) =>
    [
        "-hide_banner", "-nostdin", "-nostats", "-y", "-f", "nut", "-i", "pipe:0",
        "-map", "0:v:0", "-map", $"0:a:{track - 1}?"
    ];

    private static void AddEncodeArguments(List<string> args, Destination target)
    {
        var codec = target.Codec switch { "h265" => "libx265", "av1" => "libaom-av1", _ => "libx264" };
        args.AddRange(["-c:v", codec, "-pix_fmt", "yuv420p", "-b:v", $"{target.VideoKbps}k",
            "-maxrate", $"{target.VideoKbps}k", "-bufsize", $"{target.VideoKbps * 2}k", "-threads", "4",
            "-force_key_frames", "expr:gte(t,n_forced*2)",
            "-c:a", "aac", "-b:a", $"{target.AudioKbps}k", "-ar", "48000", "-ac", "2"]);
        if (target.Codec == "av1")
            args.AddRange(["-usage", "realtime", "-cpu-used", "8", "-row-mt", "1", "-lag-in-frames", "0"]);
        else args.AddRange(["-preset", "ultrafast"]);
        if (target.Codec == "h265") args.AddRange(["-x265-params", "pools=4:frame-threads=2"]);
        if (target.Protocol is "rtmp" or "enhanced" or "srt-mkv" or "hls" or "hls-put")
            args.AddRange(["-flags:v", "+global_header"]);
    }

    private IEnumerable<string> Arguments(Destination target, int index, bool alreadyEncoded)
    {
        var args = InputArguments(alreadyEncoded ? 1 : target.AudioTrack);
        if (alreadyEncoded || target.Mode == "copy") args.AddRange(["-c", "copy"]);
        else
            AddEncodeArguments(args, target);
        if (target.Protocol is "hls" or "hls-put")
        {
            var playlist = target.Url;
            if (target.Protocol == "hls")
            {
                var folder = Directory.CreateDirectory(Path.Combine(directory, (index + 1).ToString())).FullName;
                playlist = Path.Combine(folder, "index.m3u8");
                args.AddRange(["-hls_flags", "delete_segments+temp_file"]);
            }
            else args.AddRange(["-method", "PUT", "-http_persistent", "1"]);
            args.AddRange(["-f", "hls", "-hls_time", "2", "-hls_list_size", "6",
                "-hls_start_number_source", "epoch", "-hls_segment_type", target.Mode == "copy" ? "mpegts" : "fmp4", playlist]);
        }
        else if (target.Protocol == "srt-mkv")
            args.AddRange(["-f", "matroska", "-live", "1", "-flush_packets", "1", target.Url]);
        else
        {
            var format = target.Protocol == "srt" ? "mpegts" : "flv";
            args.AddRange(["-f", "fifo", "-fifo_format", format,
                "-drop_pkts_on_overflow", "1", "-attempt_recovery", "1", "-recover_any_error", "1",
                "-recovery_wait_time", "1", "-restart_with_keyframe", "1", "-queue_size", "180"]);
            if (target.Protocol == "enhanced")
                args.AddRange(["-format_opts", "rtmp_enhanced_codecs=hvc1,av01"]);
            args.Add(target.Url);
        }
        return args;
    }
}
