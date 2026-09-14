using System.Text.Json.Serialization;

namespace OwlServer.Models;

/// <summary>
/// One detected object inside a detection_event message (API spec IF-PI-SRV-002).
///
/// X/Y are an extension beyond the currently-frozen API spec, added to carry
/// the target's tracking position through to Arduino (요구사항명세서 추론-04/05,
/// 서버-07, 아두이노-01/02). They are optional/nullable: existing Pi clients that
/// only send category+confidence keep working unchanged, and the server simply
/// skips the serial coordinate send when they're absent.
/// </summary>
public sealed class DetectionObject
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("x")]
    public int? X { get; set; }

    [JsonPropertyName("y")]
    public int? Y { get; set; }
}
