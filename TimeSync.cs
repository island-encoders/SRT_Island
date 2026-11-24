using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

sealed class NtpClock(string host)
{
    internal static readonly DateTimeOffset Epoch = new(1900, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private (DateTimeOffset Utc, long Tick)? sample;
    private readonly SemaphoreSlim refresh = new(1);
    private readonly object sampleGate = new();

    public DateTimeOffset? Now
    {
        get
        {
            lock (sampleGate)
                return sample is { } clock && Stopwatch.GetElapsedTime(clock.Tick) < TimeSpan.FromMinutes(15)
                    ? clock.Utc + Stopwatch.GetElapsedTime(clock.Tick) : null;
        }
    }

    public async Task Run(Action<string> status, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await refresh.WaitAsync(token);
            try
            {
                bool due;
                lock (sampleGate) due = sample is not { } clock || Stopwatch.GetElapsedTime(clock.Tick) >= TimeSpan.FromMinutes(2);
                if (due)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        var addresses = await Dns.GetHostAddressesAsync(host, deadline.Token);
                        foreach (var address in addresses)
                        {
                            try
                            {
                                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                                var reading = await Query(address, attempt.Token);
                                lock (sampleGate) sample = reading;
                                break;
                            }
                            catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
                            { if (token.IsCancellationRequested) throw; }
                        }
                        if (Now is null) status("NTP server unavailable");
                    }
                    catch (Exception e) when (!token.IsCancellationRequested && (e is SocketException or OperationCanceledException))
                    { if (Now is null) status("NTP server unavailable"); }
                }
            }
            finally { refresh.Release(); }
            await Task.Delay(TimeSpan.FromSeconds(15), token);
        }
    }

    private static async Task<(DateTimeOffset, long)> Query(IPAddress address, CancellationToken token)
    {
        using var socket = new UdpClient(address.AddressFamily);
        socket.Connect(address, 123);
        var request = new byte[48];
        request[0] = 0x23;
        var utc = DateTimeOffset.UtcNow;
        var tick = Stopwatch.GetTimestamp();
        var elapsed = (utc - Epoch).TotalSeconds;
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(40), (uint)(long)elapsed);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(44), (uint)((elapsed % 1) * 4294967296));
        await socket.SendAsync(request, token);
        var reply = (await socket.ReceiveAsync(token)).Buffer;
        var received = Stopwatch.GetTimestamp();
        if (reply.Length < 48 || reply[0] >> 6 == 3 || (reply[0] & 7) != 4 || reply[1] is < 1 or > 15 ||
            !reply.AsSpan(24, 8).SequenceEqual(request.AsSpan(40, 8)) ||
            BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(32)) == 0 ||
            BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(40)) == 0)
            throw new IOException("Invalid NTP response.");
        var t2 = Decode(reply.AsSpan(32), utc);
        var t3 = Decode(reply.AsSpan(40), utc);
        var transit = Stopwatch.GetElapsedTime(tick, received) - (t3 - t2);
        if (transit < TimeSpan.FromMilliseconds(-2) || transit > TimeSpan.FromSeconds(1) || t3 < t2)
            throw new IOException("Invalid NTP round-trip time.");
        return (t3 + TimeSpan.FromTicks(Math.Max(0, transit.Ticks) / 2), received);
    }

    internal static DateTimeOffset Decode(ReadOnlySpan<byte> value, DateTimeOffset reference)
    {
        const double era = 4294967296;
        var seconds = BinaryPrimitives.ReadUInt32BigEndian(value);
        var fraction = BinaryPrimitives.ReadUInt32BigEndian(value[4..]);
        var epoch = Math.Round(((reference - Epoch).TotalSeconds - seconds) / era) * era;
        return Epoch + TimeSpan.FromSeconds(epoch + seconds + fraction / era);
    }
}

