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
using TrufaBot.Infrastructure.Services;
using TrufaBot.Infrastructure.Telegram;

namespace TrufaBot.Presentation.ViewModels;

public class AppConfigModel
{
    public string BotToken { get; set; } = "";
    public string AiServerUrl { get; set; } = "http://localhost:1234";
    public string AiModelName { get; set; } = "";
    public bool IsAiEnabled { get; set; } = true;
    public bool AutoAiIndexing { get; set; } = false;
}

public partial class MainViewModel : ObservableObject
{
    private readonly IAuditLogger _auditLogger;
    private readonly TelegramBotService _botService;
    private readonly IStorageSyncService _syncService;
    private readonly IAiVisionService _aiVisionService;
    private readonly IFaceRecognitionService _faceService;
    private readonly FaceIndexingService _faceIndexingService;
    private readonly AiIndexingService _aiIndexingService;

    [ObservableProperty]
    private string _botToken = "";

    [ObservableProperty]
    private bool _isBotRunning;

    [ObservableProperty]
    private bool _isSyncing;

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

    // --- УПРАВЛЕНИЕ ПРАВАМИ ВЫБРАННОГО ПОЛЬЗОВАТЕЛЯ ---
    [ObservableProperty]
    private User? _selectedUser;

    [ObservableProperty]
    private ObservableCollection<UserFolderPermission> _selectedUserPermissions = new();

    [ObservableProperty]
    private StorageSource? _selectedPermissionSource;

    [ObservableProperty]
    private string _selectedFolderToGrant = "*";

    [ObservableProperty]
    private ObservableCollection<string> _availableFoldersForSelectedSource = new();

    // --- ЛОКАЛЬНЫЙ ИИ (LM STUDIO) ---
    [ObservableProperty]
    private string _aiServerUrl = "http://localhost:1234";

    [ObservableProperty]
    private string _aiModelName = "";

    [ObservableProperty]
    private bool _isAiEnabled = true;

    [ObservableProperty]
    private bool _autoAiIndexing = false;

    [ObservableProperty]
    private string _aiConnectionStatus = "Не проверено";

    [ObservableProperty]
    private bool _isAiConnected;

    [ObservableProperty]
    private bool _isAiIndexingRunning;

    [ObservableProperty]
    private int _aiTotalPhotos;

    [ObservableProperty]
    private int _aiProcessedPhotos;

    [ObservableProperty]
    private int _aiPendingPhotos;

    [ObservableProperty]
    private double _aiIndexingProgress;

    [ObservableProperty]
    private string _aiIndexingStatusText = "Готов к обработке";

    [ObservableProperty]
    private ObservableCollection<MediaItem> _recentAiProcessedItems = new();

    // --- ЧЛЕНЫ СЕМЬИ И ЛИЦА (FACE RECOGNITION) ---
    [ObservableProperty]
    private ObservableCollection<Person> _people = new();

    [ObservableProperty]
    private Person? _selectedPerson;

    [ObservableProperty]
    private string _newPersonName = "";

    [ObservableProperty]
    private string _newPersonNotes = "";

    [ObservableProperty]
    private bool _isFaceIndexingRunning;

    [ObservableProperty]
    private int _faceTotalPhotos;

    [ObservableProperty]
    private int _faceProcessedPhotos;

    [ObservableProperty]
    private double _faceIndexingProgress;

    [ObservableProperty]
    private string _faceIndexingStatusText = "Готов к сканированию лиц";

    [ObservableProperty]
    private int _totalKnownFacesCount;

    public MainViewModel(
        IAuditLogger auditLogger, 
        TelegramBotService botService, 
        IStorageSyncService syncService,
        IAiVisionService aiVisionService,
        IFaceRecognitionService faceService,
        FaceIndexingService faceIndexingService,
        AiIndexingService aiIndexingService)
    {
        _auditLogger = auditLogger;
        _botService = botService;
        _syncService = syncService;
        _aiVisionService = aiVisionService;
        _faceService = faceService;
        _faceIndexingService = faceIndexingService;
        _aiIndexingService = aiIndexingService;

        _aiIndexingService.ProgressChanged += OnAiIndexingProgressChanged;
        _faceIndexingService.ProgressChanged += OnFaceIndexingProgressChanged;

        if (_auditLogger is AuditLogger loggerImpl)
        {
            loggerImpl.LogAdded += OnLogAdded;
        }

        AppDbContext.EnsureSchemaUpdated();

        LoadConfig();
        LoadData();
        RefreshAiStats();
        RefreshFaceStats();
    }

