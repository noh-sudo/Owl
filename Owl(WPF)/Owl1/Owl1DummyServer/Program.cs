using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Owl1DummyServer;

const int Port = 6000;
const string ValidUsername = "admin";
const string ValidPassword = "1234";

var listener = new TcpListener(IPAddress.Any, Port);
listener.Start();
Console.WriteLine($"[Owl1DummyServer] 포트 {Port}에서 대기 중... (테스트 계정 {ValidUsername}/{ValidPassword})");

while (true)
{
    var client = await listener.AcceptTcpClientAsync();
    Console.WriteLine($"[Owl1DummyServer] 클라이언트 연결됨: {client.Client.RemoteEndPoint}");
    _ = HandleClientAsync(client);
}

async Task HandleClientAsync(TcpClient client)
{
    using var _ = client;
    var stream = client.GetStream();
    var sendLock = new SemaphoreSlim(1, 1);
    using var cts = new CancellationTokenSource();
    var token = cts.Token;

    var nextLId = 1;

    var readerTask = Task.Run(async () =>
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var message = await ProtocolHelper.ReadOwldAsync(stream, token);
                if (message == null) break;
                var (type, jsonBytes, _) = message.Value;

                using var doc = JsonDocument.Parse(jsonBytes);
                switch (type)
                {
                    case "login":
                        var username = doc.RootElement.GetProperty("username").GetString() ?? "";
                        var password = doc.RootElement.GetProperty("password").GetString() ?? "";
                        var success = username == ValidUsername && password == ValidPassword;
                        Console.WriteLine($"[로그인 요청] username={username} -> {(success ? "성공" : "실패")}");
                        var resultJson = JsonSerializer.SerializeToUtf8Bytes(new { type = "login_result", success });
                        await ProtocolHelper.SendOwldAsync(stream, sendLock, resultJson, null);
                        break;

                    case "decision":
                        var lId = doc.RootElement.GetProperty("l_id").GetInt32();
                        var approved = doc.RootElement.GetProperty("approved").GetBoolean();
                        Console.WriteLine($"[결정 수신] l_id={lId}, approved={approved}");
                        break;

                    default:
                        Console.WriteLine($"[알 수 없는 메시지 타입] {type}");
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[수신 오류] {ex.Message}");
        }
        finally
        {
            cts.Cancel();
        }
    }, token);

    var videoTask = Task.Run(async () =>
    {
        try
        {
            var frame = 0;
            while (!token.IsCancellationRequested)
            {
                var jpeg = FrameGenerator.CreateFrame(frame++);
                await ProtocolHelper.SendOwl1Async(stream, sendLock, jpeg);
                await Task.Delay(100, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[영상 송신 오류] {ex.Message}");
            cts.Cancel();
        }
    }, token);

    var detectionTask = Task.Run(async () =>
    {
        var rnd = new Random();
        var categories = new[] { "human", "animal", "vehicle" };
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(rnd.Next(4, 9)), token);
                var category = categories[rnd.Next(categories.Length)];
                var lId = nextLId++;
                var json = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "detection_log",
                    l_id = lId,
                    category,
                    datetime = DateTime.Now,
                });
                Console.WriteLine($"[인식 로그 송신] l_id={lId}, category={category}");
                // 스펙상 detection_log의 blob_size는 항상 0(이미지 없음)이므로 blob 없이 전송한다.
                await ProtocolHelper.SendOwldAsync(stream, sendLock, json, blob: null);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[로그 송신 오류] {ex.Message}");
            cts.Cancel();
        }
    }, token);

    var statusTask = Task.Run(async () =>
    {
        var rnd = new Random();
        try
        {
            while (!token.IsCancellationRequested)
            {
                var status = new
                {
                    type = "system_status",
                    camera = rnd.NextDouble() > 0.05,
                    raspberry_pi = rnd.NextDouble() > 0.05,
                    arduino = rnd.NextDouble() > 0.1,
                };
                var json = JsonSerializer.SerializeToUtf8Bytes(status);
                await ProtocolHelper.SendOwldAsync(stream, sendLock, json, null);
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[상태 송신 오류] {ex.Message}");
            cts.Cancel();
        }
    }, token);

    await Task.WhenAny(readerTask, videoTask, detectionTask, statusTask);
    cts.Cancel();
    Console.WriteLine($"[Owl1DummyServer] 클라이언트 연결 종료: {client.Client.RemoteEndPoint}");
}
