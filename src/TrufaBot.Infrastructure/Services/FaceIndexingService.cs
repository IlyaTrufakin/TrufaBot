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

        _logger.Log("Info", "Faces", "Запущен процесс поиска и распознавания лиц на фотографиях.");

        try
        {
            using var db = new AppDbContext();
            var items = await db.MediaItems
                .Include(m => m.StorageSource)
                .Include(m => m.Faces)
                .Where(m => !m.IsDeleted && m.StorageSource.IsEnabled)
                .OrderBy(m => m.Id)
                .ToListAsync(ct);

            var photoItems = items
                .Where(m => SupportedImageExtensions.Contains(m.FileExtension))
                .ToList();

            int total = photoItems.Count;
            int processed = 0;
            int totalFacesFound = 0;

            ProgressChanged?.Invoke(this, new FaceIndexingProgressEventArgs
            {
                TotalCount = total,
                ProcessedCount = 0,
                StatusMessage = $"Сканирование лиц на {total} фотографиях..."
            });

            foreach (var item in photoItems)
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
                        StatusMessage = $"Поиск лиц ({processed + 1}/{total}): {item.FileName}"
                    });

                    var thumbPath = await _thumbnailService.GetOrCreateThumbnailAsync(fullPath, 800, 800);
                    var detected = await _faceService.DetectAndRecognizeFacesAsync(thumbPath, ct);

                    if (detected.Any())
                    {
                        // Удаляем старые лица для этого элемента и добавляем новые
                        var existingFaces = db.PersonFaces.Where(f => f.MediaItemId == item.Id);
                        db.PersonFaces.RemoveRange(existingFaces);

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
                    StatusMessage = $"Обработано {processed} из {total} (найдено {totalFacesFound} лиц)"
                });
            }

            ProgressChanged?.Invoke(this, new FaceIndexingProgressEventArgs
            {
                TotalCount = total,
                ProcessedCount = processed,
                StatusMessage = $"Распознавание лиц завершено! Найдено {totalFacesFound} лиц.",
                IsCompleted = true
            });

            _logger.Log("Info", "Faces", $"Распознавание лиц завершено. Обработано {processed} фото, найдено {totalFacesFound} лиц.");
        }
        catch (OperationCanceledException)
        {
            _logger.Log("Info", "Faces", "Распознавание лиц остановлено пользователем.");
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
