using Microsoft.EntityFrameworkCore;
using TrufaBot.Application.Interfaces;
using TrufaBot.Domain.Entities;
using TrufaBot.Infrastructure.Data;
using TrufaBot.Infrastructure.Storage;

namespace TrufaBot.Infrastructure.Services;

public class AiIndexingProgressEventArgs : EventArgs
{
    public int ProcessedCount { get; set; }
    public int TotalCount { get; set; }
    public string CurrentFile { get; set; } = "";
    public string StatusMessage { get; set; } = "";
    public bool IsCompleted { get; set; }
}

public class AiIndexingService
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".heic"
    };

    private readonly IAiVisionService _aiVisionService;
    private readonly IFaceRecognitionService _faceService;
    private readonly IThumbnailService _thumbnailService;
    private readonly IAuditLogger _logger;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;
    public event EventHandler<AiIndexingProgressEventArgs>? ProgressChanged;

    public AiIndexingService(
        IAiVisionService aiVisionService, 
        IFaceRecognitionService faceService,
        IThumbnailService thumbnailService, 
        IAuditLogger logger)
    {
        _aiVisionService = aiVisionService;
        _faceService = faceService;
        _thumbnailService = thumbnailService;
        _logger = logger;
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public async Task StartIndexingAsync(string serverUrl, string modelName)
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _logger.Log("Info", "AI", "Запущен фоновый процесс индексации фотографий ИИ с распознаванием лиц.");

        try
        {
            using var db = new AppDbContext();
            
            var pendingItems = await db.MediaItems
                .Include(m => m.StorageSource)
                .Include(m => m.Faces)
                .ThenInclude(f => f.Person)
                .Where(m => !m.IsDeleted && m.StorageSource.IsEnabled && (string.IsNullOrEmpty(m.AIDescription) || m.ClassificationStatus == ClassificationStatus.Pending))
                .OrderBy(m => m.Id)
                .ToListAsync(ct);

            var photoItems = pendingItems
                .Where(m => SupportedImageExtensions.Contains(m.FileExtension))
                .ToList();

            int total = photoItems.Count;
            int processed = 0;

            ProgressChanged?.Invoke(this, new AiIndexingProgressEventArgs
            {
                TotalCount = total,
                ProcessedCount = 0,
                StatusMessage = $"Начало анализа {total} фотографий..."
            });

            foreach (var item in photoItems)
            {
                if (ct.IsCancellationRequested) break;

                var fullPath = Path.Combine(item.StorageSource.RootPath, item.RelativePath.Replace('/', '\\'));
                if (File.Exists(fullPath))
                {
                    ProgressChanged?.Invoke(this, new AiIndexingProgressEventArgs
                    {
                        TotalCount = total,
                        ProcessedCount = processed,
                        CurrentFile = item.FileName,
                        StatusMessage = $"Анализ ({processed + 1}/{total}): {item.FileName}"
                    });

                    // Создаем оптимизированную миниатюру 600x600 для быстрой передачи в ИИ
                    var thumbPath = await _thumbnailService.GetOrCreateThumbnailAsync(fullPath, 600, 600);
                    
                    // 1. Распознаем лица на фото
                    var detectedFaces = await _faceService.DetectAndRecognizeFacesAsync(thumbPath, ct);
                    var recognizedPersonNames = detectedFaces
                        .Where(f => !string.IsNullOrEmpty(f.MatchedPersonName))
                        .Select(f => f.MatchedPersonName!)
                        .Distinct()
                        .ToList();

                    // 2. Отправляем в Qwen2.5-VL / LM Studio с контекстом распознанных лиц!
                    var (description, tags) = await _aiVisionService.AnalyzePhotoAsync(thumbPath, serverUrl, modelName, ct);

                    // Если распознаны конкретные люди, обогащаем теги и описание
                    if (recognizedPersonNames.Any())
                    {
                        var peopleTags = string.Join(", ", recognizedPersonNames);
                        tags = string.IsNullOrEmpty(tags) ? peopleTags : $"{peopleTags}, {tags}";
                    }

                    if (!string.IsNullOrEmpty(description) || !string.IsNullOrEmpty(tags))
                    {
                        item.AIDescription = description;
                        item.AITags = tags;
                        item.ClassificationStatus = ClassificationStatus.Processed;
                        item.AIProcessedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);

                        _logger.Log("Info", "AI", $"ИИ описал '{item.FileName}': {description.Substring(0, Math.Min(60, description.Length))}...", details: $"Теги: {tags}");
                    }
                    else
                    {
                        item.ClassificationStatus = ClassificationStatus.Failed;
                        await db.SaveChangesAsync(ct);
                    }
                }

                processed++;
                ProgressChanged?.Invoke(this, new AiIndexingProgressEventArgs
                {
                    TotalCount = total,
                    ProcessedCount = processed,
                    CurrentFile = item.FileName,
                    StatusMessage = $"Обработано {processed} из {total}"
                });
            }

            ProgressChanged?.Invoke(this, new AiIndexingProgressEventArgs
            {
                TotalCount = total,
                ProcessedCount = processed,
                StatusMessage = "Индексация ИИ завершена.",
                IsCompleted = true
            });

            _logger.Log("Info", "AI", $"Индексация ИИ успешно завершена. Обработано {processed} файлов.");
        }
        catch (OperationCanceledException)
        {
            _logger.Log("Info", "AI", "Индексация ИИ остановлена пользователем.");
        }
        catch (Exception ex)
        {
            _logger.Log("Error", "AI", $"Ошибка в процессе индексации ИИ: {ex.Message}", details: ex.StackTrace);
        }
        finally
        {
            _cts = null;
        }
    }
}
