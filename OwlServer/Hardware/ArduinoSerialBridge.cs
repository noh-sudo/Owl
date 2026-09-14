using System.IO.Ports;
using System.Text;
using Microsoft.Extensions.Options;
using OwlServer.Config;
using OwlServer.Utils;

namespace OwlServer.Hardware;

/// <summary>
/// Server -> Arduino coordinate channel over a wired USB-serial (COM port)
/// connection, separate from the TCP-based <see cref="ArduinoBridge"/> which
/// still carries hw_state status labels over port 6001. Coordinates go over
/// serial because that's how the Arduino/turret hardware receives them,
/// matching 요구사항명세서 서버-07 / 아두이노-01,02 ("좌표 데이터를 수신하고
/// 파싱한다").
///
/// Wire format is a plain newline-terminated line - "x,y\n" - rather than the
/// OWL1/OWLD framing used elsewhere: that framing exists to solve TCP's lack of
/// message boundaries for JSON/binary payloads, but a single serial link carries
/// exactly one stream to one microcontroller, and Arduino sketches conventionally
/// read with Serial.readStringUntil('\n') + a simple split on ',' - no length
/// prefix or JSON parser needed on that side.
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

    /// <summary>Queues "x,y\n" to be written to the serial port. Fire-and-forget:
    /// returns immediately regardless of whether the port is currently open.</summary>
    public void SendTargetCoordinate(int x, int y)
    {
        Task.Run(() =>
        {
            lock (_lock)
            {
                if (!_port.IsOpen && !TryOpen())
                {
                    return;
                }

                var line = $"{x},{y}\n";
                try
                {
                    _port.Write(line);
                    Logger.Info($"[SEND HW-Serial {_port.PortName}] {line.TrimEnd()}");
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
                {
                    Logger.Warn($"Failed to write to serial port {_port.PortName}: {ex.Message}");
                    CloseQuietly();
                }
            }
        });
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

    private void CloseQuietly()
    {
        try
        {
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
