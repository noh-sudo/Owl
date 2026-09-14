using System.Net.Sockets;
using System.Threading.Channels;
using OwlServer.Utils;

namespace OwlServer.Network;

/// <summary>
/// A connected WPF client. Video frames and JSON messages are queued separately
/// and drained by two background sender loops so a slow WPF client never blocks
/// the broadcast to other clients (dev plan §21 rule + §29 rule 12).
///
/// The frame channel is capacity-1 with DropOldest: if the client can't keep up,
/// old frames are discarded and only the newest is ever pending (dev plan §21 -
/// "오래된 frame을 버리고 최신 frame을 우선한다"). The message channel is
/// unbounded because detection logs/login results must not be dropped, and the
/// message rate is inherently low (events, not every frame).
/// </summary>
public sealed class WpfClientSession(TcpClient tcpClient) : ClientSession(tcpClient)
{
    private readonly Channel<byte[]> _frameChannel = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });

    private readonly Channel<string> _messageChannel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private Task? _frameSenderTask;
    private Task? _messageSenderTask;

    /// <summary>Set once the WPF client successfully logs in; null before that.</summary>
    public int? UserId { get; set; }

    /// <summary>Raised when a send fails, so the owner can drop/dispose this session.</summary>
    public event Action<WpfClientSession, Exception>? Faulted;

    public void Start(CancellationToken ct)
    {
        _frameSenderTask = RunFrameSenderAsync(ct);
        _messageSenderTask = RunMessageSenderAsync(ct);
    }

    public void EnqueueFrame(byte[] jpeg) => _frameChannel.Writer.TryWrite(jpeg);

    public void EnqueueMessage(string json) => _messageChannel.Writer.TryWrite(json);

    private async Task RunFrameSenderAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var jpeg in _frameChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await SendFrameAsync(jpeg, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on shutdown/disconnect.
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, ex);
        }
    }

    private async Task RunMessageSenderAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var json in _messageChannel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await SendMessageAsync(json, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on shutdown/disconnect.
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(this, ex);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _frameChannel.Writer.TryComplete();
        _messageChannel.Writer.TryComplete();

        try
        {
            if (_frameSenderTask is not null) await _frameSenderTask.ConfigureAwait(false);
            if (_messageSenderTask is not null) await _messageSenderTask.ConfigureAwait(false);
        }
        catch
        {
            // Sender-loop failures are already reported via Faulted; ignore here.
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
