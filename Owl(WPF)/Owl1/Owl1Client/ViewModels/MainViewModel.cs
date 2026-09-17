using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Owl1Client.Models;
using Owl1Client.Services;

namespace Owl1Client.ViewModels;

public class MainViewModel : ViewModelBase, IDisposable
{
    private const int MaxLogCount = 200;

    private readonly ServerConnectionService _connection;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _videoWatchdogTimer;
    private readonly DispatcherTimer _motionResetTimer;

    /// <summary>이미 결정(사격/사격중단)을 전송한 l_id 집합. 같은 l_id로
    /// decision이 중복 전송되는 것을 막기 위해 사용한다.</summary>
    private readonly HashSet<int> _decidedLIds = new();

    public ObservableCollection<DetectionLogItem> DetectionLogs { get; } = new();

    private BitmapImage? _currentFrame;
    public BitmapImage? CurrentFrame
    {
        get => _currentFrame;
        set => SetProperty(ref _currentFrame, value);
    }

    private string _timestampOverlay = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public string TimestampOverlay
    {
        get => _timestampOverlay;
        set => SetProperty(ref _timestampOverlay, value);
    }

    private bool _isVideoActive;
    public bool IsVideoActive
    {
        get => _isVideoActive;
        set => SetProperty(ref _isVideoActive, value);
    }

    private bool _isMotionDetected;
    public bool IsMotionDetected
    {
        get => _isMotionDetected;
        set
        {
            if (SetProperty(ref _isMotionDetected, value))
            {
                OnPropertyChanged(nameof(IsIdleLed));
                OnPropertyChanged(nameof(IsDetectedLed));
            }
        }
    }

    private bool _isLockedOn;
    public bool IsLockedOn
    {
        get => _isLockedOn;
        set
        {
            if (SetProperty(ref _isLockedOn, value))
            {
                OnPropertyChanged(nameof(IsIdleLed));
                OnPropertyChanged(nameof(IsDetectedLed));
            }
        }
    }

    /// <summary>상태 LED 3종(평상시/움직임 감지/추적)은 서로 배타적이어야 하므로
    /// IsMotionDetected/IsLockedOn으로부터 계산한다.</summary>
    public bool IsIdleLed => !IsMotionDetected && !IsLockedOn;
    public bool IsDetectedLed => IsMotionDetected && !IsLockedOn;

    private bool _isConnectionError = true;
    public bool IsConnectionError
    {
        get => _isConnectionError;
        set => SetProperty(ref _isConnectionError, value);
    }

    private bool _isCameraOnline;
    public bool IsCameraOnline
    {
        get => _isCameraOnline;
        set => SetProperty(ref _isCameraOnline, value);
    }

    private bool _isRaspberryPiOnline;
    public bool IsRaspberryPiOnline
    {
        get => _isRaspberryPiOnline;
        set => SetProperty(ref _isRaspberryPiOnline, value);
    }

    private bool _isArduinoOnline;
    public bool IsArduinoOnline
    {
        get => _isArduinoOnline;
        set => SetProperty(ref _isArduinoOnline, value);
    }

