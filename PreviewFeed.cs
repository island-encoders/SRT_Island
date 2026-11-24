using System.IO.Pipes;

sealed class PreviewFeed
{
    private readonly object gate = new();
    private byte[]? frame;
    private long updated;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public byte[]? Current()
    {
        lock (gate) return Environment.TickCount64 - updated < 4000 ? frame : null;
    }

    public void Publish(byte[]? next)
    {
        lock (gate)
        {
            frame = next;
            updated = Environment.TickCount64;
            changed.TrySetResult();
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public async Task Stream(HttpContext context)
    {
        var token = context.RequestAborted;
        context.Response.ContentType = "multipart/x-mixed-replace; boundary=island-frame";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.StartAsync(token);
        byte[]? previous = null;
        while (!token.IsCancellationRequested)
        {
            byte[]? next;
            Task wait;
            lock (gate) { next = frame; wait = changed.Task; }
            if (next is null || ReferenceEquals(next, previous))
            {
                await wait.WaitAsync(token);
                continue;
            }
            await context.Response.WriteAsync($"--island-frame\r\nContent-Type: image/jpeg\r\nContent-Length: {next.Length}\r\n\r\n", token);
            await context.Response.Body.WriteAsync(next, token);
            await context.Response.WriteAsync("\r\n", token);
            await context.Response.Body.FlushAsync(token);
            previous = next;
        }
    }

    public async Task Read(NamedPipeServerStream pipe, CancellationToken token)
    {
        await pipe.WaitForConnectionAsync(token);
        using var jpeg = new MemoryStream();
        var buffer = new byte[65536];
        var previous = -1;
        while (await pipe.ReadAsync(buffer, token) is var length && length > 0)
        {
            for (var i = 0; i < length; i++)
            {
                var value = buffer[i];
                if (jpeg.Length == 0 && previous == 0xff && value == 0xd8) jpeg.WriteByte(0xff);
                if (jpeg.Length > 0)
                {
                    jpeg.WriteByte(value);
                    if (previous == 0xff && value == 0xd9)
                    {
                        Publish(jpeg.ToArray());
                        jpeg.SetLength(0);
                    }
                    else if (jpeg.Length > 2_000_000) jpeg.SetLength(0);
                }
                previous = value;
            }
        }
    }
}
