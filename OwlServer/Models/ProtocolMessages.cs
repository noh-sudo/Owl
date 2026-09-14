using System.Text.Json.Serialization;

namespace OwlServer.Models;

// JSON message DTOs for the OWLD (Data/Control) protocol between WPF <-> Server
// and Server -> Arduino/Test Hardware. Field names match the API spec exactly
// (snake_case), see 올빼미 개발계획서/Owl1_API_명세서.xlsx and owl_csharp_server_plan.md §17-18.

/// <summary>Used only to peek the "type" discriminator before picking a concrete DTO.</summary>
public sealed class MessageEnvelope
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

/// <summary>WPF -> Server (IF-WPF-SRV-001). Password is plaintext on the wire, hashed/verified then discarded.</summary>
public sealed class LoginMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "login";

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

/// <summary>Server -> WPF (IF-SRV-WPF-002).</summary>
public sealed class LoginResultMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "login_result";

    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

/// <summary>Server -> WPF (IF-SRV-WPF-003), sent after cam_log INSERT succeeds.</summary>
public sealed class DetectionLogMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "detection_log";

    [JsonPropertyName("l_id")]
    public int LId { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("datetime")]
    public string Datetime { get; set; } = string.Empty;
}

/// <summary>
/// WPF -> Server (IF-WPF-SRV-002). Development-stage approve/stop *decision* only -
/// this is not a weapon fire command; see dev plan §1 and §17 "안전 범위".
/// </summary>
public sealed class DecisionMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "decision";

    [JsonPropertyName("l_id")]
    public int LId { get; set; }

    [JsonPropertyName("approved")]
    public bool Approved { get; set; }
}

/// <summary>Server -> WPF (IF-SRV-WPF-004).</summary>
public sealed class SystemStatusMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "system_status";

    [JsonPropertyName("camera")]
    public bool Camera { get; set; }

    [JsonPropertyName("raspberry_pi")]
    public bool RaspberryPi { get; set; }

    [JsonPropertyName("arduino")]
    public bool Arduino { get; set; }
}

/// <summary>
/// Server -> Arduino/Test Hardware (IF-SRV-ARD-001). Carries a state label only
/// ("idle" / "detected" / "approved" / "stopped") - never an actuator/fire command.
/// </summary>
public sealed class HwStateMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "hw_state";

    [JsonPropertyName("state")]
    public string State { get; set; } = "idle";
}

public static class HwState
{
    public const string Idle = "idle";
    public const string Detected = "detected";
    public const string Approved = "approved";
    public const string Stopped = "stopped";
}
