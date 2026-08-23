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
}

public class TelegramBotService
{
    private const long MaxTelegramUploadBytes = 49 * 1024 * 1024;
    private const int DirPageSize = 20;   // 20 папок на страницу
    private const int FilePageSize = 20;  // 20 миниатюр фото на страницу

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".heic"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm"
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
            new KeyboardButton[] { "📁 Проводник архива", "🎲 Случайное фото" }
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
        else
        {
            _logger.Log("Info", "Telegram", $"Пользователь @{user.Username ?? user.DisplayName} открыл меню.", user.DisplayName);
            await SendMainMenuAsync(bot, message.Chat.Id, user, ct);
        }
    }

    private async Task SendMainMenuAsync(ITelegramBotClient bot, long chatId, Domain.Entities.User user, CancellationToken ct)
    {
        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("📁 Проводник архива", "nav_sources") },
            new[] { InlineKeyboardButton.WithCallbackData("🎲 Случайное фото", "random_photo") }
        });

        await bot.SendMessage(
            chatId,
            $"👋 Здравствуйте, <b>{user.DisplayName}</b>!\n\nДобро пожаловать в семейный архив медиафайлов.\nВыберите раздел для просмотра:",
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
                    FilePage = 0
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
                await OpenFolderAsync(bot, chatId, messageId, user, db, state, ct);
            }
        }
        else if (data == "nav_up")
        {
            if (_userSessions.TryGetValue(userId, out var state))
            {
                if (!string.IsNullOrEmpty(state.FolderPath))
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
                        FilePage = 0
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
                    if (System.IO.File.Exists(fullPath))
                    {
                        if (item.FileSize > MaxTelegramUploadBytes)
                        {
                            await bot.SendMessage(
                                chatId,
                                $"⚠️ <b>Файл слишком большой:</b> {item.FileName}\n" +
                                $"💾 Размер: <b>{item.FileSize / (1024 * 1024.0):F1} МБ</b> (лимит Telegram Bot API — 50 МБ).",
                                parseMode: ParseMode.Html,
                                cancellationToken: ct
                            );
                            return;
                        }

                        _logger.Log("Info", "Telegram", $"Отправка оригинала: {item.FileName} для @{user.DisplayName}", user.DisplayName);
                        await using var stream = System.IO.File.OpenRead(fullPath);
                        await bot.SendDocument(
                            chatId,
                            InputFile.FromStream(stream, item.FileName),
                            caption: $"📥 Оригинал: {item.FileName} ({item.FileSize / (1024 * 1024.0):F1} МБ)",
                            cancellationToken: ct
                        );
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

        // Подпапки
        state.CachedSubDirs = Directory.GetDirectories(fullTargetDir)
            .Select(d => Path.GetRelativePath(source.RootPath, d).Replace('\\', '/'))
            .Where(rel => !IsIgnoredPath(rel) && _authService.CanViewFolder(user, state.SourceId, rel))
            .OrderBy(d => d)
            .ToList();

        // Файлы
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

        var hasPhotos = state.CachedFiles.Any(f => ImageExtensions.Contains(f.FileExtension));

        // Если в открытой папке есть фотографии -> СРАЗУ отправляем галерею миниатюр!
        if (hasPhotos)
        {
            await SendPhotoGalleryPageAsync(bot, chatId, user, db, state, ct);
        }
        else
        {
            // Если фото нет, а только папки (например корень архива) -> выводим список папок
            await RenderBrowseViewAsync(bot, chatId, messageId, user, db, state, ct);
        }
    }

    private async Task RenderBrowseViewAsync(ITelegramBotClient bot, long chatId, int? messageId, Domain.Entities.User user, AppDbContext db, UserBrowseState state, CancellationToken ct)
    {
        var source = await db.StorageSources.FindAsync(new object[] { state.SourceId }, ct);
        if (source == null) return;

        var keyboardRows = new List<InlineKeyboardButton[]>();

        // --- СПИСОК ПОДПАПОК (По 20 папок на страницу, по 2 кнопки в строке) ---
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

        // Навигация вверх / к источникам
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
        var source = await db.StorageSources.FindAsync(new object[] { state.SourceId }, ct);
        if (source == null) return;

        var allPhotos = state.CachedFiles
            .Where(f => ImageExtensions.Contains(f.FileExtension))
            .ToList();

        if (!allPhotos.Any())
        {
            await bot.SendMessage(chatId, "В этой папке нет фотографий.", replyMarkup: GetPersistentMenuKeyboard(), cancellationToken: ct);
            return;
        }

        int totalPhotos = allPhotos.Count;
        int totalPhotoPages = (int)Math.Ceiling(totalPhotos / (double)FilePageSize);
        if (state.FilePage >= totalPhotoPages && totalPhotoPages > 0) state.FilePage = totalPhotoPages - 1;
        if (state.FilePage < 0) state.FilePage = 0;

        var pagePhotos = allPhotos.Skip(state.FilePage * FilePageSize).Take(FilePageSize).ToList();

        _logger.Log("Info", "Telegram", $"Генерация галереи из {pagePhotos.Count} миниатюр (Стр. {state.FilePage + 1}/{totalPhotoPages}) для @{user.DisplayName}", user.DisplayName);

        var batch1 = pagePhotos.Take(10).ToList();
        var batch2 = pagePhotos.Skip(10).Take(10).ToList();

        await SendMediaBatchAsync(bot, chatId, source, batch1, ct);
        if (batch2.Any())
        {
            await SendMediaBatchAsync(bot, chatId, source, batch2, ct);
        }

        var navButtons = new List<InlineKeyboardButton[]>();

        if (state.CachedSubDirs.Any())
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
            pageRow.Add(InlineKeyboardButton.WithCallbackData("⬅️ 20 фото", $"file_page_{state.FilePage - 1}"));
        }
        pageRow.Add(InlineKeyboardButton.WithCallbackData($"Фото {state.FilePage + 1}/{totalPhotoPages}", $"file_page_{state.FilePage}"));
        if (state.FilePage < totalPhotoPages - 1)
        {
            pageRow.Add(InlineKeyboardButton.WithCallbackData("20 фото ➡️", $"file_page_{state.FilePage + 1}"));
        }
        navButtons.Add(pageRow.ToArray());

        navButtons.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("⬆️ Вверх", "nav_up"),
            InlineKeyboardButton.WithCallbackData("🎲 Случайное фото", "rand_current_folder")
        });

        navButtons.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("🏠 Главное меню", "nav_main_menu")
        });

        int startItemIndex = state.FilePage * FilePageSize + 1;
        int endItemIndex = state.FilePage * FilePageSize + pagePhotos.Count;
        var folderTitle = string.IsNullOrEmpty(state.FolderPath) ? source.Name : Path.GetFileName(state.FolderPath);
        var captionText = $"📁 <b>{folderTitle}</b> | Фото {startItemIndex}–{endItemIndex} из {totalPhotos}\n" +
                          $"<i>(Стр. {state.FilePage + 1} из {totalPhotoPages})</i>";

        await bot.SendMessage(
            chatId,
            captionText,
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(navButtons),
            cancellationToken: ct
        );
    }

    private async Task SendMediaBatchAsync(ITelegramBotClient bot, long chatId, StorageSource source, List<MediaItem> items, CancellationToken ct)
    {
        if (!items.Any()) return;

        var mediaList = new List<IAlbumInputMedia>();
        var openStreams = new List<FileStream>();

        try
        {
            foreach (var img in items)
            {
                var originalPath = Path.Combine(source.RootPath, img.RelativePath.Replace('/', '\\'));
                if (File.Exists(originalPath))
                {
                    var thumbPath = await _thumbnailService.GetOrCreateThumbnailAsync(originalPath, 600, 600);
                    var stream = File.OpenRead(thumbPath);
                    openStreams.Add(stream);

                    var inputPhoto = new InputMediaPhoto(InputFile.FromStream(stream, img.FileName))
                    {
                        Caption = img.FileName
                    };
                    mediaList.Add(inputPhoto);
                }
            }

            if (mediaList.Any())
            {
                await bot.SendMediaGroup(chatId, mediaList, cancellationToken: ct);
            }
        }
        finally
        {
            foreach (var stream in openStreams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    private async Task SendSingleMediaFileAsync(ITelegramBotClient bot, long chatId, Domain.Entities.User user, MediaItem item, CancellationToken ct)
    {
        using var db = new AppDbContext();
        var source = await db.StorageSources.FindAsync(new object[] { item.StorageSourceId }, ct);
        if (source == null) return;

        var fullPath = Path.Combine(source.RootPath, item.RelativePath.Replace('/', '\\'));
        if (!System.IO.File.Exists(fullPath)) return;

        var ext = item.FileExtension.ToLowerInvariant();

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("📥 Оригинал", $"orig_file_{item.Id}"),
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
            await using var stream = System.IO.File.OpenRead(thumbPath);
            await bot.SendPhoto(
                chatId,
                InputFile.FromStream(stream, item.FileName),
                caption: $"🖼 <b>{item.FileName}</b>\n📁 <code>{item.RelativePath}</code>\n💾 Размер: {item.FileSize / (1024 * 1024.0):F1} МБ",
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
            await using var stream = System.IO.File.OpenRead(fullPath);
            await bot.SendVideo(
                chatId,
                InputFile.FromStream(stream, item.FileName),
                caption: $"🎬 <b>{item.FileName}</b> ({item.FileSize / (1024 * 1024.0):F1} МБ)",
                parseMode: ParseMode.Html,
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
