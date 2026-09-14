using System.Text.Json.Serialization;

namespace OwlServer.Models;

/// <summary>
/// A batch of target-tracking coordinate frames to replay to Arduino over serial
/// (see Hardware/ArduinoSerialBridge.cs SendCoordinatesAsync). Matches the shape
/// of dummy_data/arduino_coordinates_dummy.json - extra top-level fields in that
/// file (resolution, fps_equivalent, duration_seconds, ...) are simply ignored by
/// System.Text.Json since they have no matching property here.
/// </summary>
public sealed class CoordinatePacket
{
    [JsonPropertyName("frames")]
    public List<CoordinateFrame> Frames { get; set; } = [];
}

public sealed class CoordinateFrame
{
    [JsonPropertyName("frame")]
    public int Frame { get; set; }

    [JsonPropertyName("timestamp_ms")]
    public int TimestampMs { get; set; }

    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }
}
