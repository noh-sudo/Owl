using System.Windows;
using Owl1Client.Services;
using Owl1Client.ViewModels;
using Owl1Client.Views;

namespace Owl1Client;

public partial class App : Application
{
    private ServerConnectionService? _connection;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _connection = new ServerConnectionService();
        var settings = AppSettings.Load();

        var loginViewModel = new LoginViewModel(_connection, settings);
        var loginWindow = new LoginWindow { DataContext = loginViewModel };

        loginViewModel.LoginSucceeded += (_, _) =>
        {
            var mainViewModel = new MainViewModel(_connection);
            var mainWindow = new MainWindow { DataContext = mainViewModel };
            MainWindow = mainWindow;
            mainWindow.Show();
            loginWindow.Close();
        };

        MainWindow = loginWindow;
        loginWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _connection?.Dispose();
        base.OnExit(e);
    }
}
