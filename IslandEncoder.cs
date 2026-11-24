using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

record EncoderLogin(string Address, string Username, string Password);
record EncoderChannel(bool Enabled, string Mode, string Host, int Port, int Latency);

sealed class IslandEncoder
{
    private readonly SemaphoreSlim gate = new(1);
    private HttpClient? client;

    public async Task<object> Connect(EncoderLogin login, CancellationToken token)
    {
        if (!Uri.TryCreate(login.Address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.PathAndQuery != "/" ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            string.IsNullOrWhiteSpace(login.Username) || string.IsNullOrWhiteSpace(login.Password))
            throw new InvalidOperationException("Enter the encoder’s HTTP or HTTPS address and sign-in details.");
        await gate.WaitAsync(token);
        try
        {
            var candidate = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false })
            {
                BaseAddress = uri, Timeout = TimeSpan.FromSeconds(8)
            };
            try
            {
                using var front = await candidate.GetAsync("/", token);
                front.EnsureSuccessStatusCode();
                using var response = await candidate.PostAsync("/link/action.php", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = login.Username, ["password"] = login.Password
                }), token);
                response.EnsureSuccessStatusCode();
                if (response.RequestMessage?.RequestUri?.AbsolutePath != "/dashboard.php")
                    throw new InvalidOperationException("The Island Encoder rejected the login.");
                var result = await Read(candidate, token);
                client?.Dispose();
                client = candidate;
                return result;
            }
            catch { candidate.Dispose(); throw; }
        }
        finally { gate.Release(); }
    }

    public async Task<object> Status(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return await Read(Connected(), token); }
        finally { gate.Release(); }
    }

    public async Task Disconnect(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { client?.Dispose(); client = null; }
        finally { gate.Release(); }
    }

    public async Task<object> Peers(string? tag, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var current = Connected();
            var filter = (tag ?? "").Trim();
            if (filter.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) filter = filter[4..];
            using var response = await current.PostAsync("/vpn_ajax.php", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["action"] = "getIslandPeers",
                ["tag"] = filter,
                ["show_all"] = filter.Length == 0 ? "true" : "false"
            }), token);
            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: token);
            if (payload?["result"]?["peers"] is not JsonArray peers)
                throw new InvalidOperationException("The Island Encoder’s VPN page could not report its peers.");
            return new
            {
                state = "Running",
                peers = peers.OfType<JsonObject>().Select(peer => new
                {
                    name = peer["name"]?.GetValue<string>() ?? "",
                    dns = "",
                    ips = peer["ip"] is JsonValue ip && !string.IsNullOrWhiteSpace(ip.GetValue<string>()) ? new[] { ip.GetValue<string>() } : [],
                    online = peer["online"]?.GetValue<bool>() ?? false,
                    lastSeen = (string?)null
                }).ToArray()
            };
        }
        finally { gate.Release(); }
    }

    public async Task<object> Configure(int id, EncoderChannel channel, CancellationToken token)
    {
        if (channel.Mode is not ("caller" or "listener" or "rendezvous") || channel.Port is < 1 or > 65535 ||
            channel.Latency is < 20 or > 8000 || string.IsNullOrWhiteSpace(channel.Host) ||
            Uri.CheckHostName(channel.Host.Trim('[', ']')) == UriHostNameType.Unknown)
            throw new InvalidOperationException("Check the encoder’s SRT address, mode, port and latency.");
        await gate.WaitAsync(token);
        try
        {
            var current = Connected();
            var config = await GetConfig(current, token);
            var output = config.FirstOrDefault(item => item?["id"]?.GetValue<int>() == id)?["stream"]?["srt"] as JsonObject
                ?? throw new InvalidOperationException("This input has no configurable primary SRT output.");
            output["enable"] = channel.Enabled;
            output["mode"] = channel.Mode;
            output["ip"] = channel.Host;
            output["port"] = channel.Port;
            output["latency"] = channel.Latency;
            using var response = await current.PostAsJsonAsync("/link/relay.php", new { url = "/conf/updateDefaultConf", data = config }, token);
            response.EnsureSuccessStatusCode();
            var saved = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: token);
            if (saved?["status"]?.GetValue<string>() != "success")
                throw new InvalidOperationException("The encoder could not confirm the change. Refresh its status before retrying.");
            return await Read(current, token);
        }
        finally { gate.Release(); }
    }

    private HttpClient Connected() => client ?? throw new InvalidOperationException("Connect to an Island Encoder first.");

    private static async Task<JsonArray> GetConfig(HttpClient current, CancellationToken token) =>
        await current.GetFromJsonAsync<JsonArray>($"/config/config.json?island={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}", token)
        ?? throw new InvalidOperationException("The Island Encoder did not return a channel configuration.");

    private static async Task<object> Read(HttpClient current, CancellationToken token)
    {
        var config = await GetConfig(current, token);
        using var response = await current.PostAsJsonAsync("/RPC", new
        {
            jsonrpc = "2.0", id = 1, method = "enc.getInputState", @params = Array.Empty<object>()
        }, token);
        response.EnsureSuccessStatusCode();
        var input = (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: token))?["result"] as JsonArray
            ?? throw new InvalidOperationException("The encoder’s input status service is unavailable.");
        return new
        {
            address = current.BaseAddress!.GetLeftPart(UriPartial.Authority),
            channels = config.Where(item => item?["stream"]?["srt"] is JsonObject).Select(item =>
            {
                var id = item!["id"]!.GetValue<int>();
                var state = input.FirstOrDefault(row => row?["chnId"]?.GetValue<int>() == id);
                var srt = item["stream"]!["srt"]!;
                return new
                {
                    id,
                    name = item["name"]?.GetValue<string>() ?? $"Input {id + 1}",
                    signal = state?["avalible"]?.GetValue<bool>() ?? false,
                    width = state?["width"], height = state?["height"], fps = state?["framerate"],
                    audioHz = state?["samplerate"], codec = item["encv"]?["codec"], audioCodec = item["enca"]?["codec"],
                    srt = new { enabled = srt["enable"], mode = srt["mode"], host = srt["ip"], port = srt["port"], latency = srt["latency"] }
                };
            }).ToArray()
        };
    }
}