    private string _connectionStatusText = "연결 대기 중";
    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        set => SetProperty(ref _connectionStatusText, value);
    }

    private DetectionLogItem? _selectedLog;
    public DetectionLogItem? SelectedLog
    {
        get => _selectedLog;
        set => SetProperty(ref _selectedLog, value);
    }

    public RelayCommand FireCommand { get; }
    public RelayCommand CeaseFireCommand { get; }

    public MainViewModel(ServerConnectionService connection)
    {
        _connection = connection;
        IsConnectionError = !connection.IsConnected;
        ConnectionStatusText = connection.IsConnected ? "서버 연결됨" : "연결 대기 중";

        // 화면 전환 타이밍 때문에 구독 이전에 이미 수신된 상태/프레임이 있으면 즉시 반영한다.
        if (connection.LastFrame != null) CurrentFrame = connection.LastFrame;
        if (connection.LastSystemStatus is { } status)
        {
            IsCameraOnline = status.Camera;
            IsRaspberryPiOnline = status.RaspberryPi;
            IsArduinoOnline = status.Arduino;
        }

        _connection.FrameReceived += OnFrameReceived;
        _connection.DetectionLogReceived += OnDetectionLogReceived;
        _connection.SystemStatusReceived += OnSystemStatusReceived;
        _connection.ConnectionStatusChanged += OnConnectionStatusChanged;
        _connection.ReconnectAttempt += OnReconnectAttempt;
        _connection.ChangeLedReceived += OnChangeLedReceived;

        FireCommand = new RelayCommand(_ => Decide(true), _ => CanDecide());
        CeaseFireCommand = new RelayCommand(_ => Decide(false), _ => CanDecide());

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => TimestampOverlay = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _clockTimer.Start();

        // 프레임이 일정 시간 안 들어오면(수신 지연/정지) 영상수신중 LED를 끈다.
        _videoWatchdogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _videoWatchdogTimer.Tick += (_, _) => { IsVideoActive = false; _videoWatchdogTimer.Stop(); };

        // 마지막 감지 이후 일정 시간이 지나면 움직임감지 LED를 끈다.
        _motionResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _motionResetTimer.Tick += (_, _) => { IsMotionDetected = false; _motionResetTimer.Stop(); };
    }

    private void OnFrameReceived(BitmapImage image)
    {
        CurrentFrame = image;
        IsVideoActive = true;
        _videoWatchdogTimer.Stop();
        _videoWatchdogTimer.Start();
    }

    private void OnDetectionLogReceived(DetectionLogMessage message)
    {
        AddLog(new DetectionLogItem(message.LId, message.Category, message.DateTime));

        IsMotionDetected = true;
        _motionResetTimer.Stop();
        _motionResetTimer.Start();
    }

    private void AddLog(DetectionLogItem item)
    {
        DetectionLogs.Insert(0, item);
        while (DetectionLogs.Count > MaxLogCount)
            DetectionLogs.RemoveAt(DetectionLogs.Count - 1);
    }

    private void OnSystemStatusReceived(SystemStatusMessage status)
    {
        IsCameraOnline = status.Camera;
        IsRaspberryPiOnline = status.RaspberryPi;
        IsArduinoOnline = status.Arduino;
    }

    private void OnConnectionStatusChanged(bool connected)
    {
        IsConnectionError = !connected;
        ConnectionStatusText = connected ? "서버 연결됨" : "서버 연결 끊김 - 재연결 시도 중...";
        if (!connected)
        {
            IsVideoActive = false;
            IsCameraOnline = false;
            IsRaspberryPiOnline = false;
            IsArduinoOnline = false;
        }
    }

    private void OnReconnectAttempt(string text) => ConnectionStatusText = text;

    private void OnChangeLedReceived(ChangeLedMessage message)
    {
        if (message.Color == "red")
        {
            IsLockedOn = true;
        }
        else if (message.Color == "green")
        {
            IsLockedOn = false;
            IsMotionDetected = false;
            _motionResetTimer.Stop();
        }
    }

    private bool CanDecide() =>
        SelectedLog is { IsSelectable: true } log && !_decidedLIds.Contains(log.LId);

    private async void Decide(bool approved)
    {
        if (SelectedLog is not { IsSelectable: true } selectedLog) return;
        if (_decidedLIds.Contains(selectedLog.LId)) return;

        try
        {
            await _connection.SendDecisionAsync(selectedLog.LId, approved);

            _decidedLIds.Add(selectedLog.LId);
            var actionLabel = approved ? "사격승인" : "추적 해제";
            AddLog(DetectionLogItem.CreateActionLog(selectedLog.LId, actionLabel, DateTime.Now));
            SelectedLog = null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MainViewModel] 결정 전송 실패: {ex.Message}");
            ConnectionStatusText = $"명령 전송 실패: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _clockTimer.Stop();
        _videoWatchdogTimer.Stop();
        _motionResetTimer.Stop();

        _connection.FrameReceived -= OnFrameReceived;
        _connection.DetectionLogReceived -= OnDetectionLogReceived;
        _connection.SystemStatusReceived -= OnSystemStatusReceived;
        _connection.ConnectionStatusChanged -= OnConnectionStatusChanged;
        _connection.ReconnectAttempt -= OnReconnectAttempt;
        _connection.ChangeLedReceived -= OnChangeLedReceived;
    }
}
