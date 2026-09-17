using System.Windows;
using System.Windows.Input;
using Owl1Client.ViewModels;

namespace Owl1Client.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
    }

    private LoginViewModel? ViewModel => DataContext as LoginViewModel;

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        await ViewModel.LoginAsync(UsernameBox.Text, PasswordBox.Password);
    }

    private async void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel != null)
        {
            await ViewModel.LoginAsync(UsernameBox.Text, PasswordBox.Password);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ServerSettingsPopup.IsOpen = !ServerSettingsPopup.IsOpen;
    }

    private void CloseSettingsPopup_Click(object sender, RoutedEventArgs e)
    {
        ServerSettingsPopup.IsOpen = false;
    }
}
