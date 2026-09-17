using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Owl1Client.Models;

namespace Owl1Client.Services;

/// <summary>
/// 서버와의 TCP 소켓 통신을 전담하는 서비스.
/// 하나의 소켓으로 영상(Binary, Magic="OWL1")과 제어/상태(JSON, Magic="OWLD") 패킷을
/// 다중화해서 주고받는 프로토콜을 그대로 구현한다.
/// 모든 이벤트는 Dispatcher를 통해 UI 스레드에서 안전하게 발생시킨다.
/// </summary>
public class ServerConnectionService : IDisposable
{
    private const int MaxFramePayloadSize = 16 * 1024 * 1024; // 16MB, 비정상 길이 값으로부터 방어
    private const int MaxJsonPayloadSize = 1 * 1024 * 1024;   // 1MB
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _frameLock = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _receiveCts;
    private volatile bool _manualDisconnect = true;
    private byte[]? _pendingFrame;
    private int _frameProcessingScheduled;

    public bool IsConnected { get; private set; }

    /// <summary>구독 시점(로그인 완료 후 메인 화면 생성 등) 이전에 이미 수신된 마지막 상태.
    /// 주기 전송 간격이 긴 메시지(system_status)를 화면 전환 타이밍 때문에 놓치지 않도록 캐시해둔다.</summary>
    public SystemStatusMessage? LastSystemStatus { get; private set; }
    public BitmapImage? LastFrame { get; private set; }

    /// <summary>연결 상태 변화(true=연결됨, false=끊김) - UI 스레드에서 발생.</summary>
    public event Action<bool>? ConnectionStatusChanged;
    /// <summary>재연결 시도 상황 메시지 - UI 스레드에서 발생.</summary>
    public event Action<string>? ReconnectAttempt;
    /// <summary>새 영상 프레임 수신 - UI 스레드에서 발생.</summary>
    public event Action<BitmapImage>? FrameReceived;
    public event Action<LoginResultMessage>? LoginResultReceived;
    public event Action<DetectionLogMessage>? DetectionLogReceived;
    public event Action<SystemStatusMessage>? SystemStatusReceived;
    public event Action<ChangeLedMessage>? ChangeLedReceived;

    private string _host = "127.0.0.1";
    private int _port = 6000;

    /// <summary>서버에 1회 접속을 시도한다. 성공하면 수신 루프를 백그라운드로 시작한다.</summary>
    public async Task<bool> ConnectAsync(string host, int port)
    {
        _host = host;
        _port = port;
        _manualDisconnect = false;
        return await TryConnectOnceAsync().ConfigureAwait(false);
    }

