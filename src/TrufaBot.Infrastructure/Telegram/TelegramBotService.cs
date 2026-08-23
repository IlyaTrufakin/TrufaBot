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
    private const int DirPageSize = 20;   // 20 РїР°РїРѕРє РЅР° СЃС‚СЂР°РЅРёС†Сѓ
    private const int FilePageSize = 20;  // 20 РјРёРЅРёР°С‚СЋСЂ С„РѕС‚Рѕ РЅР° СЃС‚СЂР°РЅРёС†Сѓ

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
            new BotCommand { Command = "start", Description = "рџЏ  Р“Р»Р°РІРЅРѕРµ РјРµРЅСЋ" },
            new BotCommand { Command = "browse", Description = "рџ“Ѓ РџСЂРѕРІРѕРґРЅРёРє РїР°РїРѕРє" },
            new BotCommand { Command = "random", Description = "рџЋІ РЎР»СѓС‡Р°Р№РЅРѕРµ С„РѕС‚Рѕ" }
        }, cancellationToken: _cts.Token);

        _logger.Log("Info", "Telegram", "Telegram Р±РѕС‚ СѓСЃРїРµС€РЅРѕ Р·Р°РїСѓС‰РµРЅ Рё СЃР»СѓС€Р°РµС‚ Р·Р°РїСЂРѕСЃС‹.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _botClient = null;
        _logger.Log("Info", "Telegram", "Telegram Р±РѕС‚ РѕСЃС‚Р°РЅРѕРІР»РµРЅ.");
    }

    private static ReplyKeyboardMarkup GetPersistentMenuKeyboard()
    {
        return new ReplyKeyboardMarkup(new[]
        {
            new KeyboardButton[] { "рџ“Ѓ РџСЂРѕРІРѕРґРЅРёРє Р°СЂС…РёРІР°", "рџЋІ РЎР»СѓС‡Р°Р№РЅРѕРµ С„РѕС‚Рѕ" }
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
                _logger.Log("Warning", "Security", $"РћС‚РєР°Р· РІ РґРѕСЃС‚СѓРїРµ. РќРµР°РІС‚РѕСЂРёР·РѕРІР°РЅРЅС‹Р№ РїРѕР»СЊР·РѕРІР°С‚РµР»СЊ: @{username} (ID: {userId})");
                if (update.Message != null)
                {
                    await bot.SendMessage(update.Message.Chat.Id, "в›” РЈ РІР°СЃ РЅРµС‚ РґРѕСЃС‚СѓРїР° Рє СЌС‚РѕРјСѓ РјРµРґРёР°-СЃРµСЂРІРµСЂСѓ.", cancellationToken: ct);
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
            _logger.Log("Error", "Telegram", $"РћС€РёР±РєР° РѕР±СЂР°Р±РѕС‚РєРё СЃРѕР±С‹С‚РёСЏ: {ex.Message}", details: ex.StackTrace);
        }
    }

    private async Task HandleTextMessageAsync(ITelegramBotClient bot, Message message, Domain.Entities.User user, AppDbContext db, CancellationToken ct)
    {
        var text = message.Text?.Trim() ?? "";

        if (text.Equals("рџЋІ РЎР»СѓС‡Р°Р№РЅРѕРµ С„РѕС‚Рѕ", StringComparison.OrdinalIgnoreCase) || text.Equals("/random", StringComparison.OrdinalIgnoreCase))
        {
            await SendRandomPhotoAsync(bot, message.Chat.Id, user, db, sourceId: null, folder: null, ct);
        }
        else if (text.Equals("рџ“Ѓ РџСЂРѕРІРѕРґРЅРёРє Р°СЂС…РёРІР°", StringComparison.OrdinalIgnoreCase) || text.Equals("/browse", StringComparison.OrdinalIgnoreCase))
        {
            await SendSourcesMenuAsync(bot, message.Chat.Id, null, user, db, ct);
        }
        else
        {
            _logger.Log("Info", "Telegram", $"РџРѕР»СЊР·РѕРІР°С‚РµР»СЊ @{user.Username ?? user.DisplayName} РѕС‚РєСЂС‹Р» РјРµРЅСЋ.", user.DisplayName);
            await SendMainMenuAsync(bot, message.Chat.Id, user, ct);
        }
    }

    private async Task SendMainMenuAsync(ITelegramBotClient bot, long chatId, Domain.Entities.User user, CancellationToken ct)
    {
        var inlineKeyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("рџ“Ѓ РџСЂРѕРІРѕРґРЅРёРє Р°СЂС…РёРІР°", "nav_sources") },
            new[] { InlineKeyboardButton.WithCallbackData("рџЋІ РЎР»СѓС‡Р°Р№РЅРѕРµ С„РѕС‚Рѕ", "random_photo") }
        });

        await bot.SendMessage(
            chatId,
            $"рџ‘‹ Р—РґСЂР°РІСЃС‚РІСѓР№С‚Рµ, <b>{user.DisplayName}</b>!\n\nР”РѕР±СЂРѕ РїРѕР¶Р°Р»РѕРІР°С‚СЊ РІ СЃРµРјРµР№РЅС‹Р№ Р°СЂС…РёРІ РјРµРґРёР°С„Р°Р№Р»РѕРІ.\nР’С‹Р±РµСЂРёС‚Рµ СЂР°Р·РґРµР» РґР»СЏ РїСЂРѕСЃРјРѕС‚СЂР°:",
            parseMode: ParseMode.Html,
            replyMarkup: inlineKeyboard,
            cancellationToken: ct
        );

        await bot.SendMessage(
            chatId,
            "рџ‘‡ РљРЅРѕРїРєРё Р±С‹СЃС‚СЂРѕРіРѕ РґРѕСЃС‚СѓРїР° РІСЃРµРіРґР° РїРѕРґ СЂСѓРєРѕР№:",
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
                buttons.Add(new[] { InlineKeyboardButton.WithCallbackData($"рџ“¦ {src.Name}", $"select_src_{src.Id}") });
            }
        }

        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("рџЋІ РЎР»СѓС‡Р°Р№РЅРѕРµ С„РѕС‚Рѕ (РІСЃРµ РґРёСЃРєРё)", "random_photo") });

        string messageText = buttons.Count > 1 
            ? "рџ“‚ <b>Р’С‹Р±РµСЂРёС‚Рµ РёСЃС‚РѕС‡РЅРёРє РјРµРґРёР°С„Р°Р№Р»РѕРІ:</b>" 
            : "Р’Р°Рј РїРѕРєР° РЅРµ РЅР°Р·РЅР°С‡РµРЅ РґРѕСЃС‚СѓРї РЅРё Рє РѕРґРЅРѕР№ РїР°РїРєРµ.";

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
                                $"вљ пёЏ <b>Р¤Р°Р№Р» СЃР»РёС€РєРѕРј Р±РѕР»СЊС€РѕР№:</b> {item.FileName}\n" +
                                $"рџ’ѕ Р Р°Р·РјРµСЂ: <b>{item.FileSize / (1024 * 1024.0):F1} РњР‘</b> (Р»РёРјРёС‚ Telegram Bot API вЂ” 50 РњР‘).",
                                parseMode: ParseMode.Html,
                                cancellationToken: ct
                            );
                            return;
                        }

                        _logger.Log("Info", "Telegram", $"РћС‚РїСЂР°РІРєР° РѕСЂРёРіРёРЅР°Р»Р°: {item.FileName} РґР»СЏ @{user.DisplayName}", user.DisplayName);
                        await using var stream = System.IO.File.OpenRead(fullPath);
                        await bot.SendDocument(
                            chatId,
                            InputFile.FromStream(stream, item.FileName),
                            caption: $"рџ“Ґ РћСЂРёРіРёРЅР°Р»: {item.FileName} ({item.FileSize / (1024 * 1024.0):F1} РњР‘)",
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
            await bot.SendMessage(chatId, "РҐСЂР°РЅРёР»РёС‰Рµ РІСЂРµРјРµРЅРЅРѕ РЅРµРґРѕСЃС‚СѓРїРЅРѕ.", cancellationToken: ct);
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

        // РџРѕРґРїР°РїРєРё
        state.CachedSubDirs = Directory.GetDirectories(fullTargetDir)
            .Select(d => Path.GetRelativePath(source.RootPath, d).Replace('\\', '/'))
            .Where(rel => !IsIgnoredPath(rel) && _authService.CanViewFolder(user, state.SourceId, rel))
            .OrderBy(d => d)
            .ToList();

        // Р¤Р°Р№Р»С‹
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

        // Р•СЃР»Рё РІ РѕС‚РєСЂС‹С‚РѕР№ РїР°РїРєРµ РµСЃС‚СЊ С„РѕС‚РѕРіСЂР°С„РёРё -> РЎР РђР—РЈ РѕС‚РїСЂР°РІР»СЏРµРј РіР°Р»РµСЂРµСЋ РјРёРЅРёР°С‚СЋСЂ!
        if (hasPhotos)
        {
            await SendPhotoGalleryPageAsync(bot, chatId, user, db, state, ct);
        }
        else
        {
            // Р•СЃР»Рё С„РѕС‚Рѕ РЅРµС‚, Р° С‚РѕР»СЊРєРѕ РїР°РїРєРё (РЅР°РїСЂРёРјРµСЂ РєРѕСЂРµРЅСЊ Р°СЂС…РёРІР°) -> РІС‹РІРѕРґРёРј СЃРїРёСЃРѕРє РїР°РїРѕРє
            await RenderBrowseViewAsync(bot, chatId, messageId, user, db, state, ct);
        }
    }

    private async Task RenderBrowseViewAsync(ITelegramBotClient bot, long chatId, int? messageId, Domain.Entities.User user, AppDbContext db, UserBrowseState state, CancellationToken ct)
    {
        var source = await db.StorageSources.FindAsync(new object[] { state.SourceId }, ct);
        if (source == null) return;

        var keyboardRows = new List<InlineKeyboardButton[]>();

        // --- РЎРџРРЎРћРљ РџРћР”РџРђРџРћРљ (РџРѕ 20 РїР°РїРѕРє РЅР° СЃС‚СЂР°РЅРёС†Сѓ, РїРѕ 2 РєРЅРѕРїРєРё РІ СЃС‚СЂРѕРєРµ) ---
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
            row.Add(InlineKeyboardButton.WithCallbackData($"рџ“Ѓ {dirName1}", $"enter_dir_{idx1}"));

            if (i + 1 < currentDirSlice.Count)
            {
                int idx2 = state.DirPage * DirPageSize + i + 1;
                var dirName2 = Path.GetFileName(currentDirSlice[i + 1]);
                row.Add(InlineKeyboardButton.WithCallbackData($"рџ“Ѓ {dirName2}", $"enter_dir_{idx2}"));
            }
            keyboardRows.Add(row.ToArray());
        }

        if (totalDirPages > 1)
        {
            var dirNav = new List<InlineKeyboardButton>();
            if (state.DirPage > 0)
                dirNav.Add(InlineKeyboardButton.WithCallbackData("в¬…пёЏ РџР°РїРєРё", $"dir_page_{state.DirPage - 1}"));
            dirNav.Add(InlineKeyboardButton.WithCallbackData($"РџР°РїРєРё {state.DirPage + 1}/{totalDirPages}", $"dir_page_{state.DirPage}"));
            if (state.DirPage < totalDirPages - 1)
                dirNav.Add(InlineKeyboardButton.WithCallbackData("РџР°РїРєРё вћЎпёЏ", $"dir_page_{state.DirPage + 1}"));
            keyboardRows.Add(dirNav.ToArray());
        }

        // РќР°РІРёРіР°С†РёСЏ РІРІРµСЂС… / Рє РёСЃС‚РѕС‡РЅРёРєР°Рј
        if (!string.IsNullOrEmpty(state.FolderPath))
        {
            keyboardRows.Add(new[]
            {
                InlineKeyboardButton.WithCallbackData("в¬†пёЏ Р’РІРµСЂС…", "nav_up"),
                InlineKeyboardButton.WithCallbackData("рџЏ  Р’ РєРѕСЂРµРЅСЊ", "nav_root")
            });
        }
        else
        {
            keyboardRows.Add(new[] { InlineKeyboardButton.WithCallbackData("в¬…пёЏ Рљ РёСЃС‚РѕС‡РЅРёРєР°Рј", "nav_sources") });
        }

        var folderTitle = string.IsNullOrEmpty(state.FolderPath) ? source.Name : Path.GetFileName(state.FolderPath);
        var messageText = $"рџ“Ѓ <b>РџР°РїРєР°:</b> {folderTitle}\n" +
                          $"рџ“‚ <b>РџРѕРґРїР°РїРѕРє:</b> {totalDirs} (РїРѕ {DirPageSize} РЅР° СЃС‚СЂ.)";

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
            await bot.SendMessage(chatId, "Р’ СЌС‚РѕР№ РїР°РїРєРµ РЅРµС‚ С„РѕС‚РѕРіСЂР°С„РёР№.", replyMarkup: GetPersistentMenuKeyboard(), cancellationToken: ct);
            return;
        }

        int totalPhotos = allPhotos.Count;
        int totalPhotoPages = (int)Math.Ceiling(totalPhotos / (double)FilePageSize);
        if (state.FilePage >= totalPhotoPages && totalPhotoPages > 0) state.FilePage = totalPhotoPages - 1;
        if (state.FilePage < 0) state.FilePage = 0;

        var pagePhotos = allPhotos.Skip(state.FilePage * FilePageSize).Take(FilePageSize).ToList();

        _logger.Log("Info", "Telegram", $"Р“РµРЅРµСЂР°С†РёСЏ РіР°Р»РµСЂРµРё РёР· {pagePhotos.Count} РјРёРЅРёР°С‚СЋСЂ (РЎС‚СЂ. {state.FilePage + 1}/{totalPhotoPages}) РґР»СЏ @{user.DisplayName}", user.DisplayName);

        // Telegram MediaGroup РїСЂРёРЅРёРјР°РµС‚ РґРѕ 10 С„РѕС‚Рѕ РІ РѕРґРЅРѕР№ РіСЂСѓРїРїРµ (РєРѕР»Р»Р°Р¶Рµ).
        // РџРѕСЌС‚РѕРјСѓ РґР»СЏ 20 С„РѕС‚Рѕ РѕС‚РїСЂР°РІР»СЏРµРј 2 РіСЂСѓРїРїС‹ (РїРѕ 10 С€С‚), Telegram РєСЂР°СЃРёРІРѕ СЂР°СЃРїРѕР»Р°РіР°РµС‚ РёС… СЃРµС‚РєРѕР№ РїРѕ 2-3 С„РѕС‚Рѕ РІ СЂСЏРґ!
        var batch1 = pagePhotos.Take(10).ToList();
        var batch2 = pagePhotos.Skip(10).Take(10).ToList();

        await SendMediaBatchAsync(bot, chatId, source, batch1, ct);
        if (batch2.Any())
        {
            await SendMediaBatchAsync(bot, chatId, source, batch2, ct);
        }

        // РљР»Р°РІРёР°С‚СѓСЂР° РїР°РіРёРЅР°С†РёРё РїРѕ 20 С„РѕС‚Рѕ + РїРѕРґРїР°РїРєРё (РµСЃР»Рё РµСЃС‚СЊ)
        var navButtons = new List<InlineKeyboardButton[]>();

        // Р•СЃР»Рё РІ СЌС‚РѕР№ РїР°РїРєРµ С‚Р°РєР¶Рµ РµСЃС‚СЊ РїРѕРґРїР°РїРєРё, РІС‹РІРѕРґРёРј РёС…
        if (state.CachedSubDirs.Any())
        {
            for (int i = 0; i < Math.Min(state.CachedSubDirs.Count, 6); i += 2)
            {
                var row = new List<InlineKeyboardButton>();
                row.Add(InlineKeyboardButton.WithCallbackData($"рџ“Ѓ {Path.GetFileName(state.CachedSubDirs[i])}", $"enter_dir_{i}"));
                if (i + 1 < state.CachedSubDirs.Count)
                {
                    row.Add(InlineKeyboardButton.WithCallbackData($"рџ“Ѓ {Path.GetFileName(state.CachedSubDirs[i + 1])}", $"enter_dir_{i + 1}"));
                }
                navButtons.Add(row.ToArray());
            }
        }

        // РЎС‚СЂРѕРєР° РїР°РіРёРЅР°С†РёРё С„РѕС‚Рѕ
        var pageRow = new List<InlineKeyboardButton>();
        if (state.FilePage > 0)
        {
            pageRow.Add(InlineKeyboardButton.WithCallbackData("в¬…пёЏ 20 С„РѕС‚Рѕ", $"file_page_{state.FilePage - 1}"));
        }
        pageRow.Add(InlineKeyboardButton.WithCallbackData($"Р¤РѕС‚Рѕ {state.FilePage + 1}/{totalPhotoPages}", $"file_page_{state.FilePage}"));
        if (state.FilePage < totalPhotoPages - 1)
        {
            pageRow.Add(InlineKeyboardButton.WithCallbackData("20 С„РѕС‚Рѕ вћЎпёЏ", $"file_page_{state.FilePage + 1}"));
        }
        navButtons.Add(pageRow.ToArray());

        // РќР°РІРёРіР°С†РёСЏ
        navButtons.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("в¬†пёЏ Р’РІРµСЂС…", "nav_up"),
            InlineKeyboardButton.WithCallbackData("рџЋІ РЎР»СѓС‡Р°Р№РЅРѕРµ С„РѕС‚Рѕ", "rand_current_folder")
        });

        navButtons.Add(new[]
        {
            InlineKeyboardButton.WithCallbackData("рџЏ  Р“Р»Р°РІРЅРѕРµ РјРµРЅСЋ", "nav_main_menu")
        });

        int startItemIndex = state.FilePage * FilePageSize + 1;
        int endItemIndex = state.FilePage * FilePageSize + pagePhotos.Count;
        var folderTitle = string.IsNullOrEmpty(state.FolderPath) ? source.Name : Path.GetFileName(state.FolderPath);
        var captionText = $"рџ“Ѓ <b>{folderTitle}</b> | Р¤РѕС‚Рѕ {startItemIndex}вЂ“{endItemIndex} РёР· {totalPhotos}\n" +
                          $"<i>(РЎС‚СЂ. {state.FilePage + 1} РёР· {totalPhotoPages})</i>";

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
                    // РЎРѕР·РґР°РµРј РѕРїС‚РёРјРёР·РёСЂРѕРІР°РЅРЅСѓСЋ РјРёРЅРёР°С‚СЋСЂСѓ 600x600 px СЃ РїСЂР°РІРёР»СЊРЅРѕР№ РѕСЂРёРµРЅС‚Р°С†РёРµР№ EXIF
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
                InlineKeyboardButton.WithCallbackData("рџ“Ґ РћСЂРёРіРёРЅР°Р»", $"orig_file_{item.Id}"),
                InlineKeyboardButton.WithCallbackData("рџЋІ Р•С‰Рµ СЃР»СѓС‡Р°Р№РЅРѕРµ", "rand_current_folder")
            },
            new[]
            {
                InlineKeyboardButton.WithCallbackData("рџ“Ѓ РћС‚РєСЂС‹С‚СЊ СЌС‚Сѓ РїР°РїРєСѓ", $"open_parent_folder_{item.Id}"),
                InlineKeyboardButton.WithCallbackData("рџЏ  РњРµРЅСЋ", "nav_main_menu")
            }
        });

        if (ImageExtensions.Contains(ext))
        {
            _logger.Log("Info", "Telegram", $"РћС‚РїСЂР°РІРєР° С„РѕС‚Рѕ: {item.FileName} РґР»СЏ @{user.DisplayName}", user.DisplayName);
            var thumbPath = await _thumbnailService.GetOrCreateThumbnailAsync(fullPath, 1280, 1280);
            await using var stream = System.IO.File.OpenRead(thumbPath);
            await bot.SendPhoto(
                chatId,
                InputFile.FromStream(stream, item.FileName),
                caption: $"рџ–ј <b>{item.FileName}</b>\nрџ“Ѓ <code>{item.RelativePath}</code>\nрџ’ѕ Р Р°Р·РјРµСЂ: {item.FileSize / (1024 * 1024.0):F1} РњР‘",
                parseMode: ParseMode.Html,
                replyMarkup: keyboard,
                cancellationToken: ct
            );
        }
        else if (VideoExtensions.Contains(ext))
        {
            if (item.FileSize > MaxTelegramUploadBytes)
            {
                _logger.Log("Warning", "Telegram", $"Р’РёРґРµРѕ '{item.FileName}' ({item.FileSize / (1024 * 1024.0):F1} РњР‘) РїСЂРµРІС‹С€Р°РµС‚ Р»РёРјРёС‚ Telegram (50 РњР‘).", user.DisplayName);
                await bot.SendMessage(
                    chatId,
                    $"вљ пёЏ <b>Р’РёРґРµРѕ СЃР»РёС€РєРѕРј Р±РѕР»СЊС€РѕРµ РґР»СЏ РѕС‚РїСЂР°РІРєРё РІ Telegram:</b>\n" +
                    $"рџЋ¬ <code>{item.FileName}</code>\n" +
                    $"рџ’ѕ Р Р°Р·РјРµСЂ: <b>{item.FileSize / (1024 * 1024.0):F1} РњР‘</b> (Р»РёРјРёС‚ Telegram Bot API вЂ” 50 РњР‘).\n" +
                    $"<i>Р РµРєРѕРјРµРЅРґСѓРµС‚СЃСЏ РїСЂРѕСЃРјРѕС‚СЂРµС‚СЊ СЌС‚Рѕ РІРёРґРµРѕ РЅР°РїСЂСЏРјСѓСЋ СЃ РґРѕРјР°С€РЅРµРіРѕ NAS.</i>",
                    parseMode: ParseMode.Html,
                    replyMarkup: keyboard,
                    cancellationToken: ct
                );
                return;
            }

            _logger.Log("Info", "Telegram", $"РћС‚РїСЂР°РІРєР° РІРёРґРµРѕ: {item.FileName} РґР»СЏ @{user.DisplayName}", user.DisplayName);
            await using var stream = System.IO.File.OpenRead(fullPath);
            await bot.SendVideo(
                chatId,
                InputFile.FromStream(stream, item.FileName),
                caption: $"рџЋ¬ <b>{item.FileName}</b> ({item.FileSize / (1024 * 1024.0):F1} РњР‘)",
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

        await bot.SendMessage(chatId, "Р’ СЌС‚РѕР№ РїР°РїРєРµ/РёСЃС‚РѕС‡РЅРёРєРµ РЅРµ РЅР°Р№РґРµРЅРѕ РґРѕСЃС‚СѓРїРЅС‹С… С„РѕС‚РѕРіСЂР°С„РёР№.", replyMarkup: GetPersistentMenuKeyboard(), cancellationToken: ct);
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
