using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
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

public class AppConfigModel
{
    public string BotToken { get; set; } = "";
}

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

        LoadConfig();
        LoadData();
    }

    private void LoadConfig()
    {
        AppPaths.EnsureDirectoriesCreated();
        if (File.Exists(AppPaths.ConfigFilePath))
        {
            try
            {
                var json = File.ReadAllText(AppPaths.ConfigFilePath);
                var config = JsonSerializer.Deserialize<AppConfigModel>(json);
                if (config != null && !string.IsNullOrWhiteSpace(config.BotToken))
                {
                    BotToken = config.BotToken;
                }
            }
            catch { }
        }
    }

    private void SaveConfig()
    {
        AppPaths.EnsureDirectoriesCreated();
        var config = new AppConfigModel { BotToken = BotToken };
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AppPaths.ConfigFilePath, json);
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

            SaveConfig();
            _botService.Start(BotToken);
            IsBotRunning = true;
            StatusText = "🟢 Сервер активен (Telegram-бот слушает)";
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
        _auditLogger.Log("Info", "Storage", $"Добавлен источник: {source.Name} ({source.RootPath})");

        await _syncService.SynchronizeSourceAsync(source.Id);
        LoadData();
    }

    [RelayCommand]
    private async Task DeleteSourceAsync(StorageSource? source)
    {
        if (source == null) return;

        using var db = new AppDbContext();
        var existing = await db.StorageSources
            .Include(s => s.MediaItems)
            .Include(s => s.Permissions)
            .FirstOrDefaultAsync(s => s.Id == source.Id);

        if (existing != null)
        {
            db.MediaItems.RemoveRange(existing.MediaItems);
            db.UserFolderPermissions.RemoveRange(existing.Permissions);
            db.StorageSources.Remove(existing);
            await db.SaveChangesAsync();

            _auditLogger.Log("Info", "Storage", $"Удален источник: {source.Name}");
            LoadData();
        }
    }

    [RelayCommand]
    private async Task ToggleSourceEnabledAsync(StorageSource? source)
    {
        if (source == null) return;

        using var db = new AppDbContext();
        var existing = await db.StorageSources.FindAsync(source.Id);
        if (existing != null)
        {
            existing.IsEnabled = !existing.IsEnabled;
            await db.SaveChangesAsync();

            _auditLogger.Log("Info", "Storage", $"Источник '{existing.Name}' {(existing.IsEnabled ? "включен" : "отключен")}");
            LoadData();
        }
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
    private async Task DeleteUserAsync(User? user)
    {
        if (user == null) return;

        using var db = new AppDbContext();
        var existing = await db.Users
            .Include(u => u.Permissions)
            .FirstOrDefaultAsync(u => u.Id == user.Id);

        if (existing != null)
        {
            db.UserFolderPermissions.RemoveRange(existing.Permissions);
            db.Users.Remove(existing);
            await db.SaveChangesAsync();

            _auditLogger.Log("Info", "Security", $"Удален пользователь: {user.DisplayName} (ID: {user.TelegramUserId})");
            LoadData();
        }
    }

    [RelayCommand]
    private async Task ToggleUserActiveAsync(User? user)
    {
        if (user == null) return;

        using var db = new AppDbContext();
        var existing = await db.Users.FindAsync(user.Id);
        if (existing != null)
        {
            existing.IsActive = !existing.IsActive;
            await db.SaveChangesAsync();

            _auditLogger.Log("Info", "Security", $"Пользователь '{existing.DisplayName}' {(existing.IsActive ? "активирован" : "деактивирован")}");
            LoadData();
        }
    }

    [RelayCommand]
    private async Task SyncAllSourcesAsync()
    {
        foreach (var source in Sources)
        {
            if (source.IsEnabled)
            {
                await _syncService.SynchronizeSourceAsync(source.Id);
            }
        }
        LoadData();
    }
}
