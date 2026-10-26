using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using NexusLink;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

if (args.Length == 2 && args[0] == "--adb-smoke")
{
    var search = new DirectoryInfo(AppContext.BaseDirectory);
    while (search != null && !Directory.Exists(Path.Combine(search.FullName, "pc-server-wpf"))) search = search.Parent;
    string root = search?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    string folder = Path.Combine(root, "pc-server-wpf", "scrcpy", "scrcpy-win64-v2.4");
    int received = 0;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using (var channel = await ClipboardChannel.ConnectAsync(Path.Combine(folder, "adb.exe"), Path.Combine(folder, "scrcpy-server"),
        args[1], args[1], _ => Interlocked.Increment(ref received), Console.WriteLine, timeout.Token))
    {
        await Task.Delay(3000);
        if (!channel.IsAlive) throw new Exception("Control-only clipboard channel stopped unexpectedly");
        Console.WriteLine("PASS authorized Android control-only clipboard handshake; no clipboard contents logged or replaced");
    }
    await Task.Delay(1000); // Allow ADB forward/server-file cleanup to finish.
    return;
}

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Reject<T>(Func<Task> run) where T : Exception
{
    try { await run(); } catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}

// Unicode, multiline clips, and coalesced messages survive arbitrary TCP fragmentation.
using (var memory = new MemoryStream())
{
    string clip = "CLIP:teks\nbaris kedua: 🧪 日本語 " + new string('x', 20000);
    await Wire.SendAsync(memory, clip);
    await Wire.SendAsync(memory, "STATS:free:20:LAN");
    using var fragmented = new FragmentedStream(memory.ToArray());
    Assert(await Wire.ReadAsync(fragmented) == clip, "Fragmented Unicode clipboard changed");
    Assert(await Wire.ReadAsync(fragmented) == "STATS:free:20:LAN", "Coalesced telemetry lost");
}
Console.WriteLine("PASS fragmented/coalesced Unicode and multiline frames");

foreach (int invalid in new[] { -1, 0, Wire.MaxPacket + 1 })
{
    byte[] header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, invalid);
    await Reject<InvalidDataException>(async () => await Wire.ReadAsync(new MemoryStream(header)));
}
await Reject<EndOfStreamException>(async () => await Wire.ReadAsync(new MemoryStream(new byte[] { 0, 0, 0, 5, 1 })));
Console.WriteLine("PASS invalid lengths and interrupted frames rejected");

var messages = new ConcurrentQueue<(string Category, string Message)>();
using var rsa = RSA.Create(2048);
var request = new CertificateRequest("CN=NexusLink Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
byte[] secret = RandomNumberGenerator.GetBytes(32);
using var security = new SecureTransport(certificate, secret);
var transfers = new FileTransfers(security, (category, message) => messages.Enqueue((category, message)));
async Task<SslStream> AuthenticateAsync(TcpClient client, byte[]? offeredSecret = null, bool correctPin = true)
{
    var stream = new SslStream(client.GetStream(), false, (_, cert, _, _) =>
        correctPin && cert != null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()), SHA256.HashData(certificate.RawData)));
    await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "NexusLink Test", EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
    await Wire.SendAsync(stream, "AUTH:" + Convert.ToHexString(offeredSecret ?? secret));
    Assert(await Wire.ReadAsync(stream) == "AUTH_OK", "Missing pairing acknowledgement");
    return stream;
}
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
string prefix = "nexus-check-" + Guid.NewGuid().ToString("N");
string directory = Path.Combine(AppContext.BaseDirectory, "received_files");
Directory.CreateDirectory(directory);
async Task Upload(string name, byte[] bytes, long? declaredSize = null)
{
    using var client = new TcpClient();
    await client.ConnectAsync(IPAddress.Loopback, port);
    var receive = transfers.ReceiveAsync(await listener.AcceptTcpClientAsync());
    using var stream = await AuthenticateAsync(client);
    await Wire.SendAsync(stream, JsonSerializer.Serialize(new { name, size = declaredSize ?? bytes.LongLength }));
    // Tiny fragments exercise metadata/data arriving independently.
    for (int i = 0; i < Math.Min(128, bytes.Length); i++) await stream.WriteAsync(bytes.AsMemory(i, 1));
    if (bytes.Length > 128) await stream.WriteAsync(bytes.AsMemory(128));
    if (declaredSize > bytes.Length) { client.Client.Shutdown(SocketShutdown.Send); await receive; return; }
    Assert(await Wire.ReadAsync(stream) == "OK", "Missing receipt acknowledgement");
    await receive;
}

