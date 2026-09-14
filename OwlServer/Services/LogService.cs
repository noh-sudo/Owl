using System.Text.Json;
using OwlServer.Models;
using OwlServer.Utils;

namespace OwlServer.Services;

/// <summary>
/// Tracks connectivity of the three external components (camera/Pi video socket,
/// Pi data socket, Arduino/test-hardware) and pushes a system_status message to
/// WPF whenever one changes (API spec IF-SRV-WPF-004; dev plan §26 error handling -
/// "Camera Status = OFF -> WPF 상태 업데이트"). Also centralizes the [INFO]/[WARN]
/// connect/disconnect log lines from dev plan §27.
/// </summary>
public sealed class LogService(ClientBroadcastService broadcastService)
{
    private readonly Lock _lock = new();
    private bool _cameraConnected;
    private bool _raspberryPiConnected;
    private bool _arduinoConnected;

    public void SetCameraConnected(bool connected) => SetAndBroadcast(ref _cameraConnected, connected, "Camera(video)");
    public void SetRaspberryPiConnected(bool connected) => SetAndBroadcast(ref _raspberryPiConnected, connected, "Raspberry Pi(data)");
    public void SetArduinoConnected(bool connected) => SetAndBroadcast(ref _arduinoConnected, connected, "Arduino/test-hardware");

    public void LogClientConnected(string role, string remoteEndPoint) =>
        Logger.Info($"{role} connected: {remoteEndPoint}");

    public void LogClientDisconnected(string role, string remoteEndPoint) =>
        Logger.Info($"{role} disconnected: {remoteEndPoint}");

    private void SetAndBroadcast(ref bool field, bool connected, string label)
    {
        bool changed;
        lock (_lock)
        {
            changed = field != connected;
            field = connected;
        }

        if (!changed)
        {
            return;
        }

        Logger.Info($"{label} connection state: {(connected ? "ON" : "OFF")}");
        BroadcastStatus();
    }

    private void BroadcastStatus()
    {
        SystemStatusMessage status;
        lock (_lock)
        {
            status = new SystemStatusMessage
            {
                Camera = _cameraConnected,
                RaspberryPi = _raspberryPiConnected,
                Arduino = _arduinoConnected
            };
        }

        broadcastService.BroadcastMessage(JsonSerializer.Serialize(status));
    }
}
