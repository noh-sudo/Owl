using System.Diagnostics;
using Owl1Client.Models;
using Owl1Client.Services;

namespace Owl1Client.ViewModels;

public class LoginViewModel : ViewModelBase
{
    private readonly ServerConnectionService _connection;
    private readonly AppSettings _settings;
    private TaskCompletionSource<bool>? _loginTcs;

    public event EventHandler? LoginSucceeded;

    private string _serverIp;
    public string ServerIp
    {
        get => _serverIp;
        set => SetProperty(ref _serverIp, value);
    }

    private string _serverPortText;
    public string ServerPortText
    {
        get => _serverPortText;
        set => SetProperty(ref _serverPortText, value);
    }

    private string _username = string.Empty;
    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value);
    }

    private string _errorMessage = string.Empty;
    public string ErrorMessage
    {
        get => _errorMessage;
        set
        {
            if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public LoginViewModel(ServerConnectionService connection, AppSettings settings)
    {
        _connection = connection;
        _settings = settings;
        _serverIp = settings.ServerIp;
        _serverPortText = settings.ServerPort.ToString();

        _connection.ConnectionStatusChanged += OnConnectionStatusChanged;
        _connection.ReconnectAttempt += text => StatusText = text;
        _connection.LoginResultReceived += OnLoginResultReceived;
    }

    // PasswordBox는 보안상 바인딩하지 않으므로, 코드비하인드(로그인 버튼 클릭)에서 비밀번호를 직접 넘겨받는다.
    public async Task LoginAsync(string username, string password)
    {
        if (IsBusy) return;

        ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            ErrorMessage = "아이디와 비밀번호를 입력하세요.";
            return;
        }

        if (!int.TryParse(ServerPortText, out var port) || port <= 0 || port > 65535)
        {
            ErrorMessage = "포트 번호가 올바르지 않습니다.";
            return;
        }

        IsBusy = true;
        try
        {
            if (!_connection.IsConnected)
            {
                StatusText = "서버에 연결하는 중...";
                var connected = await _connection.ConnectAsync(ServerIp, port);
                if (!connected)
                {
                    ErrorMessage = "서버에 연결할 수 없습니다. IP/포트를 확인하세요.";
                    return;
                }
            }

            StatusText = "로그인 요청 중...";
            _loginTcs = new TaskCompletionSource<bool>();
            await _connection.SendLoginAsync(username, password);

            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
            var completed = await Task.WhenAny(_loginTcs.Task, timeoutTask);

            if (completed == timeoutTask)
            {
                ErrorMessage = "서버 응답이 없습니다. 다시 시도해주세요.";
                return;
            }

            var success = await _loginTcs.Task;
            if (success)
            {
                _settings.ServerIp = ServerIp;
                _settings.ServerPort = port;
                _settings.Save();
                LoginSucceeded?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ErrorMessage = "아이디 또는 비밀번호가 올바르지 않습니다.";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[LoginViewModel] 로그인 실패: {ex}");
            ErrorMessage = $"오류가 발생했습니다: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            StatusText = string.Empty;
        }
    }

    private void OnLoginResultReceived(LoginResultMessage message) => _loginTcs?.TrySetResult(message.Success);

    private void OnConnectionStatusChanged(bool connected)
    {
        if (!connected) StatusText = "서버 연결이 끊어졌습니다.";
    }
}