try
{
    byte[] payload = new byte[1024 * 1024 + 17]; Random.Shared.NextBytes(payload);
    await Task.WhenAll(Upload(prefix + "-a.bin", payload), Upload(prefix + "-b.bin", payload));
    Assert(File.ReadAllBytes(Path.Combine(directory, prefix + "-a.bin")).SequenceEqual(payload), "Concurrent upload A corrupted");
    Assert(File.ReadAllBytes(Path.Combine(directory, prefix + "-b.bin")).SequenceEqual(payload), "Concurrent upload B corrupted");
    await Upload(prefix + "-empty.bin", Array.Empty<byte>());
    Assert(new FileInfo(Path.Combine(directory, prefix + "-empty.bin")).Length == 0, "Empty file failed");
    await Upload(prefix + "-a.bin", new byte[] { 1, 2, 3 });
    Assert(File.ReadAllBytes(Path.Combine(directory, prefix + "-a.bin")).SequenceEqual(payload), "Duplicate upload overwrote original");
    int errors = messages.Count(m => m.Category == "Error");
    await Upload(prefix + "-broken.bin", new byte[] { 1 }, 100);
    Assert(!File.Exists(Path.Combine(directory, prefix + "-broken.bin")), "Truncated file marked complete");
    Assert(messages.Count(m => m.Category == "Error") == errors + 1, "Truncated upload did not report failure");
    Assert(!Directory.GetFiles(directory, "*.partial").Any(), "Incomplete file leaked");
    Console.WriteLine("PASS concurrent uploads, byte integrity, empty files, duplicate names, truncated cleanup");

    string source = Path.Combine(directory, prefix + "-a.bin");
    string offer = transfers.Offer(source, "127.0.0.1");
    using var meta = JsonDocument.Parse(offer.Substring(10));
    string id = meta.RootElement.GetProperty("id").GetString()!;
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, port);
        var sending = transfers.SendAsync(await listener.AcceptTcpClientAsync());
        using var stream = await AuthenticateAsync(client);
        await Wire.SendAsync(stream, id);
        byte[] received = new byte[payload.Length];
        await stream.ReadExactlyAsync(received);
        Assert(received.SequenceEqual(payload), "Download payload changed");
        await Wire.SendAsync(stream, "OK");
        await sending;
    }
    using (var client = new TcpClient())
    {
        await client.ConnectAsync(IPAddress.Loopback, port);
        var replay = transfers.SendAsync(await listener.AcceptTcpClientAsync());
        using var stream = await AuthenticateAsync(client);
        await Wire.SendAsync(stream, id);
        await replay;
        Assert(await stream.ReadAsync(new byte[1]) == 0, "One-time download token replayed");
    }
    Console.WriteLine("PASS downloads, receiver ACK, one-time transfer IDs");
}
finally
{
    foreach (string file in Directory.GetFiles(directory, prefix + "*")) File.Delete(file);
}
// Both identities must match before any application data is accepted.
using (var client = new TcpClient())
{
    await client.ConnectAsync(IPAddress.Loopback, port);
    var rejecting = security.AcceptAsync(await listener.AcceptTcpClientAsync());
    await Reject<IOException>(async () => await AuthenticateAsync(client, RandomNumberGenerator.GetBytes(32)));
    await Reject<AuthenticationException>(async () => await rejecting);
}
using (var client = new TcpClient())
{
    await client.ConnectAsync(IPAddress.Loopback, port);
    var rejecting = security.AcceptAsync(await listener.AcceptTcpClientAsync());
    await Reject<AuthenticationException>(async () => await AuthenticateAsync(client, correctPin: false));
    try { await rejecting; throw new Exception("Mismatched certificate was accepted"); }
    catch (IOException) { }
    catch (AuthenticationException) { }
}
Console.WriteLine("PASS TLS encryption, pairing-secret rejection, and certificate-pin rejection");
Console.WriteLine("All transport checks passed.");

sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(buffer.Length, 3)], token);
}
