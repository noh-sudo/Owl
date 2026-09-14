using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using OwlServer.Utils;

namespace OwlServer.Network;

/// <summary>
/// Common accept-loop behavior shared by every listener (Pi video, Pi data,
/// WPF, Arduino/test-hardware) - dev plan §12/§13 "TcpServer".
///
/// Each accepted connection is handled on its own fire-and-forget Task so one
/// client's exception can never take down the listener or other clients
/// (dev plan §29 rule 12). Shutdown is CancellationToken-driven (rule 11).
/// </summary>
public abstract class TcpServerBase(int port, string name) : BackgroundService
{
    protected int Port { get; } = port;
    protected string Name { get; } = name;

    private TcpListener? _listener;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        Logger.Info($"[{Name}] listening on port {Port}");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                client.NoDelay = true;
                _ = RunClientAsync(client, stoppingToken);
            }
        }
        finally
        {
            _listener.Stop();
            Logger.Info($"[{Name}] stopped listening on port {Port}");
        }
    }

    private async Task RunClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        try
        {
            await HandleClientAsync(client, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal on shutdown.
        }
        catch (OwlProtocolException ex)
        {
            Logger.Error($"[{Name}] protocol error from {remote}", ex);
        }
        catch (IOException ex)
        {
            Logger.Warn($"[{Name}] connection from {remote} closed: {ex.Message}");
        }
        catch (SocketException ex)
        {
            Logger.Warn($"[{Name}] socket error from {remote}: {ex.Message}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[{Name}] unhandled error from {remote}: {ex}");
        }
        finally
        {
            client.Dispose();
        }
    }

    /// <summary>Implemented by each concrete listener; must return when the connection ends.</summary>
    protected abstract Task HandleClientAsync(TcpClient client, CancellationToken ct);
}
