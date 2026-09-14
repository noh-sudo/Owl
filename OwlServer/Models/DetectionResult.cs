using System.Text.Json.Serialization;

namespace OwlServer.Models;

/// <summary>
/// Raspberry Pi -> Server, Data Socket (port 5000), API spec IF-PI-SRV-002.
/// Sent once per "no-detection -> detection" transition, JSON followed by the
/// first-detection JPEG frame as the OWLD blob (see PacketProtocol/OwlMessage).
/// </summary>
public sealed class DetectionEventMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "detection_event";

    [JsonPropertyName("event_id")]
    public long EventId { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("detections")]
    public List<DetectionObject> Detections { get; set; } = [];
}
