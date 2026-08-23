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

public class TelegramBotService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".heic"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".webm"
    };

    private readonly IAuditLogger _logger;
    private readonly IAuthorizationService _authService;
    private readonly IThumbnailService _thumbnailService;
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

        _logger.Log("Info", "Telegram", "Telegram бот успешно запущен и слушает запросы.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _botClient = null;
        _logger.Log("Info", "Telegram", "Telegram бот остановлен.");
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
                    await bot.SendTextMessageAsync(update.Message.Chat.Id, "⛔ У вас нет доступа к этому медиа-серверу.", cancellationToken: ct);
                }
                return;
            }

            if (update.Type == UpdateType.Message && update.Message?.Text != null)
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

        if (text.StartsWith("/start"))
        {
            _logger.Log("Info", "Telegram", $"Пользователь @{user.Username ?? user.DisplayName} открыл главное меню.", user.DisplayName);
            var keyboard = new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithCallbackData("📁 Проводник архива", "nav_sources") },
                new[] { InlineKeyboardButton.WithCallbackData("🎲 Случайное фото", "random_photo") }
            });

            await bot.SendTextMessageAsync(
                message.Chat.Id,
                $"👋 Здравствуйте, {user.DisplayName}!\nДобро пожаловать в семейный архив. Выберите действие:",
                replyMarkup: keyboard,
                cancellationToken: ct
            );
        }
    }

    private async Task HandleCallbackQueryAsync(ITelegramBotClient bot, CallbackQuery query, Domain.Entities.User user, AppDbContext db, CancellationToken ct)
    {
        await bot.AnswerCallbackQueryAsync(query.Id, cancellationToken: ct);
        var data = query.Data ?? "";

        if (data == "random_photo")
        {
            await SendRandomPhotoAsync(bot, query.Message!.Chat.Id, user, db, sourceId: null, folder: null, ct);
        }
        else if (data.StartsWith("randfolder_"))
        {
            // Формат: randfolder_{sourceId}_{folder}
            var parts = data.Substring("randfolder_".Length).Split(new[] { '_' }, 2);
            if (parts.Length == 2 && int.TryParse(parts[0], out int sourceId))
            {
                string folder = parts[1];
                await SendRandomPhotoAsync(bot, query.Message!.Chat.Id, user, db, sourceId, folder, ct);
            }
        }
        else if (data == "nav_sources")
        {
            var sources = await db.StorageSources
                .Where(s => s.IsEnabled)
                .ToListAsync(ct);

            var buttons = new List<InlineKeyboardButton[]>();
            foreach (var src in sources)
            {
                if (_authService.CanAccessPath(user, src.Id, "*"))
                {
                    buttons.Add(new[] { InlineKeyboardButton.WithCallbackData($"📦 {src.Name}", $"browse_{src.Id}__0") });
                }
            }

            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🎲 Случайное фото (все источники)", "random_photo") });

            await bot.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "📂 Выберите источник медиафайлов:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: ct
            );
        }
        else if (data.StartsWith("sendfile_"))
        {
            // Формат: sendfile_{mediaItemId}
            if (long.TryParse(data.Substring("sendfile_".Length), out long mediaId))
            {
                var item = await db.MediaItems.Include(m => m.StorageSource).FirstOrDefaultAsync(m => m.Id == mediaId, ct);
                if (item != null && !item.IsDeleted && _authService.CanAccessPath(user, item.StorageSourceId, item.RelativePath))
                {
                    var fullPath = Path.Combine(item.StorageSource.RootPath, item.RelativePath.Replace('/', '\\'));
                    if (System.IO.File.Exists(fullPath))
                    {
                        var ext = item.FileExtension.ToLowerInvariant();
                        await using var stream = System.IO.File.OpenRead(fullPath);

                        if (ImageExtensions.Contains(ext))
                        {
                            _logger.Log("Info", "Telegram", $"Отправка фото: {item.FileName} для @{user.DisplayName}", user.DisplayName);
                            await bot.SendPhotoAsync(
                                query.Message!.Chat.Id,
                                InputFile.FromStream(stream, item.FileName),
                                caption: $"🖼 {item.FileName}\n📁 {item.RelativePath}",
                                cancellationToken: ct
                            );
                        }
                        else if (VideoExtensions.Contains(ext))
                        {
                            _logger.Log("Info", "Telegram", $"Отправка видео: {item.FileName} для @{user.DisplayName}", user.DisplayName);
                            // Лимит Telegram Bot API 50 МБ для прямой отправки
                            if (item.FileSize < 50 * 1024 * 1024)
                            {
                                await bot.SendVideoAsync(
                                    query.Message!.Chat.Id,
                                    InputFile.FromStream(stream, item.FileName),
                                    caption: $"🎬 {item.FileName}\n📁 {item.RelativePath}",
                                    cancellationToken: ct
                                );
                            }
                            else
                            {
                                await bot.SendDocumentAsync(
                                    query.Message!.Chat.Id,
                                    InputFile.FromStream(stream, item.FileName),
                                    caption: $"🎬 {item.FileName} (размер: {item.FileSize / (1024 * 1024)} МБ)",
                                    cancellationToken: ct
                                );
                            }
                        }
                    }
                }
            }
        }
        else if (data.StartsWith("browse_"))
        {
            // Формат: browse_{sourceId}_{folder}_{page}
            var parts = data.Substring("browse_".Length).Split('_');
            if (parts.Length >= 3 && int.TryParse(parts[0], out int sourceId) && int.TryParse(parts[^1], out int page))
            {
                string folder = string.Join("_", parts.Skip(1).Take(parts.Length - 2));
                var source = await db.StorageSources.FindAsync(new object[] { sourceId }, ct);
                if (source != null && Directory.Exists(source.RootPath))
                {
                    var fullTargetDir = string.IsNullOrEmpty(folder)
                        ? source.RootPath
                        : Path.Combine(source.RootPath, folder.Replace('/', '\\'));

                    if (Directory.Exists(fullTargetDir))
                    {
                        var subDirs = Directory.GetDirectories(fullTargetDir)
                            .Select(d => Path.GetRelativePath(source.RootPath, d).Replace('\\', '/'))
                            .Where(rel => _authService.CanAccessPath(user, sourceId, rel))
                            .OrderBy(d => d)
                            .ToList();

                        // Ищем файлы именно в текущей папке
                        var normalizedFolder = folder.Trim('/');
                        var allFolderFiles = await db.MediaItems
                            .Where(m => m.StorageSourceId == sourceId && !m.IsDeleted)
                            .ToListAsync(ct);

                        var currentFolderFiles = allFolderFiles
                            .Where(m =>
                            {
                                var fileDir = Path.GetDirectoryName(m.RelativePath)?.Replace('\\', '/').Trim('/') ?? "";
                                return string.Equals(fileDir, normalizedFolder, StringComparison.OrdinalIgnoreCase)
                                       && _authService.CanAccessPath(user, sourceId, m.RelativePath);
                            })
                            .OrderBy(m => m.FileName)
                            .ToList();

                        var keyboardRows = new List<InlineKeyboardButton[]>();

                        // 1. Кнопка "Случайное фото из этой папки" (если есть фото)
                        bool hasImagesInFolder = currentFolderFiles.Any(f => ImageExtensions.Contains(f.FileExtension));
                        if (hasImagesInFolder)
                        {
                            keyboardRows.Add(new[]
                            {
                                InlineKeyboardButton.WithCallbackData("🎲 Случайное фото из этой папки", $"randfolder_{sourceId}_{folder}")
                            });
                        }

                        // 2. Список подпапок
                        foreach (var dir in subDirs.Take(12))
                        {
                            var dirName = Path.GetFileName(dir);
                            keyboardRows.Add(new[] { InlineKeyboardButton.WithCallbackData($"📁 {dirName}", $"browse_{sourceId}_{dir}_0") });
                        }

                        // 3. Список файлов с пагинацией (по 6 файлов на страницу)
                        const int pageSize = 6;
                        int totalFiles = currentFolderFiles.Count;
                        int totalPages = (int)Math.Ceiling(totalFiles / (double)pageSize);
                        if (page >= totalPages && totalPages > 0) page = totalPages - 1;
                        if (page < 0) page = 0;

                        var pageFiles = currentFolderFiles.Skip(page * pageSize).Take(pageSize).ToList();
                        foreach (var file in pageFiles)
                        {
                            var icon = ImageExtensions.Contains(file.FileExtension) ? "🖼" : (VideoExtensions.Contains(file.FileExtension) ? "🎬" : "📄");
                            keyboardRows.Add(new[]
                            {
                                InlineKeyboardButton.WithCallbackData($"{icon} {file.FileName}", $"sendfile_{file.Id}")
                            });
                        }

                        // Пагинация для файлов
                        if (totalPages > 1)
                        {
                            var navButtons = new List<InlineKeyboardButton>();
                            if (page > 0)
                            {
                                navButtons.Add(InlineKeyboardButton.WithCallbackData("⬅️ Назад", $"browse_{sourceId}_{folder}_{page - 1}"));
                            }
                            navButtons.Add(InlineKeyboardButton.WithCallbackData($"Стр. {page + 1}/{totalPages}", $"browse_{sourceId}_{folder}_{page}"));
                            if (page < totalPages - 1)
                            {
                                navButtons.Add(InlineKeyboardButton.WithCallbackData("Вперед ➡️", $"browse_{sourceId}_{folder}_{page + 1}"));
                            }
                            keyboardRows.Add(navButtons.ToArray());
                        }

                        // 4. Кнопка возврата на уровень выше
                        if (!string.IsNullOrEmpty(folder))
                        {
                            var parentFolder = Path.GetDirectoryName(folder)?.Replace('\\', '/') ?? "";
                            keyboardRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬆️ На уровень вверх", $"browse_{sourceId}_{parentFolder}_0") });
                        }
                        else
                        {
                            keyboardRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ К источникам", "nav_sources") });
                        }

                        var folderTitle = string.IsNullOrEmpty(folder) ? source.Name : Path.GetFileName(folder);
                        await bot.EditMessageTextAsync(
                            query.Message!.Chat.Id,
                            query.Message.MessageId,
                            $"📁 Папка: {folderTitle}\nПодпапок: {subDirs.Count} | Файлов: {currentFolderFiles.Count}",
                            replyMarkup: new InlineKeyboardMarkup(keyboardRows),
                            cancellationToken: ct
                        );
                    }
                }
            }
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

        // Фильтруем строго только фотографии (исключаем видео) и проверяем права
        var candidatePhotos = allItems
            .Where(m => ImageExtensions.Contains(m.FileExtension.ToLowerInvariant()))
            .Where(m => string.IsNullOrEmpty(folder) || m.RelativePath.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            .Where(m => _authService.CanAccessPath(user, m.StorageSourceId, m.RelativePath))
            .ToList();

        if (candidatePhotos.Any())
        {
            var randomItem = candidatePhotos[Random.Shared.Next(candidatePhotos.Count)];
            var fullPath = Path.Combine(randomItem.StorageSource.RootPath, randomItem.RelativePath.Replace('/', '\\'));

            if (System.IO.File.Exists(fullPath))
            {
                _logger.Log("Info", "Telegram", $"Отправка случайного фото: {randomItem.FileName} для @{user.DisplayName}", user.DisplayName);
                await using var stream = System.IO.File.OpenRead(fullPath);
                await bot.SendPhotoAsync(
                    chatId,
                    InputFile.FromStream(stream, randomItem.FileName),
                    caption: $"🎲 {randomItem.FileName}\n📁 {randomItem.RelativePath}",
                    cancellationToken: ct
                );
                return;
            }
        }

        await bot.SendTextMessageAsync(chatId, "В этой папке/источнике не найдено доступных фотографий.", cancellationToken: ct);
    }

    private Task HandleErrorAsync(ITelegramBotClient bot, Exception exception, CancellationToken ct)
    {
        var errorMsg = exception is ApiRequestException apiEx
            ? $"Telegram API Error [{apiEx.ErrorCode}]: {apiEx.Message}"
            : exception.Message;

        _logger.Log("Error", "Telegram", errorMsg);
        return Task.CompletedTask;
    }
}
