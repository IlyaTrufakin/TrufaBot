using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TrufaBot.Application.Interfaces;
using TrufaBot.Application.Services;
using TrufaBot.Infrastructure.Logging;
using TrufaBot.Infrastructure.Services;
using TrufaBot.Infrastructure.Storage;
using TrufaBot.Infrastructure.Telegram;
using TrufaBot.Presentation.ViewModels;
using TrufaBot.Presentation.Views;

namespace TrufaBot.Presentation;

public partial class App : System.Windows.Application
{
    private readonly IHost _host;

    public App()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IAuditLogger, AuditLogger>();
                services.AddSingleton<IThumbnailService, ThumbnailService>();
                services.AddSingleton<IAuthorizationService, AuthorizationService>();
                services.AddSingleton<IStorageSyncService, StorageSyncService>();
                services.AddSingleton<IAiVisionService, AiVisionService>();
                services.AddSingleton<IFaceRecognitionService, FaceRecognitionService>();
                services.AddSingleton<FaceIndexingService>();
                services.AddSingleton<AiIndexingService>();
                services.AddSingleton<TelegramBotService>();

                services.AddSingleton<MainViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), $"[AppDomain] {args.ExceptionObject}\n");
            }
            catch { }
        };

        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), $"[Dispatcher] {args.Exception}\n");
            }
            catch { }
        };

        try
        {
            _host.Start();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            try
            {
                System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.log"), $"[OnStartup] {ex}\n");
            }
            catch { }
            System.Windows.MessageBox.Show(ex.ToString(), "TrufaBot Startup Error");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host.StopAsync().GetAwaiter().GetResult();
            _host.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}
