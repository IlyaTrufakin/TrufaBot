using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using TrufaBot.Application.Interfaces;
using TrufaBot.Application.Services;
using TrufaBot.Domain.Entities;
using TrufaBot.Infrastructure.Data;
using TrufaBot.Infrastructure.Storage;

namespace TrufaBot.Infrastructure.Telegram;

public class UserBrowseState
{
    public int SourceId { get; set; }
    public string FolderPath { get; set; } = "";
    public int DirPage { get; set; } = 0;
    public int FilePage { get; set; } = 0;
    public List<string> CachedSubDirs { get; set; } = new();
    public List<MediaItem> CachedFiles { get; set; } = new();
    public bool IsSearchMode { get; set; }
    public string SearchQuery { get; set; } = "";
}

public class TelegramBotService
{
    private const long MaxTelegramUploadBytes = 49 * 1024 * 1024;
    private const int DirPageSize = 20;
    private const int FilePageSize = 10;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".heic"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".webm", ".3gp", ".m4v", ".flv", ".mts", ".ts"
    };

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "@recycle", "#recycle", "$recycle.bin", "system volume information", "@eadir", ".git", ".vs", ".sync", ".tmp", "thumbnails"
    };

    private readonly IAuditLogger _logger;
    private readonly IAuthorizationService _authService;
    private readonly IThumbnailService _thumbnailService;
    private readonly ConcurrentDictionary<long, UserBrowseState> _userSessions = new();
    private ITelegramBotClient? _botClient;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _botClient != null && _cts != null && !_cts.IsCancellationRequested;

    public TelegramBotService(IAuditLogger logger, IAuthorizationService authService, IThumbnailService thumbnailService)
    {
        _logger = logger;
        _authService = authService;
        _thumbnailService = thumbnailService;
    }

    public void Start(string token)
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _botClient = new TelegramBotClient(token);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery }
        };

        _botClient.StartReceiving(
            HandleUpdateAsync,
            HandleErrorAsync,
            receiverOptions,
            _cts.Token
        );

        _ = _botClient.SetMyCommands(new[]
        {
            new BotCommand { Command = "start", Description = "🏠 Главное меню" },
            new BotCommand { Command = "browse", Description = "📁 Проводник папок" },
            new BotCommand { Command = "people", Description = "👥 Люди (Семья и Друзья)" },
            new BotCommand { Command = "search", Description = "🔍 Умный поиск по фото" },
            new BotCommand { Command = "random", Description = "🎲 Случайное фото" }
        }, cancellationToken: _cts.Token);

        _logger.Log("Info", "Telegram", "Telegram бот успешно запущен и слушает запросы.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _botClient = null;
        _logger.Log("Info", "Telegram", "Telegram бот остановлен.");
    }

    private static ReplyKeyboardMarkup GetPersistentMenuKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "📁 Проводник архива", "👥 Люди (Семья / Друзья)" },
            new KeyboardButton[] { "🎲 Случайное фото" }
        })
        {
            ResizeKeyboard = true,
            IsPersistent = true
        };
    }

    private static bool IsIgnoredPath(string path)
    {
        var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Any(p => IgnoredDirectories.Contains(p) || p.StartsWith("@") || p.StartsWith("$") || (p.StartsWith(".") && p.Length > 1));
    }

    private async Task SafeEditMessageTextAsync(ITelegramBotClient bot, long chatId, int messageId, string text, InlineKeyboardMarkup replyMarkup, CancellationToken ct)
    {
        try
        {
            await bot.EditMessageText(
                chatId,
                messageId,
                text,
                parseMode: ParseMode.Html,
                replyMarkup: replyMarkup,
                cancellationToken: ct
            );
        }
        catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        try
        {
            long userId = update.Message?.From?.Id ?? update.CallbackQuery?.From.Id ?? 0;
            string username = update.Message?.From?.Username ?? update.CallbackQuery?.From.Username ?? "unknown";

            using var db = new AppDbContext();
            var user = await db.Users
                .Include(u => u.Permissions)
                .FirstOrDefaultAsync(u => u.TelegramUserId == userId, ct);

            if (user == null || !user.IsActive)
            {
                _logger.Log("Warning", "Security", $"Отказ в доступе. Неавторизованный пользователь: @{username} (ID: {userId})");
                if (update.Message != null)
                {
                    await bot.SendMessage(update.Message.Chat.Id, "⛔ У вас нет доступа к этому медиа-серверу.", cancellationToken: ct);
                }
                return;
            }

            if (update.Type == UpdateType.Message && update.Message != null)
            {
                await HandleTextMessageAsync(bot, update.Message, user, db, ct);
            }
            else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
            {
                await HandleCallbackQueryAsync(bot, update.CallbackQuery, user, db, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.Log("Error", "Telegram", $"Ошибка обработки события: {ex.Message}", details: ex.StackTrace);
        }
    }

    private async Task HandleTextMessageAsync(ITelegramBotClient bot, Message message, Domain.Entities.User user, AppDbContext db, CancellationToken ct)
    {
        var text = message.Text?.Trim() ?? "";

        if (text.Equals("🎲 Случайное фото", StringComparison.OrdinalIgnoreCase) || text.Equals("/random", StringComparison.OrdinalIgnoreCase))
        {
            await SendRandomPhotoAsync(bot, message.Chat.Id, user, db, sourceId: null, folder: null, ct);
        }
        else if (text.Equals("📁 Проводник архива", StringComparison.OrdinalIgnoreCase) || text.Equals("/browse", StringComparison.OrdinalIgnoreCase))
        {
            await SendSourcesMenuAsync(bot, message.Chat.Id, null, user, db, ct);
        }
        else if (text.Equals("👥 Люди (Семья / Друзья)", StringComparison.OrdinalIgnoreCase) || text.Equals("👥 Члены семьи", StringComparison.OrdinalIgnoreCase) || text.Equals("/people", StringComparison.OrdinalIgnoreCase))
        {
            await SendPeopleMenuAsync(bot, message.Chat.Id, null, user, db, ct);
        }
        else if (text.StartsWith("/search", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/find", StringComparison.OrdinalIgnoreCase))
        {
            var query = text.Contains(' ') ? text.Substring(text.IndexOf(' ') + 1).Trim() : "";
            if (string.IsNullOrEmpty(query))
            {
                await bot.SendMessage(message.Chat.Id, "🔍 <b>Как искать по архиву:</b>\nНапишите запрос после команды, например:\n<code>/search Илья на лыжах</code>\n<code>/search друзья на море</code>", parseMode: ParseMode.Html, cancellationToken: ct);
            }
            else
            {
                await HandleSearchAsync(bot, message.Chat.Id, query, user, db, ct);
            }
        }
        else if (text.Equals("/start", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Log("Info", "Telegram", $"Пользователь @{user.Username ?? user.DisplayName} открыл меню.", user.DisplayName);
            await SendMainMenuAsync(bot, message.Chat.Id, user, ct);
        }
        else
        {
            await HandleSearchAsync(bot, message.Chat.Id, text, user, db, ct);
        }
    }

    private async Task SendPeopleMenuAsync(ITelegramBotClient bot, long chatId, int? editMessageId, Domain.Entities.User user, AppDbContext db, CancellationToken ct)
    {
        var people = await db.People
            .Include(p => p.Faces)
            .OrderBy(p => p.Category)
            .ThenBy(p => p.Name)
            .ToListAsync(ct);

        var buttons = new List<InlineKeyboardButton[]>();

        // 1. Семья
        var family = people.Where(p => string.Equals(p.Category, "Семья", StringComparison.OrdinalIgnoreCase)).ToList();
        if (family.Any())
        {
            for (int i = 0; i < family.Count; i += 2)
            {
                var row = new List<InlineKeyboardButton>();
                row.Add(InlineKeyboardButton.WithCallbackData($"👨‍👩‍👧 {family[i].Name} ({family[i].Faces.Count})", $"view_person_{family[i].Id}"));
                if (i + 1 < family.Count)
                {
                    row.Add(InlineKeyboardButton.WithCallbackData($"👨‍👩‍👧 {family[i + 1].Name} ({family[i + 1].Faces.Count})", $"view_person_{family[i + 1].Id}"));
                }
                buttons.Add(row.ToArray());
            }
        }

        // 2. Друзья
        var friends = people.Where(p => string.Equals(p.Category, "Друзья", StringComparison.OrdinalIgnoreCase)).ToList();
        if (friends.Any())
        {
            for (int i = 0; i < friends.Count; i += 2)
            {
                var row = new List<InlineKeyboardButton>();
                row.Add(InlineKeyboardButton.WithCallbackData($"🎉 {friends[i].Name} ({friends[i].Faces.Count})", $"view_person_{friends[i].Id}"));
                if (i + 1 < friends.Count)
                {
                    row.Add(InlineKeyboardButton.WithCallbackData($"🎉 {friends[i + 1].Name} ({friends[i + 1].Faces.Count})", $"view_person_{friends[i + 1].Id}"));
                }
                buttons.Add(row.ToArray());
            }
        }

        // 3. Другие / Коллеги
        var others = people.Where(p => !string.Equals(p.Category, "Семья", StringComparison.OrdinalIgnoreCase) && !string.Equals(p.Category, "Друзья", StringComparison.OrdinalIgnoreCase)).ToList();
        if (others.Any())
        {
            for (int i = 0; i < others.Count; i += 2)
            {
                var row = new List<InlineKeyboardButton>();
                row.Add(InlineKeyboardButton.WithCallbackData($"👤 {others[i].Name} ({others[i].Faces.Count})", $"view_person_{others[i].Id}"));
                if (i + 1 < others.Count)
                {
                    row.Add(InlineKeyboardButton.WithCallbackData($"👤 {others[i + 1].Name} ({others[i + 1].Faces.Count})", $"view_person_{others[i + 1].Id}"));
                }
                buttons.Add(row.ToArray());
            }
        }

        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "nav_main_menu") });

        string messageText = people.Any()
            ? "👥 <b>Выберите человека для просмотра фотоальбома:</b>\n<i>(Разделено по категориям: Семья, Друзья, Коллеги)</i>"
            : "В базе пока нет добавленных людей.\nДобавьте их в приложении на ПК во вкладке «Люди (Семья, Друзья)».";

        if (editMessageId.HasValue)
        {
            await SafeEditMessageTextAsync(bot, chatId, editMessageId.Value, messageText, new InlineKeyboardMarkup(buttons), ct);
        }
        else
        {
            await bot.SendMessage(
                chatId,
                messageText,
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: ct
            );
        }
    }

    private async Task HandleSearchAsync(ITelegramBotClient bot, long chatId, string query, Domain.Entities.User user, AppDbContext db, CancellationToken ct)
    {
        _logger.Log("Info", "Telegram", $"Умный поиск ИИ/Лица: '{query}' для @{user.DisplayName}", user.DisplayName);

        var terms = query.ToLowerInvariant()
            .Split(new[] { ' ', ',', '.', ';', '!' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1)
            .ToList();

        if (!terms.Any())
        {
            await bot.SendMessage(chatId, "Введите хотя бы одно слово для поиска (например: <i>Илья, друзья, море</i>).", parseMode: ParseMode.Html, cancellationToken: ct);
            return;
        }

        var allItems = await db.MediaItems
            .Include(m => m.StorageSource)
            .Include(m => m.Faces)
            .ThenInclude(f => f.Person)
            .Where(m => !m.IsDeleted && m.StorageSource.IsEnabled)
            .ToListAsync(ct);

        var scoredItems = allItems
            .Where(m => !IsIgnoredPath(m.RelativePath))
            .Where(m => _authService.CanAccessPath(user, m.StorageSourceId, m.RelativePath))
            .Select(m =>
            {
                int score = 0;
                var desc = (m.AIDescription ?? "").ToLowerInvariant();
                var tags = (m.AITags ?? "").ToLowerInvariant();
                var name = m.FileName.ToLowerInvariant();
                var path = m.RelativePath.ToLowerInvariant();
                var people = m.Faces
                    .Where(f => f.Person != null)
                    .Select(f => f.Person!)
                    .ToList();

                foreach (var term in terms)
                {
                    if (people.Any(p => p.Name.ToLowerInvariant().Contains(term))) score += 10; // Имя человека
                    if (people.Any(p => p.Category.ToLowerInvariant().Contains(term))) score += 8; // Категория (семья / друзья)
                    if (tags.Contains(term)) score += 5;
                    if (desc.Contains(term)) score += 3;
                    if (name.Contains(term)) score += 2;
                    if (path.Contains(term)) score += 1;
                }

                return new { Item = m, Score = score };
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.FileCreatedAt)
            .Select(x => x.Item)
            .ToList();

        if (!scoredItems.Any())
        {
            await bot.SendMessage(
                chatId,
                $"🔍 По запросу «<b>{query}</b>» ничего не найдено.\n\n" +
                $"💡 <i>Совет: Попробуйте поискать по имени человека (Илья, Анна), группе (семья, друзья) или по сюжету (море, горы, праздник, авто).</i>",
                parseMode: ParseMode.Html,
                replyMarkup: GetPersistentMenuKeyboard(),
                cancellationToken: ct
            );
            return;
        }

        var state = new UserBrowseState
        {
            SourceId = scoredItems.First().StorageSourceId,
            FolderPath = "",
            DirPage = 0,
            FilePage = 0,
            CachedFiles = scoredItems,
            IsSearchMode = true,
            SearchQuery = query
        };

        _userSessions[user.TelegramUserId] = state;
        await SendPhotoGalleryPageAsync(bot, chatId, user, db, state, ct);
    }

    private async Task SendMainMenuAsync(ITelegramBotClient bot, long chatId, Domain.Entities.User user, CancellationToken ct)
    {
        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("📁 Проводник архива", "nav_sources") },
            new[] { InlineKeyboardButton.WithCallbackData("👥 Люди (Семья и Друзья)", "nav_people") },
            new[] { InlineKeyboardButton.WithCallbackData("🎲 Случайное фото", "random_photo") }
        });

        await bot.SendMessage(
            chatId,
            $"👋 Здравствуйте, <b>{user.DisplayName}</b>!\n\n" +
            $"Добро пожаловать в архив медиафайлов.\n" +
            $"💡 <i>Вы можете написать имя человека («Илья»), категорию («друзья на море») или сюжет («закат в горах»), и я найду нужные фото!</i>",
            parseMode: ParseMode.Html,
            replyMarkup: inlineKeyboard,
            cancellationToken: ct
        );

        await bot.SendMessage(
            chatId,
            "👇 Кнопки быстрого доступа всегда под рукой:",
            replyMarkup: GetPersistentMenuKeyboard(),
            cancellationToken: ct
        );
    }

    private async Task SendSourcesMenuAsync(ITelegramBotClient bot, long chatId, int? editMessageId, Domain.Entities.User user, AppDbContext db, CancellationToken ct)
    {
        var sources = await db.StorageSources.Where(s => s.IsEnabled).ToListAsync(ct);
        var buttons = new List<InlineKeyboardButton[]>();

        foreach (var src in sources)
        {
            if (_authService.HasAnyAccessToSource(user, src.Id))
            {
                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData($"📦 {src.Name}", $"select_src_{src.Id}") });
            }
        }

        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🎲 Случайное фото (все диски)", "random_photo") });

        string messageText = buttons.Count > 1 
            ? "📂 <b>Выберите источник медиафайлов:</b>" 
            : "Вам пока не назначен доступ ни к одной папке.";

        if (editMessageId.HasValue)
        {
            await SafeEditMessageTextAsync(bot, chatId, editMessageId.Value, messageText, new InlineKeyboardMarkup(buttons), ct);
        }
        else
        {
            await bot.SendMessage(
                chatId,
                messageText,
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: ct
            );
        }
    }

    private async Task HandleCallbackQueryAsync(ITelegramBotClient bot, CallbackQuery query, Domain.Entities.User user, AppDbContext db, CancellationToken ct)
    {
        await bot.AnswerCallbackQuery(query.Id, cancellationToken: ct);
        var data = query.Data ?? "";
        long userId = query.From.Id;
        long chatId = query.Message!.Chat.Id;
        int messageId = query.Message.MessageId;

        if (data == "nav_main_menu")
        {
            await SendMainMenuAsync(bot, chatId, user, ct);
        }
        else if (data == "nav_people")
        {
            await SendPeopleMenuAsync(bot, chatId, messageId, user, db, ct);
        }
        else if (data.StartsWith("view_person_"))
        {
            if (int.TryParse(data.Substring("view_person_".Length), out int personId))
            {
                var person = await db.People.FindAsync(new object[] { personId }, ct);
                if (person != null)
                {
                    var itemsWithPerson = await db.PersonFaces
                        .Include(f => f.MediaItem)
                        .ThenInclude(m => m.StorageSource)
                        .Where(f => f.PersonId == personId && !f.MediaItem.IsDeleted && f.MediaItem.StorageSource.IsEnabled)
                        .Select(f => f.MediaItem)
                        .Distinct()
                        .OrderByDescending(m => m.FileCreatedAt)
                        .ToListAsync(ct);

                    var state = new UserBrowseState
                    {
                        SourceId = itemsWithPerson.FirstOrDefault()?.StorageSourceId ?? 0,
                        FolderPath = "",
                        DirPage = 0,
                        FilePage = 0,
                        CachedFiles = itemsWithPerson,
                        IsSearchMode = true,
                        SearchQuery = $"{person.Name} ({person.Category})"
                    };

                    _userSessions[userId] = state;
                    await SendPhotoGalleryPageAsync(bot, chatId, user, db, state, ct);
                }
            }
        }
        else if (data == "random_photo")
        {
            await SendRandomPhotoAsync(bot, chatId, user, db, sourceId: null, folder: null, ct);
        }
        else if (data == "nav_sources")
        {
            await SendSourcesMenuAsync(bot, chatId, messageId, user, db, ct);
        }
        else if (data.StartsWith("select_src_"))
        {
            if (int.TryParse(data.Substring("select_src_".Length), out int sourceId))
            {
                var state = new UserBrowseState
                {
                    SourceId = sourceId,
                    FolderPath = "",
                    DirPage = 0,
                    FilePage = 0,
                    IsSearchMode = false
                };
                _userSessions[userId] = state;
                await OpenFolderAsync(bot, chatId, messageId, user, db, state, ct);
            }
        }
        else if (data == "nav_root")
        {
            if (_userSessions.TryGetValue(userId, out var state))
            {
                state.FolderPath = "";
                state.DirPage = 0;
                state.FilePage = 0;
                state.IsSearchMode = false;
                await OpenFolderAsync(bot, chatId, messageId, user, db, state, ct);
            }
        }
        else if (data == "nav_up")
        {
            if (_userSessions.TryGetValue(userId, out var state))
            {
                if (state.IsSearchMode)
                {
                    await SendSourcesMenuAsync(bot, chatId, messageId, user, db, ct);
                }
                else if (!string.IsNullOrEmpty(state.FolderPath))
                {
                    var parent = Path.GetDirectoryName(state.FolderPath)?.Replace('\\', '/') ?? "";
                    state.FolderPath = parent;
                    state.DirPage = 0;
                    state.FilePage = 0;
                    await OpenFolderAsync(bot, chatId, messageId, user, db, state, ct);
                }
                else
                {
                    await SendSourcesMenuAsync(bot, chatId, messageId, user, db, ct);
                }
            }
        }
        else if (data.StartsWith("enter_dir_"))
        {
            if (_userSessions.TryGetValue(userId, out var state) && int.TryParse(data.Substring("enter_dir_".Length), out int dirIndex))
            {
                if (dirIndex >= 0 && dirIndex < state.CachedSubDirs.Count)
                {
                    state.FolderPath = state.CachedSubDirs[dirIndex];
                    state.DirPage = 0;
                    state.FilePage = 0;
                    state.IsSearchMode = false;
                    await OpenFolderAsync(bot, chatId, null, user, db, state, ct);
                }
            }
        }
        else if (data.StartsWith("dir_page_"))
        {
            if (_userSessions.TryGetValue(userId, out var state) && int.TryParse(data.Substring("dir_page_".Length), out int newDirPage))
            {
                state.DirPage = newDirPage;
                await RenderBrowseViewAsync(bot, chatId, messageId, user, db, state, ct);
            }
        }
        else if (data.StartsWith("file_page_"))
        {
            if (_userSessions.TryGetValue(userId, out var state) && int.TryParse(data.Substring("file_page_".Length), out int newFilePage))
            {
                state.FilePage = newFilePage;
                await SendPhotoGalleryPageAsync(bot, chatId, user, db, state, ct);
            }
        }
        else if (data == "rand_current_folder")
        {
            if (_userSessions.TryGetValue(userId, out var state))
            {
                await SendRandomPhotoAsync(bot, chatId, user, db, state.SourceId, state.FolderPath, ct);
            }
        }
        else if (data.StartsWith("open_parent_folder_"))
        {
            if (long.TryParse(data.Substring("open_parent_folder_".Length), out long mediaId))
            {
                var item = await db.MediaItems.FindAsync(new object[] { mediaId }, ct);
                if (item != null)
                {
                    var parentFolder = Path.GetDirectoryName(item.RelativePath)?.Replace('\\', '/') ?? "";
                    var state = new UserBrowseState
                    {
                        SourceId = item.StorageSourceId,
                        FolderPath = parentFolder,
                        DirPage = 0,
                        FilePage = 0,
                        IsSearchMode = false
                    };
                    _userSessions[userId] = state;
                    await OpenFolderAsync(bot, chatId, null, user, db, state, ct);
                }
            }
        }
        else if (data.StartsWith("orig_file_"))
        {
            if (long.TryParse(data.Substring("orig_file_".Length), out long mediaId))
            {
                var item = await db.MediaItems.Include(m => m.StorageSource).FirstOrDefaultAsync(m => m.Id == mediaId, ct);
                if (item != null && !item.IsDeleted && _authService.CanAccessPath(user, item.StorageSourceId, item.RelativePath))
                {
                    var fullPath = Path.Combine(item.StorageSource.RootPath, item.RelativePath.Replace('/', '\\'));
                    if (File.Exists(fullPath))
                    {
                        var ext = item.FileExtension.ToLowerInvariant();
                        var isVideo = VideoExtensions.Contains(ext);

                        if (item.FileSize > MaxTelegramUploadBytes)
                        {
                            await bot.SendMessage(
                                chatId,
                                $"⚠️ <b>Файл слишком большой для отправки:</b> {item.FileName}\n" +
                                $"💾 Размер: <b>{item.FileSize / (1024 * 1024.0):F1} МБ</b> (лимит Telegram Bot API — 50 МБ).\n" +
                                $"<i>Рекомендуется открыть этот файл напрямую в локальной сети NAS.</i>",
                                parseMode: ParseMode.Html,
                                cancellationToken: ct
                            );
                            return;
                        }

                        if (isVideo)
                        {
                            _logger.Log("Info", "Telegram", $"Отправка видео: {item.FileName} для @{user.DisplayName}", user.DisplayName);
                            await using var stream = File.OpenRead(fullPath);
                            await bot.SendVideo(
                                chatId,
                                InputFile.FromStream(stream, item.FileName),
                                caption: $"🎬 <b>{item.FileName}</b> ({item.FileSize / (1024 * 1024.0):F1} МБ)",
                                parseMode: ParseMode.Html,
                                supportsStreaming: true,
                                cancellationToken: ct
                            );
                        }
                        else
                        {
                            _logger.Log("Info", "Telegram", $"Отправка оригинала фото: {item.FileName} для @{user.DisplayName}", user.DisplayName);
                            await using var stream = File.OpenRead(fullPath);
                            await bot.SendDocument(
                                chatId,
                                InputFile.FromStream(stream, item.FileName),
                                caption: $"📥 <b>{item.FileName}</b>\n💾 Размер оригинала: {item.FileSize / (1024 * 1024.0):F2} МБ",
                                parseMode: ParseMode.Html,
                                cancellationToken: ct
                            );
                        }
                    }
                }
            }
        }
    }

    private async Task OpenFolderAsync(ITelegramBotClient bot, long chatId, int? messageId, Domain.Entities.User user, AppDbContext db, UserBrowseState state, CancellationToken ct)
    {
        var source = await db.StorageSources.FindAsync(new object[] { state.SourceId }, ct);
        if (source == null || !Directory.Exists(source.RootPath))
        {
            await bot.SendMessage(chatId, "Хранилище временно недоступно.", cancellationToken: ct);
            return;
        }

        var fullTargetDir = string.IsNullOrEmpty(state.FolderPath)
            ? source.RootPath
            : Path.Combine(source.RootPath, state.FolderPath.Replace('/', '\\'));

        if (!Directory.Exists(fullTargetDir))
        {
            state.FolderPath = "";
            fullTargetDir = source.RootPath;
        }

        state.CachedSubDirs = Directory.GetDirectories(fullTargetDir)
            .Select(d => Path.GetRelativePath(source.RootPath, d).Replace('\\', '/'))
            .Where(rel => !IsIgnoredPath(rel) && _authService.CanViewFolder(user, state.SourceId, rel))
            .OrderBy(d => d)
            .ToList();

        var normalizedFolder = state.FolderPath.Trim('/');
        var allFolderFiles = await db.MediaItems
            .Where(m => m.StorageSourceId == state.SourceId && !m.IsDeleted)
            .ToListAsync(ct);

        state.CachedFiles = allFolderFiles
            .Where(m =>
            {
                if (IsIgnoredPath(m.RelativePath)) return false;
                var fileDir = Path.GetDirectoryName(m.RelativePath)?.Replace('\\', '/').Trim('/') ?? "";
                return string.Equals(fileDir, normalizedFolder, StringComparison.OrdinalIgnoreCase)
                       && _authService.CanAccessPath(user, state.SourceId, m.RelativePath);
            })
            .OrderBy(m => m.FileName)
            .ToList();

        var hasMedia = state.CachedFiles.Any(f => ImageExtensions.Contains(f.FileExtension) || VideoExtensions.Contains(f.FileExtension));

        if (hasMedia)
        {
            await SendPhotoGalleryPageAsync(bot, chatId, user, db, state, ct);
        }
        else
        {
            await RenderBrowseViewAsync(bot, chatId, messageId, user, db, state, ct);
        }
    }

    private async Task RenderBrowseViewAsync(ITelegramBotClient bot, long chatId, int? messageId, Domain.Entities.User user, AppDbContext db, UserBrowseState state, CancellationToken ct)
    {
        var source = await db.StorageSources.FindAsync(new object[] { state.SourceId }, ct);
        if (source == null) return;

        var keyboardRows = new List<InlineKeyboardButton[]>();

        int totalDirs = state.CachedSubDirs.Count;
        int totalDirPages = (int)Math.Ceiling(totalDirs / (double)DirPageSize);
        if (state.DirPage >= totalDirPages && totalDirPages > 0) state.DirPage = totalDirPages - 1;
        if (state.DirPage < 0) state.DirPage = 0;

        var currentDirSlice = state.CachedSubDirs.Skip(state.DirPage * DirPageSize).Take(DirPageSize).ToList();
        for (int i = 0; i < currentDirSlice.Count; i += 2)
        {
            var row = new List<InlineKeyboardButton>();
            int idx1 = state.DirPage * DirPageSize + i;
            var dirName1 = Path.GetFileName(currentDirSlice[i]);
            row.Add(InlineKeyboardButton.WithCallbackData($"📁 {dirName1}", $"enter_dir_{idx1}"));

            if (i + 1 < currentDirSlice.Count)
            {
                int idx2 = state.DirPage * DirPageSize + i + 1;
                var dirName2 = Path.GetFileName(currentDirSlice[i + 1]);
                row.Add(InlineKeyboardButton.WithCallbackData($"📁 {dirName2}", $"enter_dir_{idx2}"));
            }
            keyboardRows.Add(row.ToArray());
        }

        if (totalDirPages > 1)
        {
            var dirNav = new List<InlineKeyboardButton>();
            if (state.DirPage > 0)
                dirNav.Add(InlineKeyboardButton.WithCallbackData("⬅️ Папки", $"dir_page_{state.DirPage - 1}"));
            dirNav.Add(InlineKeyboardButton.WithCallbackData($"Папки {state.DirPage + 1}/{totalDirPages}", $"dir_page_{state.DirPage}"));
            if (state.DirPage < totalDirPages - 1)
                dirNav.Add(InlineKeyboardButton.WithCallbackData("Папки ➡️", $"dir_page_{state.DirPage + 1}"));
            keyboardRows.Add(dirNav.ToArray());
        }

        if (!string.IsNullOrEmpty(state.FolderPath))
        {
            keyboardRows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("⬆️ Вверх", "nav_up"),
                InlineKeyboardButton.WithCallbackData("🏠 В корень", "nav_root")
            });
        }
        else
        {
            keyboardRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ К источникам", "nav_sources") });
        }

        var folderTitle = string.IsNullOrEmpty(state.FolderPath) ? source.Name : Path.GetFileName(state.FolderPath);
        var messageText = $"📁 <b>Папка:</b> {folderTitle}\n" +
                          $"📂 <b>Подпапок:</b> {totalDirs} (по {DirPageSize} на стр.)";

        if (messageId.HasValue)
        {
            await SafeEditMessageTextAsync(bot, chatId, messageId.Value, messageText, new InlineKeyboardMarkup(keyboardRows), ct);
        }
        else
        {
            await bot.SendMessage(
                chatId,
                messageText,
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(keyboardRows),
                cancellationToken: ct
            );
        }
    }

    private async Task SendPhotoGalleryPageAsync(ITelegramBotClient bot, long chatId, Domain.Entities.User user, AppDbContext db, UserBrowseState state, CancellationToken ct)
    {
        var allMedia = state.CachedFiles;
        if (!allMedia.Any())
        {
            await bot.SendMessage(chatId, "В этой папке/выборке нет файлов.", replyMarkup: GetPersistentMenuKeyboard(), cancellationToken: ct);
            return;
        }

        int totalItems = allMedia.Count;
        int totalPages = (int)Math.Ceiling(totalItems / (double)FilePageSize);
        if (state.FilePage >= totalPages && totalPages > 0) state.FilePage = totalPages - 1;
        if (state.FilePage < 0) state.FilePage = 0;

        var pageMedia = allMedia.Skip(state.FilePage * FilePageSize).Take(FilePageSize).ToList();

        using var tempDb = new AppDbContext();
        var sources = await tempDb.StorageSources.ToDictionaryAsync(s => s.Id, ct);

        var thumbTasks = pageMedia.Select(async item =>
        {
            if (sources.TryGetValue(item.StorageSourceId, out var src))
            {
                var origPath = Path.Combine(src.RootPath, item.RelativePath.Replace('/', '\\'));
                if (File.Exists(origPath))
                {
                    var thumbPath = await _thumbnailService.GetOrCreateThumbnailAsync(origPath, 800, 800);
                    return (Item: item, ThumbPath: thumbPath);
                }
            }
            return (Item: item, ThumbPath: string.Empty);
        }).ToList();

        var readyThumbs = (await Task.WhenAll(thumbTasks))
            .Where(t => !string.IsNullOrEmpty(t.ThumbPath))
            .ToList();

        foreach (var entry in readyThumbs)
        {
            try
            {
                var item = entry.Item;
                var ext = item.FileExtension.ToLowerInvariant();
                var isVideo = VideoExtensions.Contains(ext);

                var buttonText = isVideo
                    ? $"🎬 Смотреть видео ({item.FileSize / (1024 * 1024.0):F1} МБ)"
                    : $"🔍 Полное качество ({item.FileSize / (1024 * 1024.0):F1} МБ)";

                var singleMediaKeyboard = new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData(buttonText, $"orig_file_{item.Id}")
                    }
                });

                string caption;
                if (isVideo)
                {
                    caption = $"🎬 <b>{item.FileName}</b> <i>(Видео)</i>";
                }
                else
                {
                    caption = $"🖼 <b>{item.FileName}</b>";
                    if (!string.IsNullOrEmpty(item.AIDescription))
                    {
                        caption += $"\n💡 <i>{item.AIDescription}</i>";
                    }
                    if (!string.IsNullOrEmpty(item.AITags))
                    {
                        caption += $"\n🏷 <code>{item.AITags}</code>";
                    }
                }

                await using var stream = File.OpenRead(entry.ThumbPath);
                await bot.SendPhoto(
                    chatId,
                    InputFile.FromStream(stream, item.FileName),
                    caption: caption,
                    parseMode: ParseMode.Html,
                    replyMarkup: singleMediaKeyboard,
                    cancellationToken: ct
                );
            }
            catch (Exception ex)
            {
                _logger.Log("Warning", "Telegram", $"Ошибка отправки миниатюры '{entry.Item.FileName}': {ex.Message}");
            }
        }

        var navButtons = new List<InlineKeyboardButton[]>();

        if (!state.IsSearchMode && state.CachedSubDirs.Any())
        {
            for (int i = 0; i < Math.Min(state.CachedSubDirs.Count, 6); i += 2)
            {
                var row = new List<InlineKeyboardButton>();
                row.Add(InlineKeyboardButton.WithCallbackData($"📁 {Path.GetFileName(state.CachedSubDirs[i])}", $"enter_dir_{i}"));
                if (i + 1 < state.CachedSubDirs.Count)
                {
                    row.Add(InlineKeyboardButton.WithCallbackData($"📁 {Path.GetFileName(state.CachedSubDirs[i + 1])}", $"enter_dir_{i + 1}"));
                }
                navButtons.Add(row.ToArray());
            }
        }

        var pageRow = new List<InlineKeyboardButton>();
        if (state.FilePage > 0)
        {
            pageRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ Предыдущие 10", $"file_page_{state.FilePage - 1}"));
        }
        pageRow.Add(InlineKeyboardButton.WithCallbackData($"Стр. {state.FilePage + 1}/{totalPages}", $"file_page_{state.FilePage}"));
        if (state.FilePage < totalPages - 1)
        {
            pageRow.Add(InlineKeyboardButton.WithCallbackData("Следующие 10 ➡️", $"file_page_{state.FilePage + 1}"));
        }
        navButtons.Add(pageRow.ToArray());

        if (state.IsSearchMode)
        {
            navButtons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("📁 К папкам архива", "nav_sources"),
                InlineKeyboardButton.WithCallbackData("👥 Люди (Семья/Друзья)", "nav_people")
            });
        }
        else
        {
            navButtons.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("⬆️ Вверх", "nav_up"),
                InlineKeyboardButton.WithCallbackData("🎲 Случайное фото", "rand_current_folder")
            });
        }

        navButtons.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "nav_main_menu")
        });

        int startItemIndex = state.FilePage * FilePageSize + 1;
        int endItemIndex = state.FilePage * FilePageSize + readyThumbs.Count;
        
        string captionText;
        if (state.IsSearchMode)
        {
            captionText = $"🔍 <b>Результаты:</b> «<i>{state.SearchQuery}</i>»\n" +
                          $"Показаны {startItemIndex}–{endItemIndex} из {totalItems} (Стр. {state.FilePage + 1} из {totalPages})";
        }
        else
        {
            var folderTitle = string.IsNullOrEmpty(state.FolderPath) ? "Архив" : Path.GetFileName(state.FolderPath);
            captionText = $"📁 <b>{folderTitle}</b> | Показаны {startItemIndex}–{endItemIndex} из {totalItems}\n" +
                          $"<i>(Страница {state.FilePage + 1} из {totalPages} по {FilePageSize} шт)</i>";
        }

        await bot.SendMessage(
            chatId,
            captionText,
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(navButtons),
            cancellationToken: ct
        );
    }

    private async Task SendSingleMediaFileAsync(ITelegramBotClient bot, long chatId, Domain.Entities.User user, MediaItem item, CancellationToken ct)
    {
        using var db = new AppDbContext();
        var source = await db.StorageSources.FindAsync(new object[] { item.StorageSourceId }, ct);
        if (source == null) return;

        var fullPath = Path.Combine(source.RootPath, item.RelativePath.Replace('/', '\\'));
        if (!File.Exists(fullPath)) return;

        var ext = item.FileExtension.ToLowerInvariant();

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData($"🔍 Полное качество ({item.FileSize / (1024 * 1024.0):F1} МБ)", $"orig_file_{item.Id}"),
                InlineKeyboardButton.WithCallbackData("🎲 Еще случайное", "rand_current_folder")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("📁 Открыть эту папку", $"open_parent_folder_{item.Id}"),
                InlineKeyboardButton.WithCallbackData("🏠 Меню", "nav_main_menu")
            }
        });

        if (ImageExtensions.Contains(ext))
        {
            _logger.Log("Info", "Telegram", $"Отправка фото: {item.FileName} для @{user.DisplayName}", user.DisplayName);
            var thumbPath = await _thumbnailService.GetOrCreateThumbnailAsync(fullPath, 1280, 1280);
            await using var stream = File.OpenRead(thumbPath);

            var caption = $"🖼 <b>{item.FileName}</b>\n📁 <code>{item.RelativePath}</code>\n💾 Размер: {item.FileSize / (1024 * 1024.0):F1} МБ";
            if (!string.IsNullOrEmpty(item.AIDescription))
            {
                caption += $"\n\n💡 <b>ИИ:</b> <i>{item.AIDescription}</i>";
            }

            await bot.SendPhoto(
                chatId,
                InputFile.FromStream(stream, item.FileName),
                caption: caption,
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct
            );
        }
        else if (VideoExtensions.Contains(ext))
        {
            if (item.FileSize > MaxTelegramUploadBytes)
            {
                _logger.Log("Warning", "Telegram", $"Видео '{item.FileName}' ({item.FileSize / (1024 * 1024.0):F1} МБ) превышает лимит Telegram (50 МБ).", user.DisplayName);
                await bot.SendMessage(
                    chatId,
                    $"⚠️ <b>Видео слишком большое для отправки в Telegram:</b>\n" +
                    $"🎬 <code>{item.FileName}</code>\n" +
                    $"💾 Размер: <b>{item.FileSize / (1024 * 1024.0):F1} МБ</b> (лимит Telegram Bot API — 50 МБ).\n" +
                    $"<i>Рекомендуется просмотреть это видео напрямую с домашнего NAS.</i>",
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard,
                    cancellationToken: ct
                );
                return;
            }

            _logger.Log("Info", "Telegram", $"Отправка видео: {item.FileName} для @{user.DisplayName}", user.DisplayName);
            await using var stream = File.OpenRead(fullPath);
            await bot.SendVideo(
                chatId,
                InputFile.FromStream(stream, item.FileName),
                caption: $"🎬 <b>{item.FileName}</b> ({item.FileSize / (1024 * 1024.0):F1} МБ)",
                parseMode: ParseMode.Html,
                supportsStreaming: true,
                replyMarkup: keyboard,
                cancellationToken: ct
            );
        }
    }

    private async Task SendRandomPhotoAsync(ITelegramBotClient bot, long chatId, Domain.Entities.User user, AppDbContext db, int? sourceId, string? folder, CancellationToken ct)
    {
        var query = db.MediaItems
            .Include(m => m.StorageSource)
            .Where(m => !m.IsDeleted && m.StorageSource.IsEnabled);

        if (sourceId.HasValue)
        {
            query = query.Where(m => m.StorageSourceId == sourceId.Value);
        }

        var allItems = await query.ToListAsync(ct);

        var candidatePhotos = allItems
            .Where(m => !IsIgnoredPath(m.RelativePath))
            .Where(m => ImageExtensions.Contains(m.FileExtension.ToLowerInvariant()))
            .Where(m => string.IsNullOrEmpty(folder) || m.RelativePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .Where(m => _authService.CanAccessPath(user, m.StorageSourceId, m.RelativePath))
            .ToList();

        if (candidatePhotos.Any())
        {
            var randomItem = candidatePhotos[Random.Shared.Next(candidatePhotos.Count)];
            await SendSingleMediaFileAsync(bot, chatId, user, randomItem, ct);
            return;
        }

        await bot.SendMessage(chatId, "В этой папке/источнике не найдено доступных фотографий.", replyMarkup: GetPersistentMenuKeyboard(), cancellationToken: ct);
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        var errorMsg = exception is ApiRequestException apiEx
            ? $"Telegram API Error [{apiEx.ErrorCode}]: {apiEx.Message}"
            : exception.Message;

        if (!errorMsg.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Log("Error", "Telegram", errorMsg);
        }
        return Task.CompletedTask;
    }
}