    private void OnAiIndexingProgressChanged(object? sender, AiIndexingProgressEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            AiTotalPhotos = e.TotalCount;
            AiProcessedPhotos = e.ProcessedCount;
            AiPendingPhotos = Math.Max(0, e.TotalCount - e.ProcessedCount);
            AiIndexingProgress = e.TotalCount > 0 ? (double)e.ProcessedCount / e.TotalCount * 100 : 0;
            AiIndexingStatusText = e.StatusMessage;

            if (e.IsCompleted)
            {
                IsAiIndexingRunning = false;
                RefreshAiStats();
            }
        });
    }

    private void OnFaceIndexingProgressChanged(object? sender, FaceIndexingProgressEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            FaceTotalPhotos = e.TotalCount;
            FaceProcessedPhotos = e.ProcessedCount;
            FaceIndexingProgress = e.TotalCount > 0 ? (double)e.ProcessedCount / e.TotalCount * 100 : 0;
            FaceIndexingStatusText = e.StatusMessage;

            if (e.IsCompleted)
            {
                IsFaceIndexingRunning = false;
                RefreshFaceStats();
            }
        });
    }

    partial void OnSelectedUserChanged(User? value)
    {
        LoadPermissionsForSelectedUser();
    }

    partial void OnSelectedPermissionSourceChanged(StorageSource? value)
    {
        UpdateAvailableFoldersForSelectedSource();
    }

    private void OnLogAdded(AuditLogEntry entry)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            Logs.Insert(0, entry);
            if (Logs.Count > 500)
            {
                Logs.RemoveAt(Logs.Count - 1);
            }
        });
    }

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigFilePath))
            {
                var json = File.ReadAllText(AppPaths.ConfigFilePath);
                var config = JsonSerializer.Deserialize<AppConfigModel>(json);
                if (config != null)
                {
                    BotToken = config.BotToken;
                    if (!string.IsNullOrEmpty(config.AiServerUrl)) AiServerUrl = config.AiServerUrl;
                    if (!string.IsNullOrEmpty(config.AiModelName)) AiModelName = config.AiModelName;
                    IsAiEnabled = config.IsAiEnabled;
                    AutoAiIndexing = config.AutoAiIndexing;
                }
            }
        }
        catch (Exception ex)
        {
            _auditLogger.Log("Error", "Config", $"Ошибка загрузки конфигурации: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        try
        {
            var config = new AppConfigModel
            {
                BotToken = BotToken,
                AiServerUrl = AiServerUrl,
                AiModelName = AiModelName,
                IsAiEnabled = IsAiEnabled,
                AutoAiIndexing = AutoAiIndexing
            };
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.ConfigFilePath, json);
        }
        catch (Exception ex)
        {
            _auditLogger.Log("Error", "Config", $"Ошибка сохранения конфигурации: {ex.Message}");
        }
    }

    private void LoadData()
    {
        using var db = new AppDbContext();

        Sources.Clear();
        foreach (var s in db.StorageSources.ToList())
        {
            Sources.Add(s);
        }

        Users.Clear();
        foreach (var u in db.Users.Include(u => u.Permissions).ToList())
        {
            Users.Add(u);
        }

        People.Clear();
        foreach (var p in db.People.Include(p => p.Faces).ToList())
        {
            People.Add(p);
        }

        if (SelectedUser == null && Users.Any())
        {
            SelectedUser = Users.First();
        }

        if (SelectedPermissionSource == null && Sources.Any())
        {
            SelectedPermissionSource = Sources.First();
        }
    }

    public void RefreshAiStats()
    {
        Task.Run(async () =>
        {
            try
            {
                using var db = new AppDbContext();
                var total = await db.MediaItems.CountAsync(m => !m.IsDeleted);
                var processed = await db.MediaItems.CountAsync(m => !m.IsDeleted && !string.IsNullOrEmpty(m.AIDescription));
                var pending = total - processed;

                var recent = await db.MediaItems
                    .Where(m => !m.IsDeleted && !string.IsNullOrEmpty(m.AIDescription))
                    .OrderByDescending(m => m.AIProcessedAt)
                    .Take(10)
                    .ToListAsync();

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    AiTotalPhotos = total;
                    AiProcessedPhotos = processed;
                    AiPendingPhotos = Math.Max(0, pending);
                    AiIndexingProgress = total > 0 ? (double)processed / total * 100 : 0;

                    RecentAiProcessedItems.Clear();
                    foreach (var item in recent)
                    {
                        RecentAiProcessedItems.Add(item);
                    }
                });
            }
            catch { }
        });
    }

    public void RefreshFaceStats()
    {
        Task.Run(async () =>
        {
            try
            {
                using var db = new AppDbContext();
                var totalFaces = await db.PersonFaces.CountAsync();
                var peopleList = await db.People.Include(p => p.Faces).ToListAsync();

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    TotalKnownFacesCount = totalFaces;
                    People.Clear();
                    foreach (var p in peopleList)
                    {
                        People.Add(p);
                    }
                });
            }
            catch { }
        });
    }

    [RelayCommand]
    private async Task AddPersonAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPersonName))
        {
            System.Windows.MessageBox.Show("Введите имя члена семьи!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        using var db = new AppDbContext();
        var person = new Person
        {
            Name = NewPersonName.Trim(),
            Notes = NewPersonNotes.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        db.People.Add(person);
        await db.SaveChangesAsync();

        _auditLogger.Log("Info", "Faces", $"Добавлен член семьи: {person.Name}");

        NewPersonName = "";
        NewPersonNotes = "";
        RefreshFaceStats();
    }

    [RelayCommand]
    private async Task DeletePersonAsync(Person? person)
    {
        if (person == null) return;

        if (System.Windows.MessageBox.Show($"Удалить профиль '{person.Name}'?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            using var db = new AppDbContext();
            var p = await db.People.Include(p => p.Faces).FirstOrDefaultAsync(x => x.Id == person.Id);
            if (p != null)
            {
                foreach (var face in p.Faces)
                {
                    face.PersonId = null;
                }
                db.People.Remove(p);
                await db.SaveChangesAsync();

                _auditLogger.Log("Info", "Faces", $"Удален профиль: {person.Name}");
                RefreshFaceStats();
            }
        }
    }

    [RelayCommand]
    private void StartFaceIndexing()
    {
        if (IsFaceIndexingRunning) return;
        IsFaceIndexingRunning = true;
        _ = _faceIndexingService.StartFaceIndexingAsync();
    }

    [RelayCommand]
    private void StopFaceIndexing()
    {
        _faceIndexingService.Stop();
        IsFaceIndexingRunning = false;
        FaceIndexingStatusText = "Остановлено пользователем.";
    }

    [RelayCommand]
    private async Task TestAiConnectionAsync()
    {
        SaveConfig();
        AiConnectionStatus = "⏳ Проверка связи...";
        var (success, modelName, message) = await _aiVisionService.TestConnectionAsync(AiServerUrl);
        IsAiConnected = success;
        AiConnectionStatus = message;
        if (success && !string.IsNullOrEmpty(modelName))
        {
            AiModelName = modelName;
            SaveConfig();
        }
    }

    [RelayCommand]
    private void StartAiIndexing()
    {
        if (IsAiIndexingRunning) return;
        SaveConfig();
        IsAiIndexingRunning = true;
        _ = _aiIndexingService.StartIndexingAsync(AiServerUrl, AiModelName);
    }

    [RelayCommand]
    private void StopAiIndexing()
    {
        _aiIndexingService.Stop();
        IsAiIndexingRunning = false;
        AiIndexingStatusText = "Остановлено пользователем.";
    }

    [RelayCommand]
    private void ToggleBot()
    {
        SaveConfig();

        if (_botService.IsRunning)
        {
            _botService.Stop();
            IsBotRunning = false;
            StatusText = "Сервер остановлен";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(BotToken))
            {
                System.Windows.MessageBox.Show("Введите токен Telegram бота!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _botService.Start(BotToken.Trim());
                IsBotRunning = true;
                StatusText = "🟢 Сервер запущен и принимает запросы";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Ошибка запуска бота: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private async Task SyncAllSourcesAsync()
    {
        if (IsSyncing) return;
        IsSyncing = true;
        StatusText = "⏳ Идет сканирование источников...";

        try
        {
            var enabledSources = Sources.Where(s => s.IsEnabled).ToList();
            foreach (var src in enabledSources)
            {
                await _syncService.SynchronizeSourceAsync(src.Id);
            }
            LoadData();
            UpdateAvailableFoldersForSelectedSource();
            RefreshAiStats();
            RefreshFaceStats();
            StatusText = IsBotRunning ? "🟢 Сервер запущен и принимает запросы" : "Сервер остановлен";
        }
        finally
        {
            IsSyncing = false;
        }
    }

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        if (string.IsNullOrWhiteSpace(NewSourceName) || string.IsNullOrWhiteSpace(NewSourcePath))
        {
            System.Windows.MessageBox.Show("Укажите имя и путь к источнику!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var source = new StorageSource
        {
            Name = NewSourceName.Trim(),
            RootPath = NewSourcePath.Trim(),
            IsNetworkShare = NewSourcePath.StartsWith(@"\\") || NewSourcePath.StartsWith("//"),
            IsEnabled = true,
            CreatedAt = DateTime.UtcNow
        };

        using (var db = new AppDbContext())
        {
            db.StorageSources.Add(source);
            await db.SaveChangesAsync();
        }

        _auditLogger.Log("Info", "Storage", $"Добавлен источник: {source.Name} ({source.RootPath})");

        NewSourceName = "";
        NewSourcePath = "";
        LoadData();

        _ = Task.Run(async () =>
        {
            await _syncService.SynchronizeSourceAsync(source.Id);
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                LoadData();
                UpdateAvailableFoldersForSelectedSource();
                RefreshAiStats();
                RefreshFaceStats();
            });
        });
    }

    [RelayCommand]
    private async Task ToggleSourceEnabledAsync(StorageSource? source)
    {
        if (source == null) return;

        using var db = new AppDbContext();
        var src = await db.StorageSources.FindAsync(source.Id);
        if (src != null)
        {
            src.IsEnabled = !src.IsEnabled;
            await db.SaveChangesAsync();
            _auditLogger.Log("Info", "Storage", $"Источник '{src.Name}' {(src.IsEnabled ? "включен" : "отключен")}");
            LoadData();
        }
    }

    [RelayCommand]
    private async Task DeleteSourceAsync(StorageSource? source)
    {
        if (source == null) return;

        if (System.Windows.MessageBox.Show($"Удалить источник '{source.Name}'?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            using var db = new AppDbContext();
            var src = await db.StorageSources
                .Include(s => s.MediaItems)
                .Include(s => s.Permissions)
                .FirstOrDefaultAsync(s => s.Id == source.Id);

            if (src != null)
            {
                db.MediaItems.RemoveRange(src.MediaItems);
                db.UserFolderPermissions.RemoveRange(src.Permissions);
                db.StorageSources.Remove(src);
                await db.SaveChangesAsync();
                _auditLogger.Log("Info", "Storage", $"Удален источник '{source.Name}'");
                LoadData();
            }
        }
    }

    [RelayCommand]
    private async Task AddUserAsync()
    {
        if (NewTelegramUserId == 0 || string.IsNullOrWhiteSpace(NewUserDisplayName))
        {
            System.Windows.MessageBox.Show("Укажите Telegram ID и имя пользователя!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        using var db = new AppDbContext();
        if (await db.Users.AnyAsync(u => u.TelegramUserId == NewTelegramUserId))
        {
            System.Windows.MessageBox.Show("Пользователь с таким Telegram ID уже существует!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var user = new User
        {
            TelegramUserId = NewTelegramUserId,
            DisplayName = NewUserDisplayName.Trim(),
            IsActive = true,
            IsAdmin = !await db.Users.AnyAsync(),
            CreatedAt = DateTime.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        _auditLogger.Log("Info", "Security", $"Добавлен пользователь: {user.DisplayName} (ID: {user.TelegramUserId})");

        NewTelegramUserId = 0;
        NewUserDisplayName = "";
        LoadData();
    }

    [RelayCommand]
    private async Task ToggleUserAdminAsync(User? user)
    {
        if (user == null) return;

        using var db = new AppDbContext();
        var u = await db.Users.FindAsync(user.Id);
        if (u != null)
        {
            u.IsAdmin = !u.IsAdmin;
            await db.SaveChangesAsync();
            _auditLogger.Log("Info", "Security", $"Пользователю '{u.DisplayName}' {(u.IsAdmin ? "назначены права администратора" : "сняты права администратора")}");
            LoadData();
        }
    }

    [RelayCommand]
    private async Task ToggleUserActiveAsync(User? user)
    {
        if (user == null) return;

        using var db = new AppDbContext();
        var u = await db.Users.FindAsync(user.Id);
        if (u != null)
        {
            u.IsActive = !u.IsActive;
            await db.SaveChangesAsync();
            _auditLogger.Log("Info", "Security", $"Пользователь '{u.DisplayName}' {(u.IsActive ? "активирован" : "заблокирован")}");
            LoadData();
        }
    }

    [RelayCommand]
    private async Task DeleteUserAsync(User? user)
    {
        if (user == null) return;

        if (System.Windows.MessageBox.Show($"Удалить пользователя '{user.DisplayName}'?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            using var db = new AppDbContext();
            var u = await db.Users
                .Include(u => u.Permissions)
                .FirstOrDefaultAsync(u => u.Id == user.Id);

            if (u != null)
            {
                db.UserFolderPermissions.RemoveRange(u.Permissions);
                db.Users.Remove(u);
                await db.SaveChangesAsync();
                _auditLogger.Log("Info", "Security", $"Удален пользователь '{user.DisplayName}'");
                LoadData();
            }
        }
    }

    private void LoadPermissionsForSelectedUser()
    {
        SelectedUserPermissions.Clear();
        if (SelectedUser == null) return;

        using var db = new AppDbContext();
        var perms = db.UserFolderPermissions
            .Include(p => p.StorageSource)
            .Where(p => p.UserId == SelectedUser.Id)
            .ToList();

        foreach (var p in perms)
        {
            SelectedUserPermissions.Add(p);
        }
    }

    private void UpdateAvailableFoldersForSelectedSource()
    {
        AvailableFoldersForSelectedSource.Clear();
        AvailableFoldersForSelectedSource.Add("* (ВЕСЬ ДИСК/ИСТОЧНИК ПОЛНОСТЬЮ)");

        if (SelectedPermissionSource == null || !Directory.Exists(SelectedPermissionSource.RootPath))
        {
            SelectedFolderToGrant = AvailableFoldersForSelectedSource.First();
            return;
        }

        try
        {
            var topDirs = Directory.GetDirectories(SelectedPermissionSource.RootPath)
                .Select(d => Path.GetFileName(d))
                .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith("@") && !n.StartsWith("$") && !n.StartsWith("."))
                .OrderBy(n => n);

            foreach (var dir in topDirs)
            {
                AvailableFoldersForSelectedSource.Add(dir);
            }
        }
        catch { }

        SelectedFolderToGrant = AvailableFoldersForSelectedSource.First();
    }

    [RelayCommand]
    private async Task AddPermissionForSelectedUserAsync()
    {
        if (SelectedUser == null || SelectedPermissionSource == null)
        {
            System.Windows.MessageBox.Show("Выберите пользователя и источник!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var folder = SelectedFolderToGrant.StartsWith("*") ? "*" : SelectedFolderToGrant.Trim();

        using var db = new AppDbContext();
        var existing = await db.UserFolderPermissions
            .FirstOrDefaultAsync(p => p.UserId == SelectedUser.Id && p.StorageSourceId == SelectedPermissionSource.Id && p.AllowedRelativePath == folder);

        if (existing != null)
        {
            System.Windows.MessageBox.Show("Такое разрешение уже назначено пользователю!", "Информация", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var perm = new UserFolderPermission
        {
            UserId = SelectedUser.Id,
            StorageSourceId = SelectedPermissionSource.Id,
            AllowedRelativePath = folder,
            IsRecursive = true,
            IsDenied = false
        };

        db.UserFolderPermissions.Add(perm);
        await db.SaveChangesAsync();

        _auditLogger.Log("Info", "Security", $"Пользователю '{SelectedUser.DisplayName}' открыт доступ к [{SelectedPermissionSource.Name} -> {folder}]");

        LoadPermissionsForSelectedUser();
    }

    [RelayCommand]
    private async Task DeletePermissionAsync(UserFolderPermission? perm)
    {
        if (perm == null) return;

        using var db = new AppDbContext();
        var p = await db.UserFolderPermissions.FindAsync(perm.Id);
        if (p != null)
        {
            db.UserFolderPermissions.Remove(p);
            await db.SaveChangesAsync();
            _auditLogger.Log("Info", "Security", $"Отозван доступ к [{perm.StorageSource?.Name} -> {perm.AllowedRelativePath}] у пользователя ID: {perm.UserId}");
            LoadPermissionsForSelectedUser();
        }
    }
}
