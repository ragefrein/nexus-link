using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text.Json;
using System.IO;

namespace NexusLink;

internal sealed class FileTransfers
{
    private sealed record Pending(string Path, string Ip, long Size, DateTime Expires);
    private readonly ConcurrentDictionary<string, Pending> pending = new();
    private readonly Action<string, string> log;
    private readonly SemaphoreSlim slots = new(8, 8);
    private readonly SecureTransport security;
    internal FileTransfers(SecureTransport security, Action<string, string> log) { this.security = security; this.log = log; }
    internal string Offer(string path, string ip)
    {
        foreach (var item in pending.Where(p => p.Value.Expires < DateTime.UtcNow)) pending.TryRemove(item.Key, out _);
        if (pending.Count >= 128) throw new IOException("Too many pending transfers; wait for current transfers to finish");
        string id = Guid.NewGuid().ToString("N");
        long size = new FileInfo(path).Length;
        pending[id] = new(path, ip, size, DateTime.UtcNow.AddMinutes(2));
        return "FILE_SEND:" + JsonSerializer.Serialize(new { id, name = Path.GetFileName(path), size });
    }

    internal Task ReceiveAsync(TcpClient client) => RunLimitedAsync(client, () => ReceiveCoreAsync(client));
    internal Task SendAsync(TcpClient client) => RunLimitedAsync(client, () => SendCoreAsync(client));
    private async Task RunLimitedAsync(TcpClient client, Func<Task> transfer)
    {
        if (!slots.Wait(0)) { client.Dispose(); return; }
        try { await transfer(); }
        finally { slots.Release(); }
    }

    private async Task ReceiveCoreAsync(TcpClient client)
    {
        string? partial = null;
        using (client)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30)))
        {
            try
            {
                client.NoDelay = true;
                using var stream = await security.AcceptAsync(client, timeout.Token);
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                headerTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var meta = JsonDocument.Parse(await Wire.ReadAsync(stream, headerTimeout.Token));
                string name = Path.GetFileName(meta.RootElement.GetProperty("name").GetString() ?? "");
                long size = meta.RootElement.GetProperty("size").GetInt64();
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || size < 0)
                    throw new InvalidDataException("Invalid file metadata");
                string directory = Path.Combine(AppContext.BaseDirectory, "received_files");
                Directory.CreateDirectory(directory);
                partial = Path.Combine(directory, $".{Guid.NewGuid():N}.partial");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[256 * 1024];
                    long remaining = size;
                    while (remaining > 0)
                    {
                        int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), timeout.Token);
                        if (read == 0) throw new EndOfStreamException("Transfer interrupted");
                        await file.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                        remaining -= read;
                    }
                    await file.FlushAsync(timeout.Token);
                }
                // Never overwrite an existing user's file, including concurrent arrivals.
                string destination = Path.Combine(directory, name);
                try { File.Move(partial, destination); }
                catch (IOException) when (File.Exists(destination))
                {
                    destination = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(name)}_{Guid.NewGuid():N}{Path.GetExtension(name)}");
                    File.Move(partial, destination);
                }
                partial = null;
                await Wire.SendAsync(stream, "OK", timeout.Token);
                log("File", $"Received {Path.GetFileName(destination)} · {size / 1048576.0 / Math.Max(.001, clock.Elapsed.TotalSeconds):F1} MB/s");
            }
            catch (Exception ex) { log("Error", $"Receive failed: {ex.GetBaseException().Message}"); }
            finally { if (partial != null) { try { File.Delete(partial); } catch { } } }
        }
    }

    private async Task SendCoreAsync(TcpClient client)
    {
        using (client)
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(30)))
        {
            try
            {
                client.NoDelay = true;
                using var stream = await security.AcceptAsync(client, timeout.Token);
                using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                headerTimeout.CancelAfter(TimeSpan.FromSeconds(15));
                string id = await Wire.ReadAsync(stream, headerTimeout.Token);
                string ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                if (!pending.TryGetValue(id, out var offer) || offer.Ip != ip || offer.Expires < DateTime.UtcNow || !pending.TryRemove(id, out _))
                    throw new InvalidDataException("Transfer offer expired or belongs to another device");
                var clock = System.Diagnostics.Stopwatch.StartNew();
                await using var file = new FileStream(offer.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (file.Length != offer.Size) throw new IOException("Source file changed since it was selected; send it again");
                await file.CopyToAsync(stream, 256 * 1024, timeout.Token);
                if (await Wire.ReadAsync(stream, timeout.Token) != "OK") throw new IOException("Phone did not confirm receipt");
                log("File", $"Sent {Path.GetFileName(offer.Path)} · {file.Length / 1048576.0 / Math.Max(.001, clock.Elapsed.TotalSeconds):F1} MB/s");
            }
            catch (Exception ex) { log("Error", $"Send failed: {ex.Message}"); }
        }
    }
}