    private async Task<bool> TryConnectOnceAsync()
    {
        try
        {
            CleanupSocket();

            var client = new TcpClient();
            await client.ConnectAsync(_host, _port).ConfigureAwait(false);

            _client = client;
            _stream = client.GetStream();
            _receiveCts = new CancellationTokenSource();
            IsConnected = true;

            RaiseOnUi(() => ConnectionStatusChanged?.Invoke(true));

            var token = _receiveCts.Token;
            var stream = _stream;
            _ = Task.Run(() => ReceiveLoopAsync(stream, token), token);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ServerConnectionService] 연결 실패: {ex.Message}");
            IsConnected = false;
            return false;
        }
    }

    public void Disconnect()
    {
        _manualDisconnect = true;
        var wasConnected = IsConnected;
        IsConnected = false;
        CleanupSocket();
        if (wasConnected)
        {
            RaiseOnUi(() => ConnectionStatusChanged?.Invoke(false));
        }
    }

    private void CleanupSocket()
    {
        try { _receiveCts?.Cancel(); } catch { /* 무시 */ }
        try { _stream?.Dispose(); } catch { /* 무시 */ }
        try { _client?.Dispose(); } catch { /* 무시 */ }
        _stream = null;
        _client = null;
        _receiveCts = null;
    }

    // ---- 수신 루프 -----------------------------------------------------

    private async Task ReceiveLoopAsync(NetworkStream stream, CancellationToken token)
    {
        var magicBuffer = new byte[4];
        var lenBuffer4 = new byte[4];
        var lenBuffer8 = new byte[8];

        try
        {
            while (!token.IsCancellationRequested)
            {
                await ReadExactAsync(stream, magicBuffer, 4, token).ConfigureAwait(false);
                var magic = Encoding.ASCII.GetString(magicBuffer);

                if (magic == "OWL1")
                {
                    await ReadExactAsync(stream, lenBuffer4, 4, token).ConfigureAwait(false);
                    var payloadSize = BinaryPrimitives.ReadInt32BigEndian(lenBuffer4);
                    if (payloadSize <= 0 || payloadSize > MaxFramePayloadSize)
                        throw new InvalidDataException($"잘못된 영상 프레임 크기: {payloadSize}");

                    var payload = new byte[payloadSize];
                    await ReadExactAsync(stream, payload, payloadSize, token).ConfigureAwait(false);
                    HandleVideoFrame(payload);
                }
                else if (magic == "OWLD")
                {
                    await ReadExactAsync(stream, lenBuffer8, 8, token).ConfigureAwait(false);
                    var jsonSize = BinaryPrimitives.ReadInt32BigEndian(lenBuffer8.AsSpan(0, 4));
                    var blobSize = BinaryPrimitives.ReadInt32BigEndian(lenBuffer8.AsSpan(4, 4));
                    if (jsonSize < 0 || jsonSize > MaxJsonPayloadSize || blobSize < 0 || blobSize > MaxFramePayloadSize)
                        throw new InvalidDataException($"잘못된 OWLD 패킷 크기: json={jsonSize}, blob={blobSize}");

                    var jsonBytes = new byte[jsonSize];
                    await ReadExactAsync(stream, jsonBytes, jsonSize, token).ConfigureAwait(false);

                    byte[]? blob = null;
                    if (blobSize > 0)
                    {
                        blob = new byte[blobSize];
                        await ReadExactAsync(stream, blob, blobSize, token).ConfigureAwait(false);
                    }

                    HandleControlMessage(jsonBytes, blob);
                }
                else
                {
                    throw new InvalidDataException($"알 수 없는 Magic 헤더: {magic}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상적인 연결 종료(Disconnect() 호출) - 별도 처리 불필요
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ServerConnectionService] 수신 루프 오류: {ex.Message}");
            HandleDisconnected();
        }
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken token)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), token).ConfigureAwait(false);
            if (read == 0)
                throw new IOException("서버와의 연결이 종료되었습니다.");
            offset += read;
        }
    }

    // ---- 영상 프레임 처리 (최신 프레임만 렌더링, 밀리면 이전 프레임 폐기) ----

    private void HandleVideoFrame(byte[] jpegBytes)
    {
        lock (_frameLock)
        {
            _pendingFrame = jpegBytes;
        }

        if (Interlocked.CompareExchange(ref _frameProcessingScheduled, 1, 0) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ => ProcessLatestFrame());
        }
    }

    private void ProcessLatestFrame()
    {
        byte[]? bytes;
        lock (_frameLock)
        {
            bytes = _pendingFrame;
            _pendingFrame = null;
        }
        Interlocked.Exchange(ref _frameProcessingScheduled, 0);

        if (bytes == null) return;

        try
        {
            var bitmap = ImageHelper.ToBitmapImage(bytes);
            LastFrame = bitmap;
            RaiseOnUi(() => FrameReceived?.Invoke(bitmap));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ServerConnectionService] 프레임 디코딩 실패: {ex.Message}");
        }
    }

    // ---- 제어/상태 메시지 처리 ----

    private void HandleControlMessage(byte[] jsonBytes, byte[]? blob)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonBytes);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            switch (type)
            {
                case "login_result":
                    var loginResult = JsonSerializer.Deserialize<LoginResultMessage>(jsonBytes, JsonOptions);
                    if (loginResult != null) RaiseOnUi(() => LoginResultReceived?.Invoke(loginResult));
                    break;

                case "detection_log":
                    // 스펙상 detection_log의 blob_size는 항상 0(이미지 없음). 방어적으로만 로그를 남긴다.
                    if (blob is { Length: > 0 })
                        Debug.WriteLine($"[ServerConnectionService] detection_log에 예상치 못한 blob({blob.Length} bytes)이 포함되어 무시합니다.");

                    var log = JsonSerializer.Deserialize<DetectionLogMessage>(jsonBytes, JsonOptions);
                    if (log != null) RaiseOnUi(() => DetectionLogReceived?.Invoke(log));
                    break;

                case "system_status":
                    var status = JsonSerializer.Deserialize<SystemStatusMessage>(jsonBytes, JsonOptions);
                    if (status != null)
                    {
                        LastSystemStatus = status;
                        RaiseOnUi(() => SystemStatusReceived?.Invoke(status));
                    }
                    break;

                case "change_led":
                    var changeLed = JsonSerializer.Deserialize<ChangeLedMessage>(jsonBytes, JsonOptions);
                    if (changeLed != null) RaiseOnUi(() => ChangeLedReceived?.Invoke(changeLed));
                    break;

                default:
                    Debug.WriteLine($"[ServerConnectionService] 알 수 없는 메시지 타입: {type}");
                    break;
            }
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[ServerConnectionService] JSON 파싱 실패: {ex.Message}");
        }
    }

    // ---- 연결 끊김 감지 및 자동 재연결 ----

    private void HandleDisconnected()
    {
        if (!IsConnected) return;
        IsConnected = false;
        CleanupSocket();
        RaiseOnUi(() => ConnectionStatusChanged?.Invoke(false));

        if (!_manualDisconnect)
        {
            _ = AutoReconnectLoopAsync();
        }
    }

    private async Task AutoReconnectLoopAsync()
    {
        var attempt = 0;
        while (!_manualDisconnect && !IsConnected)
        {
            attempt++;
            RaiseOnUi(() => ReconnectAttempt?.Invoke($"서버 재연결 시도 중... ({attempt}회)"));
            await Task.Delay(ReconnectDelay).ConfigureAwait(false);
            if (_manualDisconnect) break;
            await TryConnectOnceAsync().ConfigureAwait(false);
        }
    }

    // ---- 송신 ----

    public Task SendLoginAsync(string username, string password) =>
        SendControlAsync(new LoginRequestMessage { Username = username, Password = password });

    public Task SendDecisionAsync(int lId, bool approved) =>
        SendControlAsync(new DecisionMessage { LId = lId, Approved = approved });

    private async Task SendControlAsync<T>(T message)
    {
        var stream = _stream ?? throw new InvalidOperationException("서버에 연결되어 있지 않습니다.");
        var json = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
        var packet = BuildOwldPacket(json, blobSize: 0);

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(packet).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static byte[] BuildOwldPacket(byte[] json, int blobSize)
    {
        var buffer = new byte[4 + 4 + 4 + json.Length];
        Encoding.ASCII.GetBytes("OWLD", 0, 4, buffer, 0);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4, 4), json.Length);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(8, 4), blobSize);
        json.CopyTo(buffer.AsSpan(12));
        return buffer;
    }

    // ---- 공통 ----

    private static void RaiseOnUi(Action action)
    {
        var app = Application.Current;
        if (app == null)
        {
            action();
            return;
        }
        app.Dispatcher.Invoke(action);
    }

    public void Dispose()
    {
        _manualDisconnect = true;
        CleanupSocket();
        _sendLock.Dispose();
    }
}
