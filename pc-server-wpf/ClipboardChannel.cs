using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace NexusLink;

/// <summary>Control-only client for the bundled scrcpy-server 2.4 protocol.
/// No video/audio encoder, Android Activity, overlay, or clipboard polling is needed.</summary>
internal sealed class ClipboardChannel : IDisposable
{
    private readonly string adb;
    private readonly string serial;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writer = new(1, 1);
    private TcpClient? client;
    private Process? server;
    private string? forward;
    private string? remoteJar;
    private string lastText = "";
    private volatile bool alive;
    internal bool IsAlive => alive && server?.HasExited == false;
    internal string Selector { get; }

    private ClipboardChannel(string adb, string serial, string selector) { this.adb = adb; this.serial = serial; Selector = selector; }
    private Process Launch(params string[] args)
    {
        var info = new ProcessStartInfo(adb) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-s"); info.ArgumentList.Add(serial);
        foreach (string arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException("ADB could not start");
    }
    private async Task<string> RunAsync(params string[] args)
    {
        using var process = Launch(args);
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        string result = await output;
        string error = await errors;
        if (process.ExitCode != 0) throw new IOException(error);
        return result.Trim();
    }

    internal static async Task<ClipboardChannel> ConnectAsync(string adb, string jar, string serial, string selector,
        Action<string> clipboard, Action<string> status, CancellationToken token)
    {
        if (!File.Exists(jar)) throw new FileNotFoundException("scrcpy-server 2.4 is missing", jar);
        var channel = new ClipboardChannel(adb, serial, selector);
        try
        {
            string id = Random.Shared.Next(1, int.MaxValue).ToString("x8");
            channel.remoteJar = $"/data/local/tmp/nexuslink-{id}.jar";
            await channel.RunAsync("push", jar, channel.remoteJar);
            string port = await channel.RunAsync("forward", "tcp:0", "localabstract:scrcpy_" + id);
            channel.forward = "tcp:" + port;
            channel.server = channel.Launch("shell", "CLASSPATH=" + channel.remoteJar, "app_process", "/",
                "com.genymobile.scrcpy.Server", "2.4", "scid=" + id, "tunnel_forward=true",
                "video=false", "audio=false", "control=true", "clipboard_autosync=true",
                "send_device_meta=false", "send_dummy_byte=true", "cleanup=false", "power_on=false");
            channel.server.OutputDataReceived += (_, _) => { };
            channel.server.ErrorDataReceived += (_, e) => { if (e.Data?.Contains("ERROR") == true) status(e.Data); };
            channel.server.BeginOutputReadLine(); channel.server.BeginErrorReadLine();
            for (int attempt = 0; attempt < 25; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var socket = new TcpClient { NoDelay = true };
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(1));
                    await socket.ConnectAsync("127.0.0.1", int.Parse(port), timeout.Token);
                    byte[] dummy = new byte[1];
                    await socket.GetStream().ReadExactlyAsync(dummy, timeout.Token);
                    channel.client = socket;
                    channel.alive = true;
                    _ = channel.ReceiveAsync(clipboard, status);
                    return channel;
                }
                catch (Exception) when (!token.IsCancellationRequested) { socket.Dispose(); }
                await Task.Delay(200, token);
            }
            throw new IOException("Clipboard ADB tidak tersambung; periksa otorisasi debugging HP.");
        }
        catch { channel.Dispose(); throw; }
    }

    private async Task ReceiveAsync(Action<string> clipboard, Action<string> status)
    {
        try
        {
            var stream = client!.GetStream();
            byte[] type = new byte[1];
            while (!lifetime.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(type, lifetime.Token);
                if (type[0] == 0)
                {
                    byte[] header = new byte[4];
                    await stream.ReadExactlyAsync(header, lifetime.Token);
                    int length = BinaryPrimitives.ReadInt32BigEndian(header);
                    if (length is < 0 or > 262139) throw new InvalidDataException("Invalid scrcpy clipboard length");
                    byte[] body = new byte[length];
                    await stream.ReadExactlyAsync(body, lifetime.Token);
                    lastText = Encoding.UTF8.GetString(body);
                    clipboard(lastText);
                }
                else if (type[0] == 1) { await stream.ReadExactlyAsync(new byte[8], lifetime.Token); }
                else throw new InvalidDataException("Unexpected scrcpy control message");
            }
        }
        catch (Exception e) { if (!lifetime.IsCancellationRequested) status("Clipboard terputus: " + e.Message); }
        finally { alive = false; }
    }

    internal async Task SendAsync(string text)
    {
        if (!IsAlive || text == lastText) return;
        byte[] body = Encoding.UTF8.GetBytes(text);
        if (body.Length > 262130) return;
        byte[] frame = new byte[14 + body.Length];
        frame[0] = 9; // SET_CLIPBOARD; sequence=0, paste=false. Never inject a paste key.
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(10), body.Length);
        body.CopyTo(frame, 14);
        await writer.WaitAsync(lifetime.Token);
        try { await client!.GetStream().WriteAsync(frame, lifetime.Token); lastText = text; }
        finally { writer.Release(); }
    }

    public void Dispose()
    {
        lifetime.Cancel(); alive = false;
        client?.Dispose();
        try { if (server?.HasExited == false) server.Kill(true); } catch (InvalidOperationException) { }
        server?.Dispose();
        _ = Task.Run(async () => {
            try { if (forward != null) await RunAsync("forward", "--remove", forward); } catch { }
            try { if (remoteJar != null) await RunAsync("shell", "rm", remoteJar); } catch { }
        });
    }
}
