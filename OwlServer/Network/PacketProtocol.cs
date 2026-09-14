using System.Buffers.Binary;
using System.Text;

namespace OwlServer.Network;

/// <summary>
/// Thrown when a stream does not follow the OWL1/OWLD framing rules
/// (bad magic, size that exceeds sane limits, or the peer closing mid-packet).
/// </summary>
public sealed class OwlProtocolException : Exception
{
    public OwlProtocolException(string message) : base(message) { }
}

/// <summary>
/// A decoded OWLD (Data/Control) packet: JSON payload plus an optional binary blob.
/// BlobSize == 0 means Blob is empty (see plan doc section 18 - login/decision/status
/// messages always carry BlobSize = 0; only detection_event carries a JPEG blob).
/// </summary>
public sealed record OwlMessage(string Json, byte[] Blob)
{
    public bool HasBlob => Blob.Length > 0;
}

/// <summary>
/// Length-prefixed framing for the two wire protocols defined in the dev plan:
///
///   Video packet  ("OWL1"): Magic(4) + PayloadSize(4, big-endian) + JPEG bytes
///   Data/Control  ("OWLD"): Magic(4) + JsonSize(4, BE) + BlobSize(4, BE) + JSON(UTF-8) + Blob
///
/// TCP gives no message boundaries, so every read here first fills the header,
/// then reads exactly PayloadSize/JsonSize/BlobSize bytes - never relies on a
/// single Stream.Read returning the whole payload.
/// </summary>
public static class PacketProtocol
{
    public static readonly byte[] FrameMagic = Encoding.ASCII.GetBytes("OWL1");
    public static readonly byte[] MessageMagic = Encoding.ASCII.GetBytes("OWLD");

    private const int HeaderIntSize = 4;

    /// <summary>
    /// Sanity ceiling for any single payload/json/blob field. Nothing in this project
    /// legitimately sends more than a few MB in one packet; this just stops a corrupt
    /// or malicious length prefix from making the server allocate gigabytes.
    /// </summary>
    public const int MaxPayloadBytes = 32 * 1024 * 1024; // 32 MB

    // ---------------------------------------------------------------------
    // OWL1 - video frame packets
    // ---------------------------------------------------------------------

    public static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        await ReadMagicAsync(stream, FrameMagic, ct).ConfigureAwait(false);
        var payloadSize = await ReadInt32BigEndianAsync(stream, ct).ConfigureAwait(false);
        ValidateSize(payloadSize, nameof(payloadSize));
        return await ReadExactAsync(stream, payloadSize, ct).ConfigureAwait(false);
    }

    public static async Task WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        var header = new byte[HeaderIntSize + HeaderIntSize];
        FrameMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(HeaderIntSize, HeaderIntSize), jpeg.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(jpeg, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // OWLD - JSON (+ optional blob) data/control packets
    // ---------------------------------------------------------------------

    public static async Task<OwlMessage> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        await ReadMagicAsync(stream, MessageMagic, ct).ConfigureAwait(false);
        var jsonSize = await ReadInt32BigEndianAsync(stream, ct).ConfigureAwait(false);
        var blobSize = await ReadInt32BigEndianAsync(stream, ct).ConfigureAwait(false);
        ValidateSize(jsonSize, nameof(jsonSize));
        ValidateSize(blobSize, nameof(blobSize));

        var jsonBytes = await ReadExactAsync(stream, jsonSize, ct).ConfigureAwait(false);
        var blob = blobSize > 0
            ? await ReadExactAsync(stream, blobSize, ct).ConfigureAwait(false)
            : [];

        var json = Encoding.UTF8.GetString(jsonBytes);
        return new OwlMessage(json, blob);
    }

    public static async Task WriteMessageAsync(Stream stream, string json, ReadOnlyMemory<byte> blob, CancellationToken ct)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);

        var header = new byte[HeaderIntSize * 3];
        MessageMagic.CopyTo(header, 0);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(HeaderIntSize, HeaderIntSize), jsonBytes.Length);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(HeaderIntSize * 2, HeaderIntSize), blob.Length);

        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(jsonBytes, ct).ConfigureAwait(false);
        if (blob.Length > 0)
        {
            await stream.WriteAsync(blob, ct).ConfigureAwait(false);
        }
    }

    public static Task WriteMessageAsync(Stream stream, string json, CancellationToken ct) =>
        WriteMessageAsync(stream, json, ReadOnlyMemory<byte>.Empty, ct);

    // ---------------------------------------------------------------------
    // Shared low-level helpers
    // ---------------------------------------------------------------------

    private static async Task ReadMagicAsync(Stream stream, byte[] expected, CancellationToken ct)
    {
        var actual = await ReadExactAsync(stream, expected.Length, ct).ConfigureAwait(false);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new OwlProtocolException(
                $"Invalid magic: expected \"{Encoding.ASCII.GetString(expected)}\", got \"{Encoding.ASCII.GetString(actual)}\"");
        }
    }

    private static async Task<int> ReadInt32BigEndianAsync(Stream stream, CancellationToken ct)
    {
        var bytes = await ReadExactAsync(stream, HeaderIntSize, ct).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32BigEndian(bytes);
    }

    private static void ValidateSize(int size, string fieldName)
    {
        if (size < 0 || size > MaxPayloadBytes)
        {
            throw new OwlProtocolException($"{fieldName} out of range: {size}");
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes, or throws if the peer
    /// closes the connection before delivering them all.
    /// </summary>
    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        if (count == 0)
        {
            return [];
        }

        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new OwlProtocolException("Connection closed while reading a packet.");
            }
            offset += read;
        }
        return buffer;
    }
}
