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
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".mp4", ".mov", ".avi", ".mkv"
    };

    public StorageSyncService(IAuditLogger logger)
    {
        _logger = logger;
    }

    public async Task SynchronizeSourceAsync(int sourceId, CancellationToken ct = default)
    {
        using var db = new AppDbContext();
        var source = await db.StorageSources.FindAsync(new object[] { sourceId }, ct);
        if (source == null || !source.IsEnabled) return;

        if (!Directory.Exists(source.RootPath))
        {
            _logger.Log("Warning", "Storage", $"Хранилище '{source.Name}' недоступно по пути: {source.RootPath}");
            return;
        }

        _logger.Log("Info", "Storage", $"Начато сканирование источника: {source.Name} ({source.RootPath})");

        var existingItems = await db.MediaItems
            .Where(m => m.StorageSourceId == sourceId)
            .ToDictionaryAsync(m => m.RelativePath, ct);

        var diskFiles = Directory.EnumerateFiles(source.RootPath, "*.*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        var diskRelativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int addedCount = 0;
        int modifiedCount = 0;

        foreach (var filePath in diskFiles)
        {
            var relativePath = Path.GetRelativePath(source.RootPath, filePath).Replace('\\', '/');
            diskRelativePaths.Add(relativePath);

            var fileInfo = new FileInfo(filePath);

            if (!existingItems.TryGetValue(relativePath, out var mediaItem))
            {
                mediaItem = new MediaItem
                {
                    StorageSourceId = sourceId,
                    RelativePath = relativePath,
                    FileName = fileInfo.Name,
                    FileExtension = fileInfo.Extension.ToLowerInvariant(),
                    FileSize = fileInfo.Length,
                    FileCreatedAt = fileInfo.CreationTimeUtc,
                    FileModifiedAt = fileInfo.LastWriteTimeUtc,
                    IsDeleted = false,
                    ClassificationStatus = ClassificationStatus.Pending,
                    LastIndexedAt = DateTime.UtcNow
                };
                db.MediaItems.Add(mediaItem);
                addedCount++;
            }
            else
            {
                if (mediaItem.IsDeleted || mediaItem.FileSize != fileInfo.Length || mediaItem.FileModifiedAt != fileInfo.LastWriteTimeUtc)
                {
                    mediaItem.IsDeleted = false;
                    mediaItem.FileSize = fileInfo.Length;
                    mediaItem.FileModifiedAt = fileInfo.LastWriteTimeUtc;
                    mediaItem.ClassificationStatus = ClassificationStatus.Pending;
                    modifiedCount++;
                }
            }
        }

        int deletedCount = 0;
        foreach (var (relPath, item) in existingItems)
        {
            if (!diskRelativePaths.Contains(relPath) && !item.IsDeleted)
            {
                item.IsDeleted = true;
                deletedCount++;
            }
        }

        source.LastScannedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.Log("Info", "Storage", $"Синхронизация '{source.Name}' завершена. Добавлено: {addedCount}, Обновлено: {modifiedCount}, Удалено: {deletedCount}");
    }
}
