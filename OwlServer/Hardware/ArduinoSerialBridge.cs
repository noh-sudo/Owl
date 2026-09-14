using System.IO.Ports;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OwlServer.Config;
using OwlServer.Models;
using OwlServer.Utils;

namespace OwlServer.Hardware;

/// <summary>
/// Server -> Arduino serial (COM port) channel - the counterpart to the
/// TCP-based <see cref="ArduinoBridge"/>, which still carries hw_state status
/// labels over port 6001 for the test-hardware bridge. This class carries two
/// kinds of plain, newline-terminated lines instead, matching 요구사항명세서
/// 서버-07 / 아두이노-01,02 ("좌표 데이터를 수신하고 파싱한다"):
///
///   "POS,x,y\n"                     - target coordinate (SendTargetCoordinate,
///                                      SendCoordinatesAsync)
///   "DEC,APPROVED\n" / "DEC,STOPPED\n" - WPF operator's approve/stop decision
///                                      (SendDecision, API spec IF-WPF-SRV-002
///                                      DecisionMessage.Approved). Status label
///                                      only, same as HwState.Approved/Stopped
///                                      sent over the TCP bridge - this does not
///                                      implement any actual firing control (dev
///                                      plan §1 안전 범위).
///
/// This plain-line format is used rather than the OWL1/OWLD framing used
/// elsewhere: that framing exists to solve TCP's lack of message boundaries for
/// JSON/binary payloads, but a single serial link carries exactly one stream to
/// one microcontroller, and the Arduino sketch reads with
/// Serial.readStringUntil('\n') + a split on ',' - no length prefix or JSON
/// parser needed on that side. The sketch is expected to reply with an ACK line
/// ("ACK,...") to any of the above.
///
/// Every open/write runs on a background Task rather than the calling thread, so
/// a slow, busy, or momentarily unplugged port can never stall detection
/// handling (dev plan §26/§29 rule 12,13 - one component's slowness must not
/// stall another). A missing/unplugged port never crashes the server: open
/// failures are logged (once, until the next successful open) and further sends
/// are silently skipped until the port becomes available again.
/// </summary>
public sealed class ArduinoSerialBridge : IDisposable
{
    private const int FrameWidth = 640;
    private const int FrameHeight = 480;

    private readonly SerialPort _port;
    private readonly Lock _lock = new();
    private bool _loggedOpenFailureOnce;

    public ArduinoSerialBridge(IOptions<ServerSettings> settings)
    {
        var config = settings.Value.ArduinoSerial;
        _port = new SerialPort(config.PortName, config.BaudRate)
        {
            NewLine = "\n",
            Encoding = Encoding.ASCII,
            WriteTimeout = 500
        };

        // Best-effort at startup so the log shows connection state up front, same
        // as the other listeners logging "listening on port X". Off the calling
        // (startup) thread for the same reason as SendTargetCoordinate below.
        Task.Run(() =>
        {
            lock (_lock)
            {
                TryOpen();
            }
        });
    }

    /// <summary>Queues "POS,x,y" to be written to the serial port. Fire-and-forget:
    /// returns immediately regardless of whether the port is currently open.</summary>
    public void SendTargetCoordinate(int x, int y)
    {
        Task.Run(() => WriteCoordinateLine(x, y));
    }

    /// <summary>
    /// Queues "DEC,APPROVED" or "DEC,STOPPED" to be written to the serial port,
    /// mirroring the WPF operator's approve/stop decision (API spec IF-WPF-SRV-002,
    /// DecisionMessage.Approved) straight to the Arduino/test rig over serial -
    /// same fire-and-forget pattern as SendTargetCoordinate. This is a status
    /// label only, same as HwState.Approved/Stopped sent over the TCP bridge
    /// (ArduinoBridge) - it does not implement any actual firing control (dev
    /// plan §1 안전 범위).
    /// </summary>
    public void SendDecision(bool approved)
    {
        Task.Run(() => WriteDecisionLine(approved));
    }

