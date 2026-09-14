using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OwlServer.Config;
using OwlServer.Models;
using OwlServer.Network;
using OwlServer.Services;
using OwlServer.Utils;

namespace OwlServer.Hardware;

/// <summary>
/// Bridge to a safe test/simulator device (LED / servo demo) on port 6001 - dev
/// plan §1 "안전 범위" and §29 rule 15/16: this project never implements real
/// weapon-firing control, only status labels ("idle"/"detected"/"approved"/"stopped")
/// for a bench-test rig.
///
/// One-directional by design: the server pushes hw_state messages out; any bytes
/// the test device sends back are only used to detect disconnection, never parsed
/// as a control input.
/// </summary>
public sealed class ArduinoBridge(IOptions<ServerSettings> settings, LogService logService)
    : TcpServerBase(settings.Value.Ports.ArduinoPort, "ArduinoBridge")
{
    private readonly ConcurrentDictionary<Guid, ClientSession> _sessions = new();

    protected override async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var session = new ClientSession(client);
        _sessions[session.Id] = session;
        logService.LogClientConnected("Test hardware", session.RemoteEndPoint);
        logService.SetArduinoConnected(true);

        try
        {
            var idleJson = JsonSerializer.Serialize(new HwStateMessage { State = HwState.Idle });
            Logger.Info($"[SEND HW {session.RemoteEndPoint}] {idleJson}");
            await session.SendMessageAsync(idleJson, ct).ConfigureAwait(false);

            // Read loop exists only to notice disconnection; incoming bytes are discarded.
            var buffer = new byte[256];
            while (true)
            {
                var read = await session.Stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            _sessions.TryRemove(session.Id, out _);
            await session.DisposeAsync().ConfigureAwait(false);
            logService.LogClientDisconnected("Test hardware", session.RemoteEndPoint);
            if (_sessions.IsEmpty)
            {
                logService.SetArduinoConnected(false);
            }
        }
    }

    /// <summary>Sends a status label to every connected test-hardware client. Best-effort:
    /// a failed send just drops that one client, it never throws back to the caller
    /// (dev plan §29 rule 12 - one client's failure must not affect others).</summary>
    public async Task BroadcastStateAsync(string state, CancellationToken ct)
    {
        if (_sessions.IsEmpty)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new HwStateMessage { State = state });
        Logger.Info($"[SEND HW broadcast x{_sessions.Count}] {json}");
        foreach (var session in _sessions.Values)
        {
            try
            {
                await session.SendMessageAsync(json, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to send hw_state to {session.RemoteEndPoint}: {ex.Message}");
            }
        }
    }
}
