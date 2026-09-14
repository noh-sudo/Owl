using System.Net.Sockets;

namespace OwlServer.Network;

/// <summary>
/// Wraps a single accepted TCP connection (dev plan §13 "ClientSession").
/// Writes are serialized through <see cref="_writeLock"/> because a WPF
/// connection can have both a video-broadcast writer and a JSON-message
/// writer running concurrently on the same underlying stream.
/// </summary>
public class ClientSession(TcpClient tcpClient) : IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public Guid Id { get; } = Guid.NewGuid();
    public DateTime ConnectedAt { get; } = DateTime.UtcNow;
    public TcpClient TcpClient { get; } = tcpClient;
    public NetworkStream Stream { get; } = tcpClient.GetStream();

    // Captured up front: TcpClient.Client can become null/throw once the socket
    // is disposed, but callers (e.g. disconnect-logging in a `finally` block)
    // still need the address after DisposeAsync has run.
    public string RemoteEndPoint { get; } = tcpClient.Client.RemoteEndPoint?.ToString() ?? "unknown";

    public async Task SendFrameAsync(byte[] jpeg, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PacketProtocol.WriteFrameAsync(Stream, jpeg, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task SendMessageAsync(string json, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PacketProtocol.WriteMessageAsync(Stream, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        Stream.Dispose();
        TcpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}
