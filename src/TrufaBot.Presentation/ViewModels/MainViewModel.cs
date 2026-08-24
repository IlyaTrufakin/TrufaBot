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
using TrufaBot.Infrastructure.Storage;
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

public partial class PersonItemViewModel : ObservableObject
{
    public int Id { get; set; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _category = "Семья";

    [ObservableProperty]
    private string? _notes;

    [ObservableProperty]
    private int _photosCount;

    public DateTime CreatedAt { get; set; }
}

public partial class UnassignedFaceItemViewModel : ObservableObject
{
    public long FaceId { get; set; }
    public long MediaItemId { get; set; }
    public string FileName { get; set; } = "";
    public string FullImagePath { get; set; } = "";
    public string CropThumbnailPath { get; set; } = "";

    [ObservableProperty]
    private PersonItemViewModel? _selectedPerson;
}

public partial class PersonPhotoItemViewModel : ObservableObject
{
    public long MediaItemId { get; set; }
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string FullImagePath { get; set; } = "";
    
    [ObservableProperty]
    private string _thumbnailPath = "";

    public string? AIDescription { get; set; }
    public DateTime FileCreatedAt { get; set; }
}

public partial class MainViewModel : ObservableObject
{
    private readonly IAuditLogger _auditLogger;
    private readonly TelegramBotService _botService;
    private readonly IStorageSyncService _syncService;
    private readonly IThumbnailService _thumbnailService;
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
    private ObservableCollection<PersonItemViewModel> _people = new();

    [ObservableProperty]
    private PersonItemViewModel? _selectedPerson;

    [ObservableProperty]
    private string _newPersonName = "";

    [ObservableProperty]
    private string _selectedPersonCategory = "Семья";

    [ObservableProperty]
    private ObservableCollection<string> _availablePersonCategories = new() { "Семья", "Родственники", "Друзья", "Знакомые", "Коллеги" };

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

    [ObservableProperty]
    private ObservableCollection<UnassignedFaceItemViewModel> _unassignedFaces = new();

    [ObservableProperty]
    private ObservableCollection<PersonPhotoItemViewModel> _selectedPersonPhotos = new();

