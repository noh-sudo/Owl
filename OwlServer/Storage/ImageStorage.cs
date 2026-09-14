using Microsoft.Extensions.Options;
using OwlServer.Config;
using OwlServer.Utils;

namespace OwlServer.Storage;

/// <summary>
/// Saves first-detection JPEG frames to disk under data/captures/yyyy/MM/dd/... and
/// returns the path to record in cam_log.thumbnail (dev plan §15 - the DB stores a
/// path only, never image bytes).
/// </summary>
public sealed class ImageStorage(IOptions<ServerSettings> settings)
{
    // Relative form (e.g. "data/captures"), used to build the DB-facing path so it
    // matches the dev plan §15 example (data/captures/2026/09/10/xxx.jpg) regardless
    // of where the process happens to run from.
    private readonly string _configuredRoot = settings.Value.Storage.CaptureRootDirectory.Replace('\\', '/').TrimEnd('/');

    // Absolute form, used for the actual file I/O.
    private readonly string _absoluteRoot = Path.IsPathRooted(settings.Value.Storage.CaptureRootDirectory)
        ? settings.Value.Storage.CaptureRootDirectory
        : Path.Combine(AppContext.BaseDirectory, settings.Value.Storage.CaptureRootDirectory);

    /// <summary>
    /// Writes the JPEG and returns the path to store in cam_log.thumbnail. Must
    /// succeed before the caller INSERTs into cam_log (dev plan §16 - "DB INSERT보다
    /// 파일 저장을 먼저 성공시키는 것이 좋다").
    /// </summary>
    public async Task<string> SaveAsync(byte[] jpeg, DateTime timestamp, long eventId, CancellationToken ct)
    {
        var yyyy = timestamp.ToString("yyyy");
        var mm = timestamp.ToString("MM");
        var dd = timestamp.ToString("dd");
        var fileName = $"{timestamp:yyyyMMdd_HHmmss_fff}_{eventId}.jpg";

        var dayDirectory = Path.Combine(_absoluteRoot, yyyy, mm, dd);
        Directory.CreateDirectory(dayDirectory);

        var fullPath = Path.Combine(dayDirectory, fileName);
        await File.WriteAllBytesAsync(fullPath, jpeg, ct).ConfigureAwait(false);

        var dbPath = $"{_configuredRoot}/{yyyy}/{mm}/{dd}/{fileName}";

        Logger.Info($"Capture saved: {fullPath}");
        return dbPath;
    }
}
