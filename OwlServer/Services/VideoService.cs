using OwlServer.Models;

namespace OwlServer.Services;

/// <summary>
/// Holds only the single latest JPEG frame in memory (dev plan §20) and relays
/// every incoming frame straight to connected WPF clients without decoding it
/// (dev plan §9 - "서버에서 영상 자체를 Bitmap으로 디코딩했다가 다시 인코딩할
/// 필요는 없다").
/// </summary>
public sealed class VideoService(ClientBroadcastService broadcastService)
{
    private readonly Lock _lock = new();
    private VideoFrame? _latestFrame;

    public void ReceiveFrame(byte[] jpeg)
    {
        var frame = new VideoFrame(jpeg, DateTime.UtcNow);
        lock (_lock)
        {
            _latestFrame = frame;
        }

        broadcastService.BroadcastFrame(jpeg);
    }

    public VideoFrame? GetLatestFrame()
    {
        lock (_lock)
        {
            return _latestFrame;
        }
    }
}
