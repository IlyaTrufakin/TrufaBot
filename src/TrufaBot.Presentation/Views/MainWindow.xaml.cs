using System.Windows;
using TrufaBot.Presentation.ViewModels;

namespace TrufaBot.Presentation.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
