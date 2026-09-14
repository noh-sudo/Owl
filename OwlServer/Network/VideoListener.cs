using System.Net.Sockets;
using Microsoft.Extensions.Options;
using OwlServer.Config;
using OwlServer.Services;

namespace OwlServer.Network;

/// <summary>
/// Raspberry Pi -> Server video socket (port 5001, API spec IF-PI-SRV-001).
/// One-directional: reads OWL1 JPEG frames and hands each one to VideoService,
/// which relays it to WPF clients without decoding (dev plan §4, §9).
/// </summary>
public sealed class VideoListener(IOptions<ServerSettings> settings, VideoService videoService, LogService logService)
    : TcpServerBase(settings.Value.Ports.RaspberryPiVideoPort, "VideoListener")
{
    protected override async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        logService.LogClientConnected("Raspberry Pi (video)", remote);
        logService.SetCameraConnected(true);

        try
        {
            var stream = client.GetStream();
            while (true)
            {
                var jpeg = await PacketProtocol.ReadFrameAsync(stream, ct).ConfigureAwait(false);
                videoService.ReceiveFrame(jpeg);
            }
        }
        finally
        {
            logService.LogClientDisconnected("Raspberry Pi (video)", remote);
            logService.SetCameraConnected(false);
        }
    }
}
