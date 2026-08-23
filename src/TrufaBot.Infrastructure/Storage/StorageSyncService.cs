using Microsoft.EntityFrameworkCore;
using TrufaBot.Application.Interfaces;
using TrufaBot.Domain.Entities;
using TrufaBot.Infrastructure.Data;

namespace TrufaBot.Infrastructure.Storage;

public class StorageSyncService : IStorageSyncService
{
    private readonly IAuditLogger _logger;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".bmp", ".mp4", ".mov", ".avi", ".mkv", ".webm"
    };

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "@recycle", "#recycle", "$recycle.bin", "system volume information", "@eadir", ".git", ".vs", ".sync", ".tmp", "thumbnails"
    };

    public StorageSyncService(IAuditLogger logger)
    {
        _logger = logger;
    }

    public static bool IsIgnoredDirectory(string dirName)
    {
        var name = dirName.Trim().ToLowerInvariant();
        return IgnoredDirectories.Contains(name) || name.StartsWith("@") || name.StartsWith("$") || (name.StartsWith(".") && name.Length > 1);
    }

    public async Task SynchronizeSourceAsync(int sourceId, CancellationToken ct = default)
    {
        await Task.Run(async () =>
        {
            using var db = new AppDbContext();
            db.ChangeTracker.AutoDetectChangesEnabled = false;

            var source = await db.StorageSources.FindAsync(new object[] { sourceId }, ct);
            if (source == null || !source.IsEnabled) return;

            if (!Directory.Exists(source.RootPath))
            {
                _logger.Log("Warning", "Storage", $"Хранилище '{source.Name}' недоступно по пути: {source.RootPath}");
                return;
            }

            _logger.Log("Info", "Storage", $"Начато быстрое сканирование: {source.Name} ({source.RootPath})");

            var existingItems = await db.MediaItems
                .Where(m => m.StorageSourceId == sourceId)
                .ToDictionaryAsync(m => m.RelativePath, StringComparer.OrdinalIgnoreCase, ct);

            var diskRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var itemsToAdd = new List<MediaItem>();
            int modifiedCount = 0;

            // Быстрый рекурсивный обход с мгновенным пропуском мусорных/системных папок (@Recycle и т.д.)
            var dirsToScan = new Stack<string>();
            dirsToScan.Push(source.RootPath);

            int scannedFilesTotal = 0;

            while (dirsToScan.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var currentDir = dirsToScan.Pop();

                try
                {
                    var dirInfo = new DirectoryInfo(currentDir);

                    // 1. Добавляем подпапки, исключая мусорные и скрытые
                    foreach (var subDir in dirInfo.EnumerateDirectories())
                    {
                        if (!IsIgnoredDirectory(subDir.Name) && (subDir.Attributes & FileAttributes.Hidden) == 0)
                        {
                            dirsToScan.Push(subDir.FullName);
                        }
                    }

                    // 2. Сканируем медиафайлы в текущей папке
                    foreach (var file in dirInfo.EnumerateFiles())
                    {
                        if (!SupportedExtensions.Contains(file.Extension)) continue;
                        if ((file.Attributes & FileAttributes.Hidden) != 0) continue;

                        scannedFilesTotal++;
                        var relativePath = Path.GetRelativePath(source.RootPath, file.FullName).Replace('\\', '/');
                        diskRelativePaths.Add(relativePath);

                        if (!existingItems.TryGetValue(relativePath, out var mediaItem))
                        {
                            itemsToAdd.Add(new MediaItem
                            {
                                StorageSourceId = sourceId,
                                RelativePath = relativePath,
                                FileName = file.Name,
                                FileExtension = file.Extension.ToLowerInvariant(),
                                FileSize = file.Length,
                                FileCreatedAt = file.CreationTimeUtc,
                                FileModifiedAt = file.LastWriteTimeUtc,
                                IsDeleted = false,
                                ClassificationStatus = ClassificationStatus.Pending,
                                LastIndexedAt = DateTime.UtcNow
                            });
                        }
                        else
                        {
                            if (mediaItem.IsDeleted || mediaItem.FileSize != file.Length || mediaItem.FileModifiedAt != file.LastWriteTimeUtc)
                            {
                                mediaItem.IsDeleted = false;
                                mediaItem.FileSize = file.Length;
                                mediaItem.FileModifiedAt = file.LastWriteTimeUtc;
                                mediaItem.ClassificationStatus = ClassificationStatus.Pending;
                                db.Entry(mediaItem).State = EntityState.Modified;
                                modifiedCount++;
                            }
                        }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex)
                {
                    _logger.Log("Warning", "Storage", $"Ошибка доступа к папке '{currentDir}': {ex.Message}");
                }
            }

            // Пакетная вставка новых файлов
            if (itemsToAdd.Count > 0)
            {
                await db.MediaItems.AddRangeAsync(itemsToAdd, ct);
            }

            // Помечаем удаленные файлы
            int deletedCount = 0;
            foreach (var (relPath, item) in existingItems)
            {
                if (!diskRelativePaths.Contains(relPath) && !item.IsDeleted)
                {
                    item.IsDeleted = true;
                    db.Entry(item).State = EntityState.Modified;
                    deletedCount++;
                }
            }

            source.LastScannedAt = DateTime.UtcNow;
            db.Entry(source).State = EntityState.Modified;

            db.ChangeTracker.AutoDetectChangesEnabled = true;
            await db.SaveChangesAsync(ct);

            _logger.Log("Info", "Storage", $"Синхронизация '{source.Name}' завершена. Сканировано: {scannedFilesTotal}, Новых: {itemsToAdd.Count}, Обновлено: {modifiedCount}, Удалено: {deletedCount}");
        }, ct);
    }
}