    public MainViewModel(
        IAuditLogger auditLogger, 
        TelegramBotService botService, 
        IStorageSyncService syncService,
        IThumbnailService thumbnailService,
        IAiVisionService aiVisionService,
        IFaceRecognitionService faceService,
        FaceIndexingService faceIndexingService,
        AiIndexingService aiIndexingService)
    {
        _auditLogger = auditLogger;
        _botService = botService;
        _syncService = syncService;
        _thumbnailService = thumbnailService;
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
        _ = LoadUnassignedFacesAsync();
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
                _ = LoadUnassignedFacesAsync();
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

    partial void OnSelectedPersonChanged(PersonItemViewModel? value)
    {
        LoadPhotosForSelectedPerson(value);
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
                var totalFaces = await db.PersonFaces.CountAsync(f => !f.IsIgnored);
                var peopleList = await db.People.OrderBy(p => p.Category).ThenBy(p => p.Name).ToListAsync();

                // Считаем уникальные фотографии для каждого человека
                var distinctCounts = await db.PersonFaces
                    .Where(f => f.PersonId != null && !f.IsIgnored && !f.MediaItem.IsDeleted && f.MediaItem.StorageSource.IsEnabled)
                    .GroupBy(f => f.PersonId!.Value)
                    .Select(g => new { PersonId = g.Key, Count = g.Select(x => x.MediaItemId).Distinct().Count() })
                    .ToDictionaryAsync(x => x.PersonId, x => x.Count);

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    TotalKnownFacesCount = totalFaces;
                    var currentSelectedId = SelectedPerson?.Id;

                    var dict = peopleList.ToDictionary(p => p.Id);

                    for (int i = People.Count - 1; i >= 0; i--)
                    {
                        if (!dict.ContainsKey(People[i].Id))
                        {
                            People.RemoveAt(i);
                        }
                    }

                    for (int i = 0; i < peopleList.Count; i++)
                    {
                        var fresh = peopleList[i];
                        int count = distinctCounts.GetValueOrDefault(fresh.Id, 0);

                        var existing = People.FirstOrDefault(x => x.Id == fresh.Id);
                        if (existing != null)
                        {
                            existing.Name = fresh.Name;
                            existing.Category = fresh.Category;
                            existing.Notes = fresh.Notes;
                            existing.PhotosCount = count;
                        }
                        else
                        {
                            var vm = new PersonItemViewModel
                            {
                                Id = fresh.Id,
                                Name = fresh.Name,
                                Category = fresh.Category,
                                Notes = fresh.Notes,
                                PhotosCount = count,
                                CreatedAt = fresh.CreatedAt
                            };
                            People.Insert(i, vm);
                        }
                    }

                    if (currentSelectedId.HasValue)
                    {
                        var matched = People.FirstOrDefault(x => x.Id == currentSelectedId.Value);
                        if (matched != null && (SelectedPerson == null || SelectedPerson.Id != matched.Id))
                        {
                            SelectedPerson = matched;
                        }
                    }
                    else if (People.Any() && SelectedPerson == null)
                    {
                        SelectedPerson = People.First();
                    }
                });
            }
            catch { }
        });
    }

    public async Task LoadUnassignedFacesAsync()
    {
        try
        {
            using var db = new AppDbContext();
            var unassigned = await db.PersonFaces
                .Include(f => f.MediaItem)
                .ThenInclude(m => m.StorageSource)
                .Where(f => f.PersonId == null && !f.IsIgnored && !f.MediaItem.IsDeleted && f.MediaItem.StorageSource.IsEnabled)
                .OrderByDescending(f => f.Id)
                .Take(12)
                .ToListAsync();

            var list = new List<UnassignedFaceItemViewModel>();
            foreach (var face in unassigned)
            {
                var fullPath = Path.Combine(face.MediaItem.StorageSource.RootPath, face.MediaItem.RelativePath.Replace('/', '\\'));
                var cropPath = await _faceService.GetOrCreateFaceCropThumbnailAsync(fullPath, face.BoxX, face.BoxY, face.BoxWidth, face.BoxHeight, face.Id);

                list.Add(new UnassignedFaceItemViewModel
                {
                    FaceId = face.Id,
                    MediaItemId = face.MediaItemId,
                    FileName = face.MediaItem.FileName,
                    FullImagePath = fullPath,
                    CropThumbnailPath = cropPath,
                    SelectedPerson = null
                });
            }

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                UnassignedFaces.Clear();
                foreach (var item in list)
                {
                    UnassignedFaces.Add(item);
                }
            });
        }
        catch (Exception ex)
        {
            _auditLogger.Log("Error", "Faces", $"Ошибка загрузки лиц: {ex.Message}");
        }
    }

    private void LoadPhotosForSelectedPerson(PersonItemViewModel? person)
    {
        if (person == null)
        {
            SelectedPersonPhotos.Clear();
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                using var db = new AppDbContext();
                var photos = await db.PersonFaces
                    .Include(f => f.MediaItem)
                    .ThenInclude(m => m.StorageSource)
                    .Where(f => f.PersonId == person.Id && !f.IsIgnored && !f.MediaItem.IsDeleted && f.MediaItem.StorageSource.IsEnabled)
                    .Select(f => f.MediaItem)
                    .Distinct()
                    .OrderByDescending(m => m.FileCreatedAt)
                    .Take(50)
                    .ToListAsync();

                var viewModels = new List<PersonPhotoItemViewModel>();

                foreach (var photo in photos)
                {
                    var fullPath = Path.Combine(photo.StorageSource.RootPath, photo.RelativePath.Replace('/', '\\'));
                    var thumb = await _thumbnailService.GetOrCreateThumbnailAsync(fullPath, 240, 240);

                    viewModels.Add(new PersonPhotoItemViewModel
                    {
                        MediaItemId = photo.Id,
                        FileName = photo.FileName,
                        RelativePath = photo.RelativePath,
                        FullImagePath = fullPath,
                        ThumbnailPath = thumb,
                        AIDescription = photo.AIDescription,
                        FileCreatedAt = photo.FileCreatedAt
                    });
                }

                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    SelectedPersonPhotos.Clear();
                    foreach (var vm in viewModels)
                    {
                        SelectedPersonPhotos.Add(vm);
                    }
                    person.PhotosCount = SelectedPersonPhotos.Count;
                });
            }
            catch { }
        });
    }

    [RelayCommand]
    private async Task AutoMatchAllFacesAsync()
    {
        StatusText = "⏳ Нейросеть распознает лица людей во всем архиве...";
        int matched = await _faceService.AutoMatchAllKnownPeopleAsync(threshold: 0.42f);
        StatusText = IsBotRunning ? "🟢 Сервер запущен и принимает запросы" : "Сервер остановлен";

        _auditLogger.Log("Info", "Faces", $"Авто-распознавание завершено! Нейросеть нашла и привязала {matched} новых фото к добавленным людям.");
        System.Windows.MessageBox.Show($"Нейросеть успешно распознала и привязала {matched} фото к членам семьи и друзьям по всему архиву!", "Авто-распознавание", MessageBoxButton.OK, MessageBoxImage.Information);

        RefreshFaceStats();
        await LoadUnassignedFacesAsync();
        LoadPhotosForSelectedPerson(SelectedPerson);
    }

    [RelayCommand]
    private async Task AssignFaceAsync(UnassignedFaceItemViewModel? item)
    {
        if (item == null || item.SelectedPerson == null)
        {
            System.Windows.MessageBox.Show("Пожалуйста, сначала выберите человека в выпадающем списке на карточке лица!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var personId = item.SelectedPerson.Id;
        var personName = item.SelectedPerson.Name;

        try
        {
            await _faceService.AssignFaceToPersonAsync(item.FaceId, personId);
            _auditLogger.Log("Info", "Faces", $"Лицо на фото '{item.FileName}' привязано к '{personName}'.");

            UnassignedFaces.Remove(item);
            RefreshFaceStats();
            LoadPhotosForSelectedPerson(SelectedPerson);
        }
        catch (Exception ex)
        {
            _auditLogger.Log("Error", "Faces", $"Ошибка привязки лица: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task IgnoreFaceAsync(UnassignedFaceItemViewModel? item)
    {
        if (item == null) return;
        try
        {
            await _faceService.IgnoreFaceAsync(item.FaceId);
            _auditLogger.Log("Info", "Faces", $"Лицо на '{item.FileName}' помечено как незнакомец (скрыто).");
            UnassignedFaces.Remove(item);
            RefreshFaceStats();
        }
        catch (Exception ex)
        {
            _auditLogger.Log("Error", "Faces", $"Ошибка при скрытии лица: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteFalseFaceAsync(UnassignedFaceItemViewModel? item)
    {
        if (item == null) return;
        try
        {
            await _faceService.DeleteFaceAsync(item.FaceId);
            _auditLogger.Log("Info", "Faces", $"Удалено ошибочное распознавание лица на '{item.FileName}'.");
            UnassignedFaces.Remove(item);
            RefreshFaceStats();
        }
        catch (Exception ex)
        {
            _auditLogger.Log("Error", "Faces", $"Ошибка удаления детекции лица: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task UnassignPhotoFromPersonAsync(PersonPhotoItemViewModel? photoItem)
    {
        if (photoItem == null || SelectedPerson == null) return;

        try
        {
            var personId = SelectedPerson.Id;
            var personName = SelectedPerson.Name;

            using var db = new AppDbContext();
            var faces = await db.PersonFaces
                .Where(f => f.MediaItemId == photoItem.MediaItemId && f.PersonId == personId)
                .ToListAsync();

            foreach (var f in faces)
            {
                f.PersonId = null;
            }
            await db.SaveChangesAsync();

            _auditLogger.Log("Info", "Faces", $"Фото '{photoItem.FileName}' успешно отвязано от '{personName}'.");

            // Мгновенно убираем фото из галереи
            SelectedPersonPhotos.Remove(photoItem);

            // Мгновенно уменьшаем реактивный счетчик у человека в таблице
            if (SelectedPerson.PhotosCount > 0)
            {
                SelectedPerson.PhotosCount--;
            }

            // Обновляем статистику в фоне
            RefreshFaceStats();
            await LoadUnassignedFacesAsync();
        }
        catch (Exception ex)
        {
            _auditLogger.Log("Error", "Faces", $"Ошибка отвязки фото: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ResetAllFaceAssignmentsAsync()
    {
        if (System.Windows.MessageBox.Show("Сбросить все привязки фотографий к людям? Фотографии останутся на диске, но альбомы людей вернутся в исходное состояние.", "Сброс привязок", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            await _faceService.ResetAllAssignmentsAsync();
            _auditLogger.Log("Info", "Faces", "Все привязки фотографий к людям были сброшены.");
            RefreshFaceStats();
            await LoadUnassignedFacesAsync();
            LoadPhotosForSelectedPerson(SelectedPerson);
        }
    }

    [RelayCommand]
    private async Task ClearAndRescanFacesAsync()
    {
        if (System.Windows.MessageBox.Show("Удалить старые сканы лиц и пересканировать архив заново с точным фильтром людей (исключая растения и фоны)?", "Очистка и пересканирование", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            await _faceService.ClearAllFacesAndResetAsync();
            _auditLogger.Log("Info", "Faces", "База лиц очищена от старых сканов. Запускается чистое сканирование...");
            RefreshFaceStats();
            await LoadUnassignedFacesAsync();
            StartFaceIndexing();
        }
    }

    [RelayCommand]
    private async Task RefreshFacesListAsync()
    {
        await LoadUnassignedFacesAsync();
        LoadPhotosForSelectedPerson(SelectedPerson);
    }

    [RelayCommand]
    private async Task AddPersonAsync()
    {
        if (string.IsNullOrWhiteSpace(NewPersonName))
        {
            System.Windows.MessageBox.Show("Введите имя человека!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        using var db = new AppDbContext();
        var person = new Person
        {
            Name = NewPersonName.Trim(),
            Category = string.IsNullOrWhiteSpace(SelectedPersonCategory) ? "Семья" : SelectedPersonCategory.Trim(),
            Notes = NewPersonNotes.Trim(),
            CreatedAt = DateTime.UtcNow
        };

        db.People.Add(person);
        await db.SaveChangesAsync();

        _auditLogger.Log("Info", "Faces", $"Добавлен профиль: {person.Name} ({person.Category})");

        NewPersonName = "";
        NewPersonNotes = "";
        RefreshFaceStats();
    }

    [RelayCommand]
    private async Task DeletePersonAsync(PersonItemViewModel? person)
    {
        if (person == null) return;

        if (System.Windows.MessageBox.Show($"Удалить профиль '{person.Name}' ({person.Category})?", "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
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
                await LoadUnassignedFacesAsync();
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
