namespace OwlServer.Config;

public sealed class ServerSettings
{
    public const string SectionName = "OwlServer";

    public PortSettings Ports { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    public MySqlSettings MySql { get; set; } = new();
    public ArduinoSerialSettings ArduinoSerial { get; set; } = new();
    public TrackingSettings Tracking { get; set; } = new();
}

public sealed class PortSettings
{
    public int RaspberryPiDataPort { get; set; } = 5000;
    public int RaspberryPiVideoPort { get; set; } = 5001;
    public int WpfPort { get; set; } = 6000;
    public int ArduinoPort { get; set; } = 6001;
}

public sealed class StorageSettings
{
    public string CaptureRootDirectory { get; set; } = "data/captures";
}

/// <summary>
/// Split into discrete fields (rather than one raw connection string) so the
/// password can be supplied purely through an environment variable
/// (OwlServer__MySql__Password) via the standard .NET configuration provider,
/// and never needs to be committed to appsettings.json.
/// </summary>
public sealed class MySqlSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string Database { get; set; } = "Owl";
    public string UserId { get; set; } = "owlAdmin";
    public string Password { get; set; } = string.Empty;
    public string SslMode { get; set; } = "Preferred";
    public int CommandTimeoutSeconds { get; set; } = 10;
}

/// <summary>
/// Server -> Arduino coordinate channel, spoken over a wired USB-serial (COM
/// port) connection. Separate from PortSettings.ArduinoPort (the existing TCP
/// hw_state status channel, unchanged) - target-tracking coordinates go over
/// serial instead, per 요구사항명세서 서버-07/아두이노-01,02.
///
/// PortName must be the COM port Windows assigned to the Arduino once it's
/// plugged in via USB (check Device Manager's "Ports (COM &amp; LPT)" - it
/// usually shows up as "Arduino Uno (COMn)" or a USB-serial chip name like
/// "USB-SERIAL CH340 (COMn)"). BaudRate must match whatever the Arduino sketch
/// configures via Serial.begin(...) - 9600 is a common default.
/// </summary>
public sealed class ArduinoSerialSettings
{
    public string PortName { get; set; } = "COM5";
    public int BaudRate { get; set; } = 9600;
}

/// <summary>
/// Server-side LED state machine driven by the Pi's tracking_coordinate stream
/// (single target only - no multi-object aggregation). SustainedSeconds is the
/// yellow -> red threshold (same track_id seen continuously that long);
/// LostTimeoutSeconds is how long with no tracking_coordinate at all before the
/// server considers the target gone and reports back to green. LostTimeoutSeconds
/// mirrors the Pi's own --cooldown default (3s) so a brief occlusion on the Pi
/// side doesn't also flicker the LED green on the server side.
/// </summary>
public sealed class TrackingSettings
{
    public double SustainedSeconds { get; set; } = 2.0;
    public double LostTimeoutSeconds { get; set; } = 3.0;
}
