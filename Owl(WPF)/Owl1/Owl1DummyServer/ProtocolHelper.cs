using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Owl1DummyServer;

/// <summary>
/// WPF 클라이언트와 동일한 OWL1(영상)/OWLD(제어) 프레이밍 프로토콜을 다시 구현한다.
/// 실제 서버 프로젝트와는 독립적인 코드지만, 통신 규약(Magic 헤더, Big-Endian 길이)은 동일하게 맞춘다.
/// </summary>
public static class ProtocolHelper
{
    public static async Task SendOwl1Async(NetworkStream stream, SemaphoreSlim sendLock, byte[] jpeg)
    {
        var buffer = new byte[4 + 4 + jpeg.Length];
        Encoding.ASCII.GetBytes("OWL1", 0, 4, buffer, 0);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4, 4), jpeg.Length);
        jpeg.CopyTo(buffer.AsSpan(8));

        await sendLock.WaitAsync();
        try
        {
            await stream.WriteAsync(buffer);
            await stream.FlushAsync();
        }
        finally
        {
            sendLock.Release();
        }
    }

    public static async Task SendOwldAsync(NetworkStream stream, SemaphoreSlim sendLock, byte[] json, byte[]? blob)
    {
        blob ??= Array.Empty<byte>();
        var buffer = new byte[4 + 4 + 4 + json.Length + blob.Length];
        Encoding.ASCII.GetBytes("OWLD", 0, 4, buffer, 0);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4, 4), json.Length);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(8, 4), blob.Length);
        json.CopyTo(buffer.AsSpan(12));
        blob.CopyTo(buffer.AsSpan(12 + json.Length));

        await sendLock.WaitAsync();
        try
        {
            await stream.WriteAsync(buffer);
            await stream.FlushAsync();
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>클라이언트가 보낸 OWLD 패킷 하나를 읽는다. 연결이 끊기면 null을 반환한다.</summary>
    public static async Task<(string Type, byte[] Json, byte[]? Blob)?> ReadOwldAsync(NetworkStream stream, CancellationToken token)
    {
        var magicBuffer = new byte[4];
        if (!await TryReadExactAsync(stream, magicBuffer, 4, token)) return null;

        var magic = Encoding.ASCII.GetString(magicBuffer);
        if (magic != "OWLD")
            throw new InvalidDataException($"클라이언트로부터 예상치 못한 Magic 헤더: {magic}");

        var lenBuffer = new byte[8];
        if (!await TryReadExactAsync(stream, lenBuffer, 8, token)) return null;
        var jsonSize = BinaryPrimitives.ReadInt32BigEndian(lenBuffer.AsSpan(0, 4));
        var blobSize = BinaryPrimitives.ReadInt32BigEndian(lenBuffer.AsSpan(4, 4));

        var jsonBytes = new byte[jsonSize];
        if (!await TryReadExactAsync(stream, jsonBytes, jsonSize, token)) return null;

        byte[]? blob = null;
        if (blobSize > 0)
        {
            blob = new byte[blobSize];
            if (!await TryReadExactAsync(stream, blob, blobSize, token)) return null;
        }

        using var doc = JsonDocument.Parse(jsonBytes);
        var type = doc.RootElement.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";
        return (type, jsonBytes, blob);
    }

    private static async Task<bool> TryReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken token)
    {
        var offset = 0;
        while (offset < count)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), token);
            }
            catch (IOException)
            {
                return false;
            }
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
