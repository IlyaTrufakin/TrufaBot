using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Windows;
using Forms = System.Windows.Forms;
using TrufaBot.Presentation.ViewModels;

namespace TrufaBot.Presentation.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _loadedIcon;
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
            // 1. Пытаемся загрузить иконку из app.ico рядом с exe
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var icoFile = Path.Combine(baseDir, "app.ico");
            if (File.Exists(icoFile))
            {
                try { _loadedIcon = new System.Drawing.Icon(icoFile); } catch { }
            }

            // 2. Если нет файла, извлекаем из скомпилированного .exe
            if (_loadedIcon == null)
            {
                try
                {
                    var exePath = Environment.ProcessPath;
                    if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                    {
                        _loadedIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                    }
                }
                catch { }
            }

            // 3. Создаем NotifyIcon
            _trayIcon = new Forms.NotifyIcon
            {
                Text = "TrufaBot — Домашний сервер",
                Icon = _loadedIcon ?? SystemIcons.Application,
                Visible = true
            };

            // Клик левой кнопкой мыши или двойной клик открывает окно
            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == Forms.MouseButtons.Left)
                {
                    ShowMainWindow();
                }
            };
            _trayIcon.DoubleClick += (s, e) => ShowMainWindow();

            // Контекстное меню по правому клику
            var contextMenu = new Forms.ContextMenuStrip();
            contextMenu.Items.Add("🖥 Открыть окно", null, (s, e) => ShowMainWindow());
            contextMenu.Items.Add("▶ Запустить / Остановить бота", null, (s, e) => _viewModel.ToggleBotCommand.Execute(null));
            contextMenu.Items.Add(new Forms.ToolStripSeparator());

            var exitItem = contextMenu.Items.Add("🚪 Полный выход", null, (s, e) => ExitApplication());
            if (exitItem.Font != null)
            {
                exitItem.Font = new Font(exitItem.Font, System.Drawing.FontStyle.Bold);
            }

            _trayIcon.ContextMenuStrip = contextMenu;

            // В Windows 11 автоматически делаем значок закрепленным (Promoted = 1),
            // чтобы он отображался на панели задач, а не прятался
            EnsureTrayIconPromotedInWindows11();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TrayIcon] Ошибка инициализации: {ex}");
        }
    }

    private static void EnsureTrayIconPromotedInWindows11()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", true);
            if (key == null) return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName, true);
                if (subKey == null) continue;

                var path = subKey.GetValue("ExecutablePath") as string;
                if (string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase))
                {
                    var isPromoted = subKey.GetValue("IsPromoted");
                    if (isPromoted == null || Convert.ToInt32(isPromoted) != 1)
                    {
                        subKey.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    }
                }
            }
        }
        catch { }
    }

    public void ShowMainWindow()
    {
        if (!IsVisible)
        {
            Show();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitApplication()
    {
        _isExplicitExit = true;
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        _loadedIcon?.Dispose();
        _loadedIcon = null;
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            // Отменяем закрытие и скрываем окно в системный трей (возле часов)
            e.Cancel = true;
            Hide();

            if (_trayIcon != null)
            {
                _trayIcon.Visible = true;
                try
                {
                    _trayIcon.ShowBalloonTip(
                        2000,
                        "TrufaBot",
                        "Сервер работает в фоновом режиме. Кликните по значку в трее, чтобы открыть окно.",
                        Forms.ToolTipIcon.Info
                    );
                }
                catch { }
            }
        }
        else
        {
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            _loadedIcon?.Dispose();
            _loadedIcon = null;
            base.OnClosing(e);
        }
    }
}