sealed class TimeSync(NtpClock clock, int delayMs, Action<string> status)
{
    private const int PacketSize = 188;
    private const int BatchSize = 384 * PacketSize;
    private static readonly byte[] StartCode = [0, 0, 1];
    private static readonly byte[] IslandEncoderUuid = Convert.FromHexString("DA3C78F60C464D3094FE450C49E226EA");
    private static readonly byte[] StampUuid = Convert.FromHexString("A5B3C2D1E4F56789ABCDEF0123456789");
    internal readonly record struct Stamp(ulong Value, bool UnixMicroseconds)
    {
        public DateTimeOffset? Utc(DateTimeOffset reference)
        {
            if (UnixMicroseconds)
                return Value <= (ulong)((DateTimeOffset.MaxValue - DateTimeOffset.UnixEpoch).Ticks / 10)
                    ? DateTimeOffset.UnixEpoch.AddTicks((long)Value * 10) : null;
            Span<byte> encoded = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(encoded, Value);
            return NtpClock.Decode(encoded, reference);
        }
    }

    private record Batch(byte[] Data, long? Pts, long? Dts, Stamp? Stamp, bool Discontinuity);

    public async Task Run(Stream source, Stream target, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Read ahead while the consumer waits on the common playout clock.
        var queue = Channel.CreateBounded<Batch>(new BoundedChannelOptions(600) { SingleReader = true, SingleWriter = true });
        var produce = Read(source, queue.Writer, stop.Token);
        var ntp = clock.Run(status, stop.Token);
        (long Pts, DateTimeOffset Utc)? anchor = null;
        var lastState = "";
        try
        {
            await foreach (var batch in queue.Reader.ReadAllAsync(token))
            {
                var now = clock.Now;
                var nextState = now is null ? "Waiting for NTP clock" : "Waiting for NTP SEI";
                if (batch.Discontinuity || now is null) anchor = null;
                if (now is { } utc && batch.Pts is { } pts && batch.Stamp is { } stamp)
                {
                    if (stamp.Utc(utc) is { } captured && Math.Abs((utc - captured).TotalSeconds) < 60)
                        anchor = (pts, captured);
                    else { anchor = null; nextState = "SEI clock differs from NTP"; }
                }

                if (now is { } current && anchor is { } sync && batch.Dts is { } dts)
                {
                    // Preserve decode order for streams whose presentation timestamps are reordered.
                    var delta = ClockDelta(dts, sync.Pts);
                    if (Math.Abs(delta) <= 90000 * 15)
                    {
                        var wait = sync.Utc + TimeSpan.FromSeconds(delta / 90000d) + TimeSpan.FromMilliseconds(delayMs) - current;
                        if (wait > TimeSpan.FromMilliseconds(delayMs + 1000))
                        {
                            anchor = null;
                            nextState = "SEI time is ahead of NTP";
                        }
                        else
                        {
                            nextState = wait < TimeSpan.FromMilliseconds(-50) ? "Late for sync" : "NTP SEI aligned";
                            if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
                        }
                    }
                    else anchor = null;
                }
                if (batch.Dts.HasValue && nextState != lastState)
                {
                    status(nextState);
                    lastState = nextState;
                }
                await target.WriteAsync(batch.Data, token);
            }
        }
        finally
        {
            stop.Cancel();
            try { await produce; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally
            {
                try { await ntp; }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
    }

    private static long ClockDelta(long value, long origin)
    {
        const long wrap = 1L << 33;
        return ((value - origin + wrap / 2) & (wrap - 1)) - wrap / 2;
    }

    private static async Task Read(Stream source, ChannelWriter<Batch> queue, CancellationToken token)
    {
        var packet = new byte[PacketSize];
        using var data = new MemoryStream(BatchSize);
        using var video = new MemoryStream(BatchSize);
        var videoPid = -1;
        long? pts = null, dts = null;
        var discontinuity = false;
        async Task Flush()
        {
            if (data.Length == 0) return;
            var stamp = FindStamp(video.GetBuffer().AsSpan(0, (int)video.Length));
            await queue.WriteAsync(new(data.ToArray(), pts, dts, stamp, discontinuity), token);
            data.SetLength(0);
            video.SetLength(0);
            discontinuity = false;
        }

        try
        {
            while (await source.ReadAtLeastAsync(packet, PacketSize, false, token) == PacketSize)
            {
                if (packet[0] != 0x47) throw new IOException("NTP sync requires a 188-byte MPEG-TS input.");
                var pid = ((packet[1] & 31) << 8) | packet[2];
                var adaptation = (packet[3] & 32) != 0;
                var payload = adaptation ? 5 + packet[4] : 4;
                var present = (packet[3] & 16) != 0 && payload < PacketSize;
                var start = (packet[1] & 64) != 0;
                var offset = payload;
                var videoStart = present && start && payload + 9 <= PacketSize && packet[payload] == 0 &&
                    packet[payload + 1] == 0 && packet[payload + 2] == 1 && (packet[payload + 3] & 0xf0) == 0xe0;
                if (videoStart && videoPid < 0) videoPid = pid;
                if (videoStart && pid == videoPid)
                {
                    await Flush();
                    var flags = packet[payload + 7] >> 6;
                    offset = payload + 9 + packet[payload + 8];
                    pts = flags >= 2 && payload + 14 <= PacketSize ? ReadPts(packet.AsSpan(payload + 9)) : null;
                    dts = flags == 3 && payload + 19 <= PacketSize ? ReadPts(packet.AsSpan(payload + 14)) : pts;
                    discontinuity = adaptation && packet[4] > 0 && (packet[5] & 128) != 0;
                }
                if (present && pid == videoPid && offset < PacketSize && (!start || videoStart))
                    video.Write(packet, offset, PacketSize - offset);
                data.Write(packet);
                if (data.Length == BatchSize) await Flush();
            }
            await Flush();
            queue.TryComplete();
        }
        catch (Exception error) { queue.TryComplete(error); }
    }

    private static long ReadPts(ReadOnlySpan<byte> bytes) =>
        ((long)(bytes[0] & 14) << 29) | ((long)bytes[1] << 22) | ((long)(bytes[2] & 254) << 14) |
        ((long)bytes[3] << 7) | (uint)(bytes[4] >> 1);

    internal static Stamp? FindStamp(ReadOnlySpan<byte> video)
    {
        for (var i = 0; i + 5 < video.Length; i++)
        {
            if (video[i] != 0 || video[i + 1] != 0 || video[i + 2] != 1) continue;
            var header = video[i + 3];
            var skip = (header & 31) == 6 ? 1 : (header >> 1) == 39 ? 2 : 0;
            if (skip == 0) continue;
            var start = i + 3 + skip;
            var end = video[start..].IndexOf(StartCode);
            var nal = end < 0 ? video[start..] : video.Slice(start, end);
            var rbsp = new byte[nal.Length];
            var count = 0;
            var zeros = 0;
            foreach (var b in nal)
            {
                if (zeros == 2 && b == 3) { zeros = 0; continue; }
                rbsp[count++] = b;
                zeros = b == 0 ? Math.Min(2, zeros + 1) : 0;
            }
            var pos = 0;
            while (pos + 1 < count)
            {
                var type = ReadSeiValue(rbsp.AsSpan(0, count), ref pos);
                var size = ReadSeiValue(rbsp.AsSpan(0, count), ref pos);
                if (size < 0 || size > count - pos) break;
                if (type == 5 && size >= 16)
                {
                    var uuid = rbsp.AsSpan(pos, 16);
                    if (size == 32 && uuid.SequenceEqual(StampUuid))
                        return new(BinaryPrimitives.ReadUInt64BigEndian(rbsp.AsSpan(pos + 24, 8)), false);
                    // Island Encoders carry a little-endian Unix timestamp in microseconds.
                    if (size == 26 && uuid.SequenceEqual(IslandEncoderUuid) && rbsp[pos + 16] == 0x0a && rbsp[pos + 17] == 0x01)
                        return new(BinaryPrimitives.ReadUInt64LittleEndian(rbsp.AsSpan(pos + 18, 8)), true);
                }
                pos += size;
            }
        }
        return null;
    }

    private static int ReadSeiValue(ReadOnlySpan<byte> data, ref int pos)
    {
        var value = 0;
        while (pos < data.Length)
        {
            var b = data[pos++];
            value += b;
            if (b != 255) return value;
        }
        return -1;
    }
}
