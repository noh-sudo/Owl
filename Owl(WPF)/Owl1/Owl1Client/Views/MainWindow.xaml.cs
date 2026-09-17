using System.Windows;
using Owl1Client.ViewModels;

namespace Owl1Client.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closed += (_, _) => (DataContext as MainViewModel)?.Dispose();
    }
}
