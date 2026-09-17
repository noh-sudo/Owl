using System.Text.Json.Serialization;

namespace Owl1Client.Models;

// OWLD 제어/상태 패킷의 JSON 바디 - 서버 통신 프로토콜 스펙에 정의된 필드명을 그대로 사용한다.

public class LoginRequestMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "login";

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

public class LoginResultMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "login_result";

    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class DetectionLogMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "detection_log";

    [JsonPropertyName("l_id")]
    public int LId { get; set; }

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("datetime")]
    public DateTime DateTime { get; set; }
}

public class DecisionMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "decision";

    [JsonPropertyName("l_id")]
    public int LId { get; set; }

    [JsonPropertyName("approved")]
    public bool Approved { get; set; }
}

public class ChangeLedMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "change_led";

    [JsonPropertyName("color")]
    public string Color { get; set; } = string.Empty;
}

public class SystemStatusMessage
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
