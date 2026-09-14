using System.Collections.Concurrent;
using OwlServer.Network;
using OwlServer.Utils;

namespace OwlServer.Services;

/// <summary>
/// Tracks connected WPF clients and fans video frames / JSON messages out to all
/// of them (dev plan §13 "ClientBroadcastService"). Per-client backpressure and
/// the drop-oldest-frame policy live in <see cref="WpfClientSession"/> itself;
/// this class just owns the registry and forgets about disposal details.
/// </summary>
public sealed class ClientBroadcastService
{
    private readonly ConcurrentDictionary<Guid, WpfClientSession> _sessions = new();

    public int ConnectedCount => _sessions.Count;

    public void Register(WpfClientSession session)
    {
        session.Faulted += OnSessionFaulted;
        _sessions[session.Id] = session;
        Logger.Info($"WPF client registered: {session.RemoteEndPoint} (total {_sessions.Count})");
    }

    public void Unregister(WpfClientSession session)
    {
        if (_sessions.TryRemove(session.Id, out _))
        {
            session.Faulted -= OnSessionFaulted;
            Logger.Info($"WPF client unregistered: {session.RemoteEndPoint} (total {_sessions.Count})");
        }
    }

    public void BroadcastFrame(byte[] jpeg)
    {
        foreach (var session in _sessions.Values)
        {
            session.EnqueueFrame(jpeg);
        }
    }

    public void BroadcastMessage(string json)
    {
        if (_sessions.IsEmpty)
        {
            return;
        }

        Logger.Info($"[SEND WPF broadcast x{_sessions.Count}] {json}");
        foreach (var session in _sessions.Values)
        {
            session.EnqueueMessage(json);
        }
    }

    private void OnSessionFaulted(WpfClientSession session, Exception ex)
    {
        Logger.Warn($"WPF client send failed, dropping session {session.RemoteEndPoint}: {ex.Message}");
        Unregister(session);
        _ = session.DisposeAsync();
    }
}
