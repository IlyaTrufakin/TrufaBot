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
            var randomItem = await db.MediaItems
                .Include(m => m.StorageSource)
                .Where(m => !m.IsDeleted && m.StorageSource.IsEnabled)
                .OrderBy(r => EF.Functions.Random())
                .FirstOrDefaultAsync(ct);

            if (randomItem != null && _authService.CanAccessPath(user, randomItem.StorageSourceId, randomItem.RelativePath))
            {
                var fullPath = Path.Combine(randomItem.StorageSource.RootPath, randomItem.RelativePath.Replace('/', '\\'));
                if (System.IO.File.Exists(fullPath))
                {
                    _logger.Log("Info", "Telegram", $"Отправка случайного фото пользователю @{user.Username}: {randomItem.FileName}", user.DisplayName);
                    await using var stream = System.IO.File.OpenRead(fullPath);
                    await bot.SendPhotoAsync(
                        query.Message!.Chat.Id,
                        InputFile.FromStream(stream, randomItem.FileName),
                        caption: $"🎲 {randomItem.FileName}\n📁 {randomItem.RelativePath}",
                        cancellationToken: ct
                    );
                    return;
                }
            }

            await bot.SendTextMessageAsync(query.Message!.Chat.Id, "Доступных фотографий не найдено.", cancellationToken: ct);
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

            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🎲 Случайное фото", "random_photo") });

            await bot.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "📂 Выберите источник медиафайлов:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: ct
            );
        }
        else if (data.StartsWith("browse_"))
        {
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
                            .ToList();

                        var files = await db.MediaItems
                            .Where(m => m.StorageSourceId == sourceId && !m.IsDeleted && m.RelativePath.StartsWith(folder))
                            .Take(50)
                            .ToListAsync(ct);

                        var keyboardRows = new List<InlineKeyboardButton[]>();

                        foreach (var dir in subDirs.Take(10))
                        {
                            var dirName = Path.GetFileName(dir);
                            keyboardRows.Add(new[] { InlineKeyboardButton.WithCallbackData($"📁 {dirName}", $"browse_{sourceId}_{dir}_0") });
                        }

                        if (!string.IsNullOrEmpty(folder))
                        {
                            var parentFolder = Path.GetDirectoryName(folder)?.Replace('\\', '/') ?? "";
                            keyboardRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬆️ На уровень вверх", $"browse_{sourceId}_{parentFolder}_0") });
                        }
                        else
                        {
                            keyboardRows.Add(new[] { InlineKeyboardButton.WithCallbackData("⬅️ К источникам", "nav_sources") });
                        }

                        await bot.EditMessageTextAsync(
                            query.Message!.Chat.Id,
                            query.Message.MessageId,
                            $"📁 Папка: {(string.IsNullOrEmpty(folder) ? source.Name : folder)}\nПодпапок: {subDirs.Count}, Файлов: {files.Count}",
                            replyMarkup: new InlineKeyboardMarkup(keyboardRows),
                            cancellationToken: ct
                        );
                    }
                }
            }
        }
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
