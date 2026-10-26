using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.IO;

namespace NexusLink;

internal static class Wire
{
    internal const int MaxPacket = 1024 * 1024;
    private static readonly ConditionalWeakTable<Stream, SemaphoreSlim> Writers = new();

    internal static async Task SendAsync(Stream stream, string message, CancellationToken token = default)
    {
        byte[] body = Encoding.UTF8.GetBytes(message);
        if (body.Length is < 1 or > MaxPacket) throw new InvalidDataException("Message too large");
        byte[] frame = new byte[body.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length);
        body.CopyTo(frame, 4);
        var gate = Writers.GetValue(stream, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try { await stream.WriteAsync(frame, token); }
        finally { gate.Release(); }
    }

    internal static async Task<string> ReadAsync(Stream stream, CancellationToken token = default)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 1 or > MaxPacket) throw new InvalidDataException("Update NexusLink on both devices (protocol v2)");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, token);
        return Encoding.UTF8.GetString(body);
    }
}
