using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OwlServer.Config;
using OwlServer.Hardware;
using OwlServer.Models;
using OwlServer.Network;
using OwlServer.Repository;
using OwlServer.Storage;
using OwlServer.Utils;

namespace OwlServer.Services;

/// <summary>
/// Handles the event types the DB/UI/Arduino care about (dev plan §13
/// "DetectionService", §24, §25):
///   1. A Raspberry Pi detection_event: save the first-detection frame, INSERT
///      cam_log, tell WPF, tell the test-hardware bridge.
///   2. A Raspberry Pi tracking_coordinate: continuous coordinate relay to the
///      Arduino servo, plus a server-authoritative LED state machine (see
///      HandleTrackingCoordinate/CheckTrackingLost below).
///   3. A WPF operator decision (approve/stop): INSERT shoot_log, tell the
///      test-hardware bridge. This is a UI approval record only - it never
///      drives an actual firing mechanism (dev plan §1 안전 범위).
/// </summary>
public sealed class DetectionService : IDisposable
{
    private readonly ImageStorage imageStorage;
    private readonly CamLogRepository camLogRepository;
    private readonly ShootLogRepository shootLogRepository;
    private readonly ClientBroadcastService broadcastService;
    private readonly ArduinoBridge arduinoBridge;
    private readonly ArduinoSerialBridge arduinoSerialBridge;
    private readonly TrackingSettings trackingSettings;

    // ---- Server-side LED state machine (single target only, no multi-object
    // aggregation - see conversation with the user). Guarded by trackingLock
    // since HandleTrackingCoordinate (network thread) and the watchdog timer
    // (threadpool timer thread) both touch this state. ----
    private readonly Lock trackingLock = new();
    private readonly System.Threading.Timer lostWatchdog;
    private int? currentTrackId;
    private DateTime? trackFirstSeenAt;
    private DateTime lastTrackingSeenAt = DateTime.MinValue;
    private bool redSignaled;

    public DetectionService(
        ImageStorage imageStorage,
        CamLogRepository camLogRepository,
        ShootLogRepository shootLogRepository,
        ClientBroadcastService broadcastService,
        ArduinoBridge arduinoBridge,
        ArduinoSerialBridge arduinoSerialBridge,
        IOptions<ServerSettings> settings)
    {
        this.imageStorage = imageStorage;
        this.camLogRepository = camLogRepository;
        this.shootLogRepository = shootLogRepository;
        this.broadcastService = broadcastService;
        this.arduinoBridge = arduinoBridge;
        this.arduinoSerialBridge = arduinoSerialBridge;
        trackingSettings = settings.Value.Tracking;

        var period = TimeSpan.FromMilliseconds(500);
        lostWatchdog = new System.Threading.Timer(CheckTrackingLost, null, period, period);
    }

