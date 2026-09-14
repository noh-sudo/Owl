using System.Globalization;
using System.Text.Json;
using OwlServer.Hardware;
using OwlServer.Models;
using OwlServer.Network;
using OwlServer.Repository;
using OwlServer.Storage;
using OwlServer.Utils;

namespace OwlServer.Services;

/// <summary>
/// Handles the two event types the DB/UI care about (dev plan §13 "DetectionService",
/// §24, §25):
///   1. A Raspberry Pi detection_event: save the first-detection frame, INSERT
///      cam_log, tell WPF, tell the test-hardware bridge.
///   2. A WPF operator decision (approve/stop): INSERT shoot_log, tell the
///      test-hardware bridge. This is a UI approval record only - it never
///      drives an actual firing mechanism (dev plan §1 안전 범위).
/// </summary>
public sealed class DetectionService(
    ImageStorage imageStorage,
    CamLogRepository camLogRepository,
    ShootLogRepository shootLogRepository,
    ClientBroadcastService broadcastService,
    ArduinoBridge arduinoBridge,
    ArduinoSerialBridge arduinoSerialBridge)
{
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
}
