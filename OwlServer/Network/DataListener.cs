using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OwlServer.Config;
using OwlServer.Models;
using OwlServer.Services;
using OwlServer.Utils;

namespace OwlServer.Network;

/// <summary>
/// Raspberry Pi -> Server detection/data socket (port 5000, API spec IF-PI-SRV-002).
/// Reads OWLD messages; only "detection_event" is expected here, sent once per
/// no-detection -> detection transition (dev plan §7).
/// </summary>
public sealed class DataListener(IOptions<ServerSettings> settings, DetectionService detectionService, LogService logService)
    : TcpServerBase(settings.Value.Ports.RaspberryPiDataPort, "DataListener")
{
    protected override async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        logService.LogClientConnected("Raspberry Pi (data)", remote);
        logService.SetRaspberryPiConnected(true);

        try
        {
            var stream = client.GetStream();
            while (true)
            {
                var message = await PacketProtocol.ReadMessageAsync(stream, ct).ConfigureAwait(false);
                var blobNote = message.HasBlob ? $" (+blob {message.Blob.Length}B)" : "";
                Logger.Info($"[RECV Pi-Data] {message.Json}{blobNote}");
                await DispatchAsync(message, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            logService.LogClientDisconnected("Raspberry Pi (data)", remote);
            logService.SetRaspberryPiConnected(false);
        }
    }

    private async Task DispatchAsync(OwlMessage message, CancellationToken ct)
    {
        string type;
        try
        {
            type = JsonSerializer.Deserialize<MessageEnvelope>(message.Json)?.Type ?? string.Empty;
        }
        catch (JsonException ex)
        {
            Logger.Error("Malformed JSON on data socket, dropping.", ex);
            return;
        }

        switch (type)
        {
            case "detection_event":
                await detectionService.HandleDetectionEventAsync(message, ct).ConfigureAwait(false);
                break;
            default:
                Logger.Warn($"Unrecognized message type '{type}' on data socket, ignoring.");
                break;
        }
    }
}
