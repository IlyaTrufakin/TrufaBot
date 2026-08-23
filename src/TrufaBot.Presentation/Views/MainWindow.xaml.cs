using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using TrufaBot.Presentation.ViewModels;

namespace TrufaBot.Presentation.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private TaskbarIcon? _trayIcon;
    private bool _isExplicitExit = false;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        InitializeTrayIcon();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new TaskbarIcon
            {
                ToolTipText = "TrufaBot — Домашний медиа-сервер",
                Icon = SystemIcons.Application
            };

            // Двойной клик по иконке в трее открывает окно
            _trayIcon.TrayMouseDoubleClick += (s, e) => ShowMainWindow();

            // Контекстное меню по правому клику в трее
            var contextMenu = new ContextMenu();

            var openItem = new MenuItem { Header = "🖥 Открыть окно" };
            openItem.Click += (s, e) => ShowMainWindow();
            contextMenu.Items.Add(openItem);

            var toggleBotItem = new MenuItem { Header = "▶ Запустить / Остановить бота" };
            toggleBotItem.Click += (s, e) => _viewModel.ToggleBotCommand.Execute(null);
            contextMenu.Items.Add(toggleBotItem);

            contextMenu.Items.Add(new Separator());

            var exitItem = new MenuItem { Header = "🚪 Полный выход", FontWeight = FontWeights.Bold };
            exitItem.Click += (s, e) => ExitApplication();
            contextMenu.Items.Add(exitItem);

            _trayIcon.ContextMenu = contextMenu;
        }
        catch { }
    }

    public void ShowMainWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _isExplicitExit = true;
        _trayIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            // Отменяем закрытие и скрываем окно в системный трей (возле часов)
            e.Cancel = true;
            Hide();

            _trayIcon?.ShowNotification(
                title: "TrufaBot продолжает работать",
                message: "Сервер свернут в системный трей и обрабатывает запросы Telegram в фоновом режиме.",
                icon: NotificationIcon.Info
            );
        }
        else
        {
            _trayIcon?.Dispose();
            base.OnClosing(e);
        }
    }
}
