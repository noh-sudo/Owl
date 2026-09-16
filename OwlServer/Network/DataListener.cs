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
/// Reads OWLD messages. "detection_event" fires once per no-detection -> detection
/// transition (dev plan §7); "tracking_coordinate" is a separate, much higher-rate
/// stream (Pi-side rate-limited, but still up to ~10-15/sec) carrying no blob, used
/// purely to keep the Arduino servo tracking a target that's still in frame.
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
                var type = PeekType(message.Json);

                // tracking_coordinate arrives up to ~10-15x/sec - logging every one
                // would drown out everything else on the console. ArduinoSerialBridge
                // already logs each accepted "[SEND HW-Serial ...] TX | POS,x,y" write,
                // so the traffic is still visible, just not double-logged here.
                if (type != "tracking_coordinate")
                {
                    var blobNote = message.HasBlob ? $" (+blob {message.Blob.Length}B)" : "";
                    Logger.Info($"[RECV Pi-Data] {message.Json}{blobNote}");
                }

                await DispatchAsync(type, message, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            logService.LogClientDisconnected("Raspberry Pi (data)", remote);
            logService.SetRaspberryPiConnected(false);
        }
    }

    private static string PeekType(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MessageEnvelope>(json)?.Type ?? string.Empty;
        }
        catch (JsonException ex)
        {
            Logger.Error("Malformed JSON on data socket.", ex);
            return string.Empty;
        }
    }

    private async Task DispatchAsync(string type, OwlMessage message, CancellationToken ct)
    {
        switch (type)
        {
            case "detection_event":
                await detectionService.HandleDetectionEventAsync(message, ct).ConfigureAwait(false);
                break;
            case "tracking_coordinate":
                detectionService.HandleTrackingCoordinate(message.Json);
                break;
            case "":
                // Already logged by PeekType (malformed JSON) - nothing more to do.
                break;
            default:
                Logger.Warn($"Unrecognized message type '{type}' on data socket, ignoring.");
                break;
        }
    }
}
