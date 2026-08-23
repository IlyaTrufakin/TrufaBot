using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using TrufaBot.Application.Interfaces;
using TrufaBot.Domain.Entities;
using TrufaBot.Infrastructure.Common;
using TrufaBot.Infrastructure.Data;
using TrufaBot.Infrastructure.Logging;
using TrufaBot.Infrastructure.Telegram;

namespace TrufaBot.Presentation.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IAuditLogger _auditLogger;
    private readonly TelegramBotService _botService;
    private readonly IStorageSyncService _syncService;

    [ObservableProperty]
    private string _botToken = "";

    [ObservableProperty]
    private bool _isBotRunning;

    [ObservableProperty]
    private string _statusText = "Сервер остановлен";

    [ObservableProperty]
    private string _newSourceName = "";

    [ObservableProperty]
    private string _newSourcePath = "";

    [ObservableProperty]
    private long _newTelegramUserId;

    [ObservableProperty]
    private string _newUserDisplayName = "";

    [ObservableProperty]
    private ObservableCollection<StorageSource> _sources = new();

    [ObservableProperty]
    private ObservableCollection<User> _users = new();

    [ObservableProperty]
    private ObservableCollection<AuditLogEntry> _logs = new();

    public MainViewModel(IAuditLogger auditLogger, TelegramBotService botService, IStorageSyncService syncService)
    {
        _auditLogger = auditLogger;
        _botService = botService;
        _syncService = syncService;

        if (_auditLogger is AuditLogger loggerImpl)
        {
            loggerImpl.LogAdded += OnLogAdded;
        }

        LoadData();
    }

    private void OnLogAdded(AuditLogEntry entry)
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            Logs.Insert(0, entry);
            if (Logs.Count > 1000) Logs.RemoveAt(Logs.Count - 1);
        });
    }

    public void LoadData()
    {
        AppPaths.EnsureDirectoriesCreated();
        using var db = new AppDbContext();
        db.Database.EnsureCreated();

        Sources = new ObservableCollection<StorageSource>(db.StorageSources.ToList());
        Users = new ObservableCollection<User>(db.Users.Include(u => u.Permissions).ToList());
        
        var historyLogs = db.AuditLogs.OrderByDescending(l => l.Timestamp).Take(100).ToList();
        Logs = new ObservableCollection<AuditLogEntry>(historyLogs);
    }

    [RelayCommand]
    private void ToggleBot()
    {
        if (IsBotRunning)
        {
            _botService.Stop();
            IsBotRunning = false;
            StatusText = "Сервер остановлен";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(BotToken))
            {
                _auditLogger.Log("Warning", "System", "Нельзя запустить бота без токена!");
                return;
            }

            _botService.Start(BotToken);
            IsBotRunning = true;
            StatusText = "🟢 Сервер активен (работает Telegram-бот)";
        }
    }

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        if (string.IsNullOrWhiteSpace(NewSourceName) || string.IsNullOrWhiteSpace(NewSourcePath))
            return;

        using var db = new AppDbContext();
        var source = new StorageSource
        {
            Name = NewSourceName.Trim(),
            RootPath = NewSourcePath.Trim(),
            IsNetworkShare = NewSourcePath.StartsWith(@"\\"),
            IsEnabled = true
        };

        db.StorageSources.Add(source);
        await db.SaveChangesAsync();

        NewSourceName = "";
        NewSourcePath = "";
        LoadData();
        _auditLogger.Log("Info", "Storage", $"Добавлен новый источник: {source.Name} ({source.RootPath})");
    }

    [RelayCommand]
    private async Task AddUserAsync()
    {
        if (NewTelegramUserId <= 0 || string.IsNullOrWhiteSpace(NewUserDisplayName))
            return;

        using var db = new AppDbContext();
        var user = new User
        {
            TelegramUserId = NewTelegramUserId,
            DisplayName = NewUserDisplayName.Trim(),
            IsActive = true,
            IsAdmin = !db.Users.Any()
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        NewTelegramUserId = 0;
        NewUserDisplayName = "";
        LoadData();
        _auditLogger.Log("Info", "Security", $"Добавлен пользователь: {user.DisplayName} (ID: {user.TelegramUserId})");
    }

    [RelayCommand]
    private async Task SyncAllSourcesAsync()
    {
        foreach (var source in Sources)
        {
            await _syncService.SynchronizeSourceAsync(source.Id);
        }
        LoadData();
    }
}