    /// <summary>
    /// Replays a batch of coordinates - the shape produced by
    /// dummy_data/arduino_coordinates_dummy.json - to the Arduino, pacing each
    /// send by the gap between consecutive timestamp_ms values so the servo sees
    /// the same cadence the frames were captured/recorded at. Pass in the raw
    /// JSON string as-is (e.g. File.ReadAllText of the dummy data file, or a
    /// future API payload with the same "frames" shape).
    /// </summary>
    public async Task SendCoordinatesAsync(string json)
    {
        CoordinatePacket? packet;
        try
        {
            packet = JsonSerializer.Deserialize<CoordinatePacket>(json);
        }
        catch (JsonException ex)
        {
            Logger.Error("[ArduinoSerialBridge] malformed coordinate packet JSON, aborting playback.", ex);
            return;
        }

        if (packet?.Frames is not { Count: > 0 } frames)
        {
            Logger.Warn("[ArduinoSerialBridge] no coordinate frames to send.");
            return;
        }

        var previousTimestampMs = 0;
        foreach (var frame in frames)
        {
            // timestamp_ms에 맞춰 프레임 간격 유지
            var delayMs = frame.TimestampMs - previousTimestampMs;
            if (delayMs > 0)
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
            }

            WriteCoordinateLine(frame.X, frame.Y, frame.Frame, frame.TimestampMs);
            previousTimestampMs = frame.TimestampMs;
        }

        Logger.Info("[ArduinoSerialBridge] coordinate playback complete.");
    }

    private void WriteCoordinateLine(int x, int y, int? frameNumber = null, int? timestampMs = null)
    {
        lock (_lock)
        {
            if (!_port.IsOpen && !TryOpen())
            {
                return;
            }

            var clampedX = Math.Clamp(x, 0, FrameWidth);
            var clampedY = Math.Clamp(y, 0, FrameHeight);
            var message = $"POS,{clampedX},{clampedY}";

            try
            {
                _port.WriteLine(message);
                var context = frameNumber is { } f ? $"frame={f}, time={timestampMs}ms, " : "";
                Logger.Info($"[SEND HW-Serial {_port.PortName}] TX | {context}{message}");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
            {
                Logger.Warn($"Failed to write to serial port {_port.PortName}: {ex.Message}");
                CloseQuietly();
            }
        }
    }

    private void WriteDecisionLine(bool approved)
    {
        lock (_lock)
        {
            if (!_port.IsOpen && !TryOpen())
            {
                return;
            }

            var message = $"DEC,{(approved ? "APPROVED" : "STOPPED")}";

            try
            {
                _port.WriteLine(message);
                Logger.Info($"[SEND HW-Serial {_port.PortName}] TX | {message}");
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
            {
                Logger.Warn($"Failed to write to serial port {_port.PortName}: {ex.Message}");
                CloseQuietly();
            }
        }
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private bool TryOpen()
    {
        if (_port.IsOpen)
        {
            return true;
        }

        try
        {
            // -= before += makes this idempotent regardless of how many times
            // TryOpen has previously succeeded/failed - never double-subscribed.
            _port.DataReceived -= OnArduinoDataReceived;
            _port.DataReceived += OnArduinoDataReceived;

            _port.Open();
            Logger.Info($"[ArduinoSerialBridge] opened {_port.PortName} @ {_port.BaudRate} baud");
            _loggedOpenFailureOnce = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!_loggedOpenFailureOnce)
            {
                Logger.Warn($"[ArduinoSerialBridge] could not open {_port.PortName}: {ex.Message} (Arduino not plugged in? coordinate sends will be skipped until it's available)");
                _loggedOpenFailureOnce = true;
            }
            return false;
        }
    }

    /// <summary>Logs whatever the Arduino sketch prints back over serial (e.g. "ACK,...").
    /// Runs on the SerialPort's own background thread, independent of the send path's
    /// lock - concurrent read/write on one SerialPort is safe (separate buffers).</summary>
    private void OnArduinoDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        try
        {
            var received = _port.ReadExisting().Trim();
            if (!string.IsNullOrWhiteSpace(received))
            {
                Logger.Info($"[RECV HW-Serial {_port.PortName}] RX | {received}");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Logger.Warn($"[ArduinoSerialBridge] failed to read from {_port.PortName}: {ex.Message}");
        }
    }

    /// <summary>Caller must hold <see cref="_lock"/>.</summary>
    private void CloseQuietly()
    {
        try
        {
            _port.DataReceived -= OnArduinoDataReceived;
            _port.Close();
        }
        catch
        {
            // Best-effort - the port is already in a bad state, nothing more to do.
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            CloseQuietly();
            _port.Dispose();
        }
    }
}
