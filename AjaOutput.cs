using System.Text.RegularExpressions;
using System.Diagnostics;

record AjaOutput(string Card, string Channel, string Connector)
{
    internal static bool IsAja(string device) => device.StartsWith("aja|", StringComparison.OrdinalIgnoreCase);

    internal static AjaOutput Parse(string device)
    {
        var match = Regex.Match(device, @"^aja\|([A-Za-z0-9_-]{1,64})\|([1-8])\|(hdmi|sdi-[1-8])$");
        if (!match.Success) throw new InvalidOperationException("Choose an AJA card index or serial, channel and SDI or HDMI output.");
        return new AjaOutput(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
    }

    internal string[] Arguments() =>
    [
        "-v", "fdsrc", "fd=0", "blocksize=65536", "!", "application/x-nut", "!", "avdemux_nut", "name=ingest",
        "ingest.video_0", "!", "queue", "!", "videoconvert", "!", "video/x-raw,format=v210", "!", "assemble.video",
        "ingest.audio_0", "!", "queue", "!", "audioconvert", "!", "audio/x-raw,format=S32LE,rate=48000,layout=interleaved", "!", "assemble.audio",
        "ajasinkcombiner", "name=assemble", "!", "ajasink", "name=playout",
        $"device-identifier={Card}", $"channel={int.Parse(Channel) - 1}", $"output-destination={Connector}"
    ];

    internal static bool Conflicts(string first, string second)
    {
        if (!IsAja(first) || !IsAja(second)) return false;
        var a = Parse(first);
        var b = Parse(second);
        return a.Card.Equals(b.Card, StringComparison.OrdinalIgnoreCase) && (a.Channel == b.Channel || a.Connector == b.Connector);
    }

    internal static async Task<string[]> Discover(string launcher, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(launcher)) return [];
        var monitor = Path.Combine(Path.GetDirectoryName(launcher)!, "gst-device-monitor-1.0.exe");
        if (!File.Exists(monitor)) return [];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = Process.Start(Worker.ProcessInfo(monitor, ["Video/Sink"]));
        if (process is null) return [];
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            await errors;
            var discovered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in (await output).Split('\n'))
            {
                if (!line.Contains("ajasink", StringComparison.OrdinalIgnoreCase)) continue;
                var card = Regex.Match(line, "device-identifier=[\"']?([A-Za-z0-9_-]{1,64})").Groups[1].Value;
                var channel = Regex.Match(line, @"\bchannel=([0-7])").Groups[1].Value;
                var connector = Regex.Match(line, "output-destination=[\"']?(hdmi|sdi-[1-8])").Groups[1].Value;
                if (card.Length == 0 || channel.Length == 0) continue;
                var port = int.Parse(channel) + 1;
                discovered.Add($"aja|{card}|{port}|{(connector.Length == 0 ? $"sdi-{port}" : connector)}");
            }
            return discovered.ToArray();
        }
        finally { Worker.Kill(process); }
    }
}
