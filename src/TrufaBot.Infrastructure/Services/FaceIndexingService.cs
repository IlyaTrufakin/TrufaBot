using Microsoft.EntityFrameworkCore;
using TrufaBot.Application.Interfaces;
using TrufaBot.Domain.Entities;
using TrufaBot.Infrastructure.Data;
using TrufaBot.Infrastructure.Storage;

namespace TrufaBot.Infrastructure.Services;

public class FaceIndexingProgressEventArgs : EventArgs
{
    public int ProcessedCount { get; set; }
    public int TotalCount { get; set; }
    public string CurrentFile { get; set; } = "";
    public string StatusMessage { get; set; } = "";
    public bool IsCompleted { get; set; }
}

public class FaceIndexingService
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".heic"
    };

    private readonly IFaceRecognitionService _faceService;
    private readonly IThumbnailService _thumbnailService;
    private readonly IAuditLogger _logger;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
    public event EventHandler<FaceIndexingProgressEventArgs>? ProgressChanged;

    public FaceIndexingService(IFaceRecognitionService faceService, IThumbnailService thumbnailService, IAuditLogger logger)
    {
        _faceService = faceService;
        _thumbnailService = thumbnailService;
        _logger = logger;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public async Task StartFaceIndexingAsync()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _logger.Log("Info", "Faces", "Запущен процесс поиска лиц нейросетью UltraFace.");

        try
        {
            using var db = new AppDbContext();
            
            // Находим ID файлов, которые УЖЕ были отсканированы на лица
            var scannedMediaIds = await db.PersonFaces
                .Select(f => f.MediaItemId)
                .Distinct()
                .ToListAsync(ct);

            var scannedSet = new HashSet<long>(scannedMediaIds);

            var items = await db.MediaItems
                .Include(m => m.StorageSource)
                .Where(m => !m.IsDeleted && m.StorageSource.IsEnabled)
                .OrderBy(m => m.Id)
                .ToListAsync(ct);

            // Обрабатываем ТОЛЬКО новые / несканированные фотографии, чтобы НЕ затирать существующие привязки
            var unindexedPhotoItems = items
                .Where(m => SupportedImageExtensions.Contains(m.FileExtension) && !scannedSet.Contains(m.Id))
                .ToList();

            int total = unindexedPhotoItems.Count;
            int processed = 0;
            int totalFacesFound = 0;

            if (total == 0)
            {
                ProgressChanged?.Invoke(this, new FaceIndexingProgressEventArgs
                {
                    TotalCount = 0,
                    ProcessedCount = 0,
                    StatusMessage = "Все фотографии в архиве уже просканированы нейросетью!",
                    IsCompleted = true
                });
                return;
            }

            ProgressChanged?.Invoke(this, new FaceIndexingProgressEventArgs
            {
                TotalCount = total,
                ProcessedCount = 0,
                StatusMessage = $"Нейросеть сканирует {total} новых фотографий..."
            });

            foreach (var item in unindexedPhotoItems)
            {
                if (ct.IsCancellationRequested) break;

                var fullPath = Path.Combine(item.StorageSource.RootPath, item.RelativePath.Replace('/', '\\'));
                if (File.Exists(fullPath))
                {
                    ProgressChanged?.Invoke(this, new FaceIndexingProgressEventArgs
                    {
                        TotalCount = total,
                        ProcessedCount = processed,
                        CurrentFile = item.FileName,
                        StatusMessage = $"Сканирование ({processed + 1}/{total}): {item.FileName}"
                    });

                    var thumbPath = await _thumbnailService.GetOrCreateThumbnailAsync(fullPath, 800, 800);
                    var detected = await _faceService.DetectAndRecognizeFacesAsync(thumbPath, ct);

                    if (detected.Any())
                    {
                        foreach (var d in detected)
                        {
                            var face = new PersonFace
                            {
                                MediaItemId = item.Id,
                                PersonId = d.MatchedPersonId,
                                BoxX = d.BoxX,
                                BoxY = d.BoxY,
                                BoxWidth = d.BoxWidth,
                                BoxHeight = d.BoxHeight,
                                Embedding = FaceRecognitionService.EncodeEmbedding(d.Embedding),
                                Confidence = d.Confidence,
                                DetectedAt = DateTime.UtcNow
                            };
                            db.PersonFaces.Add(face);
                            totalFacesFound++;
                        }
                        await db.SaveChangesAsync(ct);
                    }
                }

                processed++;
                ProgressChanged?.Invoke(this, new FaceIndexingProgressEventArgs
                {
                    TotalCount = total,
                    ProcessedCount = processed,
                    CurrentFile = item.FileName,
                    StatusMessage = $"Обработано {processed} из {total} (найдено {totalFacesFound} лиц людей)"
                });
            }

            ProgressChanged?.Invoke(this, new FaceIndexingProgressEventArgs
            {
                TotalCount = total,
                ProcessedCount = processed,
                StatusMessage = $"Сканирование завершено! Найдено {totalFacesFound} лиц людей.",
                IsCompleted = true
            });

            _logger.Log("Info", "Faces", $"Сканирование завершено. Обработано {processed} новых фото, обнаружено {totalFacesFound} лиц людей.");
        }
        catch (OperationCanceledException)
        {
            _logger.Log("Info", "Faces", "Сканирование лиц приостановлено пользователем.");
        }
        catch (Exception ex)
        {
            _logger.Log("Error", "Faces", $"Ошибка в процессе поиска лиц: {ex.Message}");
        }
        finally
        {
            _cts = null;
        }
    }
}
