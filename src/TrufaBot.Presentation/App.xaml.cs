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

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }
}
