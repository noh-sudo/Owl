using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OwlServer.Config;
using OwlServer.Models;
using OwlServer.Services;
using OwlServer.Utils;

namespace OwlServer.Network;

/// <summary>
/// WPF client socket (port 6000). A single connection carries both directions:
/// the server pushes OWL1 video frames + OWLD messages (login result, detection
/// logs, system status) out via <see cref="WpfClientSession"/>'s queues, while
/// this listener's read loop only ever expects OWLD messages coming in
/// ("login", "decision") - the client never sends video (dev plan §9, §17).
/// </summary>
public sealed class WpfListener(
    IOptions<ServerSettings> settings,
    ClientBroadcastService broadcastService,
    AuthenticationService authenticationService,
    DetectionService detectionService,
    LogService logService)
    : TcpServerBase(settings.Value.Ports.WpfPort, "WpfListener")
{
    protected override async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var session = new WpfClientSession(client);
        logService.LogClientConnected("WPF client", session.RemoteEndPoint);

        broadcastService.Register(session);
        session.Start(ct);

        try
        {
            while (true)
            {
                var message = await PacketProtocol.ReadMessageAsync(session.Stream, ct).ConfigureAwait(false);
                Logger.Info($"[RECV WPF {session.RemoteEndPoint}] {message.Json}");
                await DispatchAsync(session, message, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            broadcastService.Unregister(session);
            await session.DisposeAsync().ConfigureAwait(false);
            logService.LogClientDisconnected("WPF client", session.RemoteEndPoint);
        }
    }

    private async Task DispatchAsync(WpfClientSession session, OwlMessage message, CancellationToken ct)
    {
        string type;
        try
        {
            type = JsonSerializer.Deserialize<MessageEnvelope>(message.Json)?.Type ?? string.Empty;
        }
        catch (JsonException ex)
        {
            Logger.Error("Malformed JSON from WPF client, dropping.", ex);
            return;
        }

        switch (type)
        {
            case "login":
                await HandleLoginAsync(session, message.Json, ct).ConfigureAwait(false);
                break;

            case "decision":
                await HandleDecisionAsync(session, message.Json, ct).ConfigureAwait(false);
                break;

            default:
                Logger.Warn($"Unrecognized message type '{type}' from WPF client {session.RemoteEndPoint}, ignoring.");
                break;
        }
    }

    private async Task HandleLoginAsync(WpfClientSession session, string json, CancellationToken ct)
    {
        var login = JsonSerializer.Deserialize<LoginMessage>(json);
        if (login is null || string.IsNullOrEmpty(login.Username))
        {
            Logger.Warn($"Malformed login message from {session.RemoteEndPoint}.");
            return;
        }

        var outcome = await authenticationService.LoginAsync(login.Username, login.Password, ct).ConfigureAwait(false);
        session.UserId = outcome.Success ? outcome.UserId : null;

        Logger.Info($"Login attempt for '{login.Username}' from {session.RemoteEndPoint}: {(outcome.Success ? "success" : "failure")}");

        var resultJson = JsonSerializer.Serialize(new LoginResultMessage { Success = outcome.Success });
        Logger.Info($"[SEND WPF {session.RemoteEndPoint}] {resultJson}");
        session.EnqueueMessage(resultJson);
    }

    private async Task HandleDecisionAsync(WpfClientSession session, string json, CancellationToken ct)
    {
        var decision = JsonSerializer.Deserialize<DecisionMessage>(json);
        if (decision is null)
        {
            Logger.Warn($"Malformed decision message from {session.RemoteEndPoint}.");
            return;
        }

        await detectionService.HandleDecisionAsync(session, decision, ct).ConfigureAwait(false);
    }
}
