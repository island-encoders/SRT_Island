using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

record EncoderEntry(Guid Id, string Name, string Address, string TailnetAddress, string Username = "", string Secret = "");
record EncoderEntryEdit(Guid Id, string Name, string Address, string TailnetAddress, string Username = "",
    string Password = "", bool ForgetPassword = false);
record EncoderConnection(bool Tailnet = false);

sealed class EncoderBook
{
    private readonly object gate = new();
    private readonly string file;
    private EncoderEntry[] entries;

    public EncoderBook(string directory)
    {
        file = Path.Combine(directory, "encoders.json");
        entries = File.Exists(file) ? JsonSerializer.Deserialize<EncoderEntry[]>(File.ReadAllText(file)) ?? [] : [];
    }

    public EncoderEntry[] List() { lock (gate) return entries; }

    public void Save(EncoderEntryEdit entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 100 ||
            !ValidAddress(entry.Address) ||
            (!string.IsNullOrWhiteSpace(entry.TailnetAddress) && !ValidAddress(entry.TailnetAddress)))
            throw new InvalidOperationException("Enter a name and valid HTTP or HTTPS addresses.");
        if (entry.Username is null || entry.Username.Length > 128 || entry.Password is null ||
            entry.Password.Length > 512 || (entry.Password.Length > 0 && string.IsNullOrWhiteSpace(entry.Username)))
            throw new InvalidOperationException("Enter a username when saving an encoder password.");
        lock (gate)
        {
            var id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id;
            var old = entries.FirstOrDefault(item => item.Id == id);
            var secret = !entry.ForgetPassword && old is not null && old.Username == entry.Username &&
                old.Address == entry.Address && old.TailnetAddress == entry.TailnetAddress ? old.Secret : "";
            if (entry.Password.Length > 0)
                secret = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(entry.Password),
                    id.ToByteArray(), DataProtectionScope.CurrentUser));
            var saved = new EncoderEntry(id, entry.Name, entry.Address, entry.TailnetAddress ?? "", entry.Username, secret);
            Store(entries.Where(item => item.Id != id).Append(saved).ToArray());
        }
    }

    public EncoderLogin Login(Guid id, bool tailnet)
    {
        EncoderEntry entry;
        lock (gate) entry = entries.FirstOrDefault(item => item.Id == id)
            ?? throw new InvalidOperationException("The encoder is no longer in the address book.");
        if (string.IsNullOrEmpty(entry.Secret))
            throw new InvalidOperationException("Edit the encoder to save its username and password.");
        try
        {
            var password = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(entry.Secret),
                id.ToByteArray(), DataProtectionScope.CurrentUser));
            var address = tailnet ? entry.TailnetAddress : entry.Address;
            if (string.IsNullOrWhiteSpace(address)) throw new InvalidOperationException("This encoder has no saved Tailscale address.");
            return new EncoderLogin(address, entry.Username, password);
        }
        catch (Exception error) when (error is CryptographicException or FormatException)
        { throw new InvalidOperationException("Re-enter the password for this Windows user."); }
    }

    public void Remove(Guid id) { lock (gate) Store(entries.Where(item => item.Id != id).ToArray()); }

    private void Store(EncoderEntry[] next)
    {
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(next, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
        entries = next;
    }

    private static bool ValidAddress(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme is ("http" or "https") && uri.PathAndQuery == "/" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    public static async Task<object> Tailnet(string? tag, CancellationToken token)
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (!File.Exists(executable)) throw new InvalidOperationException("The Windows Tailscale client is unavailable.");
        using var process = Process.Start(Worker.ProcessInfo(executable, ["status", "--json"]))
            ?? throw new InvalidOperationException("Could not read the Windows Tailscale client.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var raw = await output;
            await errors;
            if (process.ExitCode != 0 && raw.Length == 0) throw new InvalidOperationException("Tailscale status is unavailable.");
            var state = JsonNode.Parse(raw)?.AsObject() ?? throw new InvalidOperationException("Tailscale status is unavailable.");
            var peers = state["Peer"] as JsonObject ?? new JsonObject();
            return new
            {
                state = state["BackendState"]?.GetValue<string>() ?? "Unknown",
                peers = peers.Select(pair => pair.Value).Where(peer => peer is not null).Select(peer => new
                {
                    name = peer!["HostName"]?.GetValue<string>() ?? "",
                    dns = (peer["DNSName"]?.GetValue<string>() ?? "").TrimEnd('.'),
                    ips = ReadStrings(peer["TailscaleIPs"]),
                    tags = ReadStrings(peer["Tags"]),
                    online = peer["Online"]?.GetValue<bool>() ?? false,
                    lastSeen = peer["LastSeen"]?.GetValue<string>()
                }).Where(peer => string.IsNullOrWhiteSpace(tag) || peer.tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                  .OrderByDescending(peer => peer.online).ThenBy(peer => peer.name).ToArray()
            };
        }
        finally { Worker.Kill(process); }
    }

    private static string[] ReadStrings(JsonNode? node) =>
        node is JsonArray values ? values.OfType<JsonValue>().Select(value => value.GetValue<string>()).ToArray() : [];
}
