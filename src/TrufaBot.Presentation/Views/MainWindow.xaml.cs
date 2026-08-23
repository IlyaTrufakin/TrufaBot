using System.ComponentModel;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;
using TrufaBot.Presentation.ViewModels;

namespace TrufaBot.Presentation.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Forms.NotifyIcon? _trayIcon;
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
            _trayIcon = new Forms.NotifyIcon
            {
                Text = "TrufaBot — Домашний медиа-сервер",
                Icon = SystemIcons.Application,
                Visible = true
            };

            // Двойной клик или одинарный клик по иконке в трее открывает окно
            _trayIcon.DoubleClick += (s, e) => ShowMainWindow();

            // Контекстное меню по правому клику в трее
            var contextMenu = new Forms.ContextMenuStrip();

            contextMenu.Items.Add("🖥 Открыть окно", null, (s, e) => ShowMainWindow());
            contextMenu.Items.Add("▶ Запустить / Остановить бота", null, (s, e) => _viewModel.ToggleBotCommand.Execute(null));
            contextMenu.Items.Add(new Forms.ToolStripSeparator());

            var exitItem = contextMenu.Items.Add("🚪 Полный выход", null, (s, e) => ExitApplication());
            if (exitItem.Font != null)
            {
                exitItem.Font = new System.Drawing.Font(exitItem.Font, System.Drawing.FontStyle.Bold);
            }

            _trayIcon.ContextMenuStrip = contextMenu;
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
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            // Отменяем закрытие и скрываем окно в системный трей (возле часов)
            e.Cancel = true;
            Hide();

            _trayIcon?.ShowBalloonTip(
                2000,
                "TrufaBot продолжает работать",
                "Сервер свернут в системный трей и обрабатывает запросы Telegram в фоновом режиме.",
                Forms.ToolTipIcon.Info
            );
        }
        else
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            base.OnClosing(e);
        }
    }
}