    public async Task HandleDetectionEventAsync(OwlMessage message, CancellationToken ct)
    {
        DetectionEventMessage detection;
        try
        {
            detection = JsonSerializer.Deserialize<DetectionEventMessage>(message.Json)
                        ?? throw new JsonException("null detection_event payload");
        }
        catch (JsonException ex)
        {
            Logger.Error("Malformed detection_event JSON, dropping.", ex);
            return;
        }

        if (detection.Detections.Count == 0)
        {
            Logger.Warn($"detection_event {detection.EventId} has no detections, dropping.");
            return;
        }

        if (!message.HasBlob)
        {
            Logger.Warn($"detection_event {detection.EventId} is missing its first-detection JPEG blob, dropping.");
            return;
        }

        var timestamp = ParseTimestamp(detection.Timestamp);
        var category = detection.Detections[0].Category;

        // File save must succeed before the DB row is written (dev plan §16).
        string thumbnailPath;
        try
        {
            thumbnailPath = await imageStorage
                .SaveAsync(message.Blob, timestamp, detection.EventId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error($"Failed to save capture image for detection_event {detection.EventId}", ex);
            return;
        }

        int lId;
        try
        {
            lId = await camLogRepository.InsertAsync(category, thumbnailPath, timestamp, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // dev plan §26: a DB failure must not take down video relay / the rest
            // of the pipeline - log it and stop here rather than throwing upward.
            Logger.Error($"cam_log INSERT failed for detection_event {detection.EventId}", ex);
            return;
        }

        Logger.Info($"cam_log inserted: l_id={lId}, category={category}");

        var logMessage = new DetectionLogMessage
        {
            LId = lId,
            Category = category,
            Datetime = timestamp.ToString("o", CultureInfo.InvariantCulture)
        };
        broadcastService.BroadcastMessage(JsonSerializer.Serialize(logMessage));

        await arduinoBridge.BroadcastStateAsync(HwState.Detected, ct).ConfigureAwait(false);

        var target = detection.Detections[0];
        if (target.X is { } x && target.Y is { } y)
        {
            arduinoSerialBridge.SendTargetCoordinate(x, y);
        }
    }

    /// <summary>
    /// Raspberry Pi -> Server tracking_coordinate: a continuous coordinate feed,
    /// independent of detection_event, so the Arduino servo keeps following the
    /// target instead of only moving once at first detection. No DB write, no
    /// per-message WPF broadcast - straight relay to the serial bridge
    /// (fire-and-forget, same as the coordinate sent from
    /// HandleDetectionEventAsync above). The coordinate relay happens
    /// regardless of whether track_id is present.
    ///
    /// track_id (when present) additionally drives a server-authoritative LED
    /// state machine: WPF already turns the LED yellow on its own when it gets
    /// detection_log (first detection). From here, once the *same* track_id has
    /// been seen continuously for Tracking.SustainedSeconds, WPF is told to go
    /// red (change_led) - and if no tracking_coordinate arrives at all for
    /// Tracking.LostTimeoutSeconds, CheckTrackingLost (the watchdog timer) tells
    /// it to go back to green. Multi-object aggregation is intentionally not
    /// handled - only a single "current" track_id is tracked at a time.
    /// </summary>
    public void HandleTrackingCoordinate(string json)
    {
        TrackingCoordinateMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<TrackingCoordinateMessage>(json);
        }
        catch (JsonException ex)
        {
            Logger.Error("Malformed tracking_coordinate JSON, dropping.", ex);
            return;
        }

        if (message is null)
        {
            return;
        }

        arduinoSerialBridge.SendTargetCoordinate(message.X, message.Y);

        if (message.TrackId is not { } trackId)
        {
            // Tracker hasn't confirmed an ID for this box yet - coordinate still
            // relayed above, just not counted toward the sustained-tracking timer.
            return;
        }

        bool justSustained;
        lock (trackingLock)
        {
            var now = DateTime.UtcNow;
            lastTrackingSeenAt = now;

            if (currentTrackId != trackId)
            {
                // New target - either the first one ever, or a different
                // track_id took over as the primary tracked object.
                currentTrackId = trackId;
                trackFirstSeenAt = now;
                redSignaled = false;
            }

            justSustained = !redSignaled
                && trackFirstSeenAt is { } firstSeen
                && (now - firstSeen) >= TimeSpan.FromSeconds(trackingSettings.SustainedSeconds);

            if (justSustained)
            {
                redSignaled = true;
            }
        }

        if (justSustained)
        {
            broadcastService.BroadcastMessage(JsonSerializer.Serialize(new ChangeLedMessage { Color = LedColor.Red }));
            Logger.Info($"track_id={trackId} sustained >= {trackingSettings.SustainedSeconds}s, LED -> red");
        }
    }

    /// <summary>Watchdog tick (every 500ms): if the current track hasn't sent a
    /// tracking_coordinate in Tracking.LostTimeoutSeconds, consider it gone and
    /// tell WPF to go back to green.</summary>
    private void CheckTrackingLost(object? state)
    {
        var justLost = false;
        lock (trackingLock)
        {
            if (currentTrackId is not null &&
                DateTime.UtcNow - lastTrackingSeenAt >= TimeSpan.FromSeconds(trackingSettings.LostTimeoutSeconds))
            {
                currentTrackId = null;
                trackFirstSeenAt = null;
                redSignaled = false;
                justLost = true;
            }
        }

        if (justLost)
        {
            broadcastService.BroadcastMessage(JsonSerializer.Serialize(new ChangeLedMessage { Color = LedColor.Green }));
            Logger.Info($"No tracking_coordinate for >= {trackingSettings.LostTimeoutSeconds}s, LED -> green");
        }
    }

    public async Task HandleDecisionAsync(WpfClientSession session, DecisionMessage decision, CancellationToken ct)
    {
        if (session.UserId is not { } userId)
        {
            Logger.Warn($"Decision from {session.RemoteEndPoint} rejected: not logged in.");
            return;
        }

        try
        {
            // Local time, matching cam_log.created_at (which stores the Pi's
            // wall-clock detection timestamp as-is, not normalized to UTC).
            var sId = await shootLogRepository
                .InsertAsync(userId, decision.LId, decision.Approved, DateTime.Now, ct)
                .ConfigureAwait(false);
            Logger.Info($"shoot_log inserted: s_id={sId}, u_id={userId}, l_id={decision.LId}, approved={decision.Approved}");
        }
        catch (Exception ex)
        {
            Logger.Error($"shoot_log INSERT failed for u_id={userId}, l_id={decision.LId}", ex);
            return;
        }

        await arduinoBridge
            .BroadcastStateAsync(decision.Approved ? HwState.Approved : HwState.Stopped, ct)
            .ConfigureAwait(false);

        arduinoSerialBridge.SendDecision(decision.Approved);
    }

    private static DateTime ParseTimestamp(string raw)
    {
        // Timestamp is for logging only, never used to pick a video frame (dev plan §11/§16).
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        Logger.Warn($"detection_event timestamp '{raw}' could not be parsed, using server time instead.");
        return DateTime.UtcNow;
    }

    public void Dispose() => lostWatchdog.Dispose();
}
