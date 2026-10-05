using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using TrufaBot.Application.Interfaces;
using TrufaBot.Domain.Entities;
using TrufaBot.Infrastructure.Common;
using TrufaBot.Infrastructure.Data;

namespace TrufaBot.Infrastructure.Services;

public class FaceRecognitionService : IFaceRecognitionService
{
    private const int EmbeddingSize = 128;
    private static readonly string FaceCacheDir = Path.Combine(AppPaths.CacheFolder, "faces");
    private static readonly string ModelDir = Path.Combine(AppPaths.AppDataFolder, "models");
    private static readonly string DetectorModelPath = Path.Combine(ModelDir, "version-RFB-320.onnx");
    private static readonly string EmbeddingModelPath = Path.Combine(ModelDir, "face_embedding.onnx");

    private static readonly string DetectorUrl = "https://raw.githubusercontent.com/Linzaer/Ultra-Light-Fast-Generic-Face-Detector-1MB/master/models/onnx/version-RFB-320.onnx";
    private static readonly string EmbeddingUrl = "https://github.com/opencv/opencv_zoo/raw/main/models/face_recognition_sface/face_recognition_sface_2021dec.onnx";

    private InferenceSession? _detectorSession;
    private InferenceSession? _embeddingSession;
    private readonly object _lock = new();

    public FaceRecognitionService()
    {
        Directory.CreateDirectory(FaceCacheDir);
        Directory.CreateDirectory(ModelDir);
        EnsureModelsDownloaded();
        InitializeSessions();
    }

    private void EnsureModelsDownloaded()
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };

            if (!File.Exists(DetectorModelPath) || new FileInfo(DetectorModelPath).Length < 500000)
            {
                var bytes = client.GetByteArrayAsync(DetectorUrl).GetAwaiter().GetResult();
                if (bytes != null && bytes.Length > 500000) File.WriteAllBytes(DetectorModelPath, bytes);
            }

            if (!File.Exists(EmbeddingModelPath) || new FileInfo(EmbeddingModelPath).Length < 5000000)
            {
                var bytes = client.GetByteArrayAsync(EmbeddingUrl).GetAwaiter().GetResult();
                if (bytes != null && bytes.Length > 5000000) File.WriteAllBytes(EmbeddingModelPath, bytes);
            }
        }
        catch
        {
        }
    }

    private void InitializeSessions()
    {
        lock (_lock)
        {
            if (_detectorSession == null && File.Exists(DetectorModelPath))
            {
                try
                {
                    var opt = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                    _detectorSession = new InferenceSession(DetectorModelPath, opt);
                }
                catch { _detectorSession = null; }
            }

            if (_embeddingSession == null && File.Exists(EmbeddingModelPath))
            {
                try
                {
                    var opt = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                    _embeddingSession = new InferenceSession(EmbeddingModelPath, opt);
                }
                catch { _embeddingSession = null; }
            }
        }
    }

    public async Task<List<DetectedFaceResult>> DetectAndRecognizeFacesAsync(string imagePath, CancellationToken ct = default)
    {
        if (!File.Exists(imagePath)) return new List<DetectedFaceResult>();

        var results = new List<DetectedFaceResult>();

        try
        {
            using var codec = SKCodec.Create(imagePath);
            if (codec == null) return results;

            using var bitmap = SKBitmap.Decode(codec);
            if (bitmap == null || bitmap.Width < 40 || bitmap.Height < 40) return results;

            var detectedFaces = await Task.Run(() => RunUltraFaceAndExtractEmbeddings(bitmap), ct);
            return detectedFaces;
        }
        catch
        {
            return results;
        }
    }

    private List<DetectedFaceResult> RunUltraFaceAndExtractEmbeddings(SKBitmap originalBitmap)
    {
        var list = new List<DetectedFaceResult>();

        lock (_lock)
        {
            if (_detectorSession == null)
            {
                InitializeSessions();
                if (_detectorSession == null) return list;
            }

            const int inputW = 320;
            const int inputH = 240;

            using var resized = originalBitmap.Resize(new SKImageInfo(inputW, inputH, SKColorType.Rgb888x), SKFilterQuality.Medium);
            if (resized == null) return list;

            var inputTensor = new DenseTensor<float>(new[] { 1, 3, inputH, inputW });
            var pixels = resized.Pixels;

            for (int y = 0; y < inputH; y++)
            {
                for (int x = 0; x < inputW; x++)
                {
                    var pixel = pixels[y * inputW + x];
                    inputTensor[0, 0, y, x] = (pixel.Red - 127.0f) / 128.0f;
                    inputTensor[0, 1, y, x] = (pixel.Green - 127.0f) / 128.0f;
                    inputTensor[0, 2, y, x] = (pixel.Blue - 127.0f) / 128.0f;
                }
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor("input", inputTensor)
            };

            using var outputs = _detectorSession.Run(inputs);
            var scoresTensor = outputs.FirstOrDefault(o => o.Name.Contains("scores"))?.AsTensor<float>();
            var boxesTensor = outputs.FirstOrDefault(o => o.Name.Contains("boxes"))?.AsTensor<float>();

            if (scoresTensor == null || boxesTensor == null) return list;

            int numBoxes = scoresTensor.Dimensions[1];
            var candidateBoxes = new List<(float X1, float Y1, float X2, float Y2, float Score)>();

            for (int i = 0; i < numBoxes; i++)
            {
                float score = scoresTensor[0, i, 1];
                if (score > 0.70f)
                {
                    float x1 = Math.Clamp(boxesTensor[0, i, 0], 0f, 1f);
                    float y1 = Math.Clamp(boxesTensor[0, i, 1], 0f, 1f);
                    float x2 = Math.Clamp(boxesTensor[0, i, 2], 0f, 1f);
                    float y2 = Math.Clamp(boxesTensor[0, i, 3], 0f, 1f);

                    float w = x2 - x1;
                    float h = y2 - y1;

                    if (w > 0.04f && h > 0.04f)
                    {
                        candidateBoxes.Add((x1, y1, x2, y2, score));
                    }
                }
            }

            var finalBoxes = ApplyNMS(candidateBoxes, 0.40f);

            int origW = originalBitmap.Width;
            int origH = originalBitmap.Height;

            foreach (var b in finalBoxes)
            {
                float bw = b.X2 - b.X1;
                float bh = b.Y2 - b.Y1;

                // Добавляем 20% запаса вокруг лица для захвата формы головы, подбородка и прически
                float marginX = bw * 0.20f;
                float marginY = bh * 0.20f;

                int cropX = Math.Max(0, (int)((b.X1 - marginX) * origW));
                int cropY = Math.Max(0, (int)((b.Y1 - marginY) * origH));
                int cropW = Math.Min(origW - cropX, (int)((bw + marginX * 2) * origW));
                int cropH = Math.Min(origH - cropY, (int)((bh + marginY * 2) * origH));

                if (cropW < 20 || cropH < 20) continue;

                var rect = new SKRectI(cropX, cropY, cropX + cropW, cropY + cropH);
                using var faceBitmap = new SKBitmap();
                if (originalBitmap.ExtractSubset(faceBitmap, rect))
                {
                    // Вычисляем глубокий вектор лица через нейросеть SFace
                    var embedding = ComputeDeepSFaceEmbedding(faceBitmap);
                    if (embedding.Length == EmbeddingSize)
                    {
                        list.Add(new DetectedFaceResult
                        {
                            BoxX = b.X1,
                            BoxY = b.Y1,
                            BoxWidth = bw,
                            BoxHeight = bh,
                            Confidence = b.Score,
                            Embedding = embedding,
                            MatchedPersonId = null,
                            MatchedPersonName = null
                        });
                    }
                }
            }
        }

        return list;
    }

    public float[] ComputeDeepSFaceEmbedding(SKBitmap faceBitmap)
    {
        lock (_lock)
        {
            if (_embeddingSession == null)
            {
                InitializeSessions();
                if (_embeddingSession == null) return Array.Empty<float>();
            }

            try
            {
                const int sfaceSize = 112;
                using var resized = faceBitmap.Resize(new SKImageInfo(sfaceSize, sfaceSize, SKColorType.Rgb888x), SKFilterQuality.High);
                if (resized == null) return Array.Empty<float>();

                // SFace ожидает NCHW тензор: [1, 3, 112, 112], RGB, значения 0..255
                var inputTensor = new DenseTensor<float>(new[] { 1, 3, sfaceSize, sfaceSize });
                var pixels = resized.Pixels;

                for (int y = 0; y < sfaceSize; y++)
                {
                    int rowOffset = y * sfaceSize;
                    for (int x = 0; x < sfaceSize; x++)
                    {
                        var pixel = pixels[rowOffset + x];
                        inputTensor[0, 0, y, x] = pixel.Red;
                        inputTensor[0, 1, y, x] = pixel.Green;
                        inputTensor[0, 2, y, x] = pixel.Blue;
                    }
                }

                string inputName = _embeddingSession.InputMetadata.Keys.FirstOrDefault() ?? "data";
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
                };

                using var outputs = _embeddingSession.Run(inputs);
                var outputTensor = outputs.First().AsTensor<float>();

                var embedding = new float[outputTensor.Length];
                for (int i = 0; i < embedding.Length; i++)
                {
                    embedding[i] = outputTensor.GetValue(i);
                }

                // L2 Нормализация для точного косинусного сходства
                double norm = 0;
                for (int i = 0; i < embedding.Length; i++) norm += embedding[i] * embedding[i];
                norm = Math.Sqrt(norm);
                if (norm > 0)
                {
                    for (int i = 0; i < embedding.Length; i++) embedding[i] = (float)(embedding[i] / norm);
                }

                return embedding;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SFace ONNX Error]: {ex.Message}");
                return Array.Empty<float>();
            }
        }
    }

    public float[] ExtractSFaceEmbeddingFromImage(string imagePath, float boxX, float boxY, float boxW, float boxH)
    {
        if (!File.Exists(imagePath)) return Array.Empty<float>();

        try
        {
            using var codec = SKCodec.Create(imagePath);
            if (codec == null) return Array.Empty<float>();

            using var originalBitmap = SKBitmap.Decode(codec);
            if (originalBitmap == null) return Array.Empty<float>();

            int origW = originalBitmap.Width;
            int origH = originalBitmap.Height;

            float marginX = boxW * 0.20f;
            float marginY = boxH * 0.20f;

            int cropX = Math.Max(0, (int)((boxX - marginX) * origW));
            int cropY = Math.Max(0, (int)((boxY - marginY) * origH));
            int cropW = Math.Min(origW - cropX, (int)((boxW + marginX * 2) * origW));
            int cropH = Math.Min(origH - cropY, (int)((boxH + marginY * 2) * origH));

            if (cropW < 20 || cropH < 20) return Array.Empty<float>();

            var rect = new SKRectI(cropX, cropY, cropX + cropW, cropY + cropH);
            using var faceBitmap = new SKBitmap();
            if (!originalBitmap.ExtractSubset(faceBitmap, rect)) return Array.Empty<float>();

            return ComputeDeepSFaceEmbedding(faceBitmap);
        }
        catch
        {
            return Array.Empty<float>();
        }
    }

    public static bool IsLegacyDummyEmbedding(float[]? embedding)
    {
        if (embedding == null || embedding.Length != EmbeddingSize) return true;
        // Настоящие векторы SFace центрированы вокруг 0 (примерно 50% значений отрицательные).
        // В старом черновом векторе яркости все 128 чисел были строго положительными (>= 0).
        return embedding.All(v => v >= 0);
    }

    private List<(float X1, float Y1, float X2, float Y2, float Score)> ApplyNMS(List<(float X1, float Y1, float X2, float Y2, float Score)> boxes, float iouThreshold)
    {
        var sorted = boxes.OrderByDescending(b => b.Score).ToList();
        var selected = new List<(float X1, float Y1, float X2, float Y2, float Score)>();

        while (sorted.Count > 0)
        {
            var best = sorted[0];
            selected.Add(best);
            sorted.RemoveAt(0);

            sorted.RemoveAll(box => CalculateIoU(best, box) > iouThreshold);
        }

        return selected;
    }

    private float CalculateIoU((float X1, float Y1, float X2, float Y2, float Score) a, (float X1, float Y1, float X2, float Y2, float Score) b)
    {
        float interX1 = Math.Max(a.X1, b.X1);
        float interY1 = Math.Max(a.Y1, b.Y1);
        float interX2 = Math.Min(a.X2, b.X2);
        float interY2 = Math.Min(a.Y2, b.Y2);

        float interW = Math.Max(0, interX2 - interX1);
        float interH = Math.Max(0, interY2 - interY1);
        float interArea = interW * interH;

        float areaA = (a.X2 - a.X1) * (a.Y2 - a.Y1);
        float areaB = (b.X2 - b.X1) * (b.Y2 - b.Y1);
        float unionArea = areaA + areaB - interArea;

        if (unionArea <= 0) return 0;
        return interArea / unionArea;
    }

    public async Task<string> GetOrCreateFaceCropThumbnailAsync(string originalImagePath, float boxX, float boxY, float boxW, float boxH, long faceId, CancellationToken ct = default)
    {
        var cropPath = Path.Combine(FaceCacheDir, $"face_{faceId}.jpg");
        if (File.Exists(cropPath)) return cropPath;

        return await Task.Run(() =>
        {
            try
            {
                if (!File.Exists(originalImagePath)) return string.Empty;

                using var codec = SKCodec.Create(originalImagePath);
                if (codec == null) return string.Empty;

                using var originalBitmap = SKBitmap.Decode(codec);
                if (originalBitmap == null) return string.Empty;

                int imgW = originalBitmap.Width;
                int imgH = originalBitmap.Height;

                float marginX = boxW * 0.20f;
                float marginY = boxH * 0.20f;

                int x = Math.Max(0, (int)((boxX - marginX) * imgW));
                int y = Math.Max(0, (int)((boxY - marginY) * imgH));
                int w = Math.Min(imgW - x, (int)((boxW + marginX * 2) * imgW));
                int h = Math.Min(imgH - y, (int)((boxH + marginY * 2) * imgH));

                if (w < 20 || h < 20) return string.Empty;

                var rect = new SKRectI(x, y, x + w, y + h);
                using var faceBitmap = new SKBitmap();
                if (!originalBitmap.ExtractSubset(faceBitmap, rect)) return string.Empty;

                using var resized = faceBitmap.Resize(new SKImageInfo(160, 160, SKColorType.Rgba8888, SKAlphaType.Premul), SKFilterQuality.Medium);
                if (resized == null) return string.Empty;

                using var image = SKImage.FromBitmap(resized);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                using var stream = File.OpenWrite(cropPath);
                data.SaveTo(stream);

                return cropPath;
            }
            catch
            {
                return string.Empty;
            }
        }, ct);
    }

    public async Task AssignFaceToPersonAsync(long faceId, int personId, CancellationToken ct = default)
    {
        using var db = new AppDbContext();
        var targetFace = await db.PersonFaces
            .Include(f => f.MediaItem)
            .ThenInclude(m => m.StorageSource)
            .FirstOrDefaultAsync(f => f.Id == faceId, ct);

        if (targetFace == null) return;

        var person = await db.People.FindAsync(new object[] { personId }, ct);
        if (person == null) return;

        targetFace.PersonId = personId;
        targetFace.IsIgnored = false;

        // Если у этого лица был старый черновой вектор, вычисляем настоящий глубокий отпечаток SFace
        var curVec = DecodeEmbedding(targetFace.Embedding ?? "");
        if (IsLegacyDummyEmbedding(curVec))
        {
            var fullPath = Path.Combine(targetFace.MediaItem.StorageSource.RootPath, targetFace.MediaItem.RelativePath.Replace('/', '\\'));
            var newEmb = ExtractSFaceEmbeddingFromImage(fullPath, targetFace.BoxX, targetFace.BoxY, targetFace.BoxWidth, targetFace.BoxHeight);
            if (newEmb.Length == EmbeddingSize)
            {
                targetFace.Embedding = EncodeEmbedding(newEmb);
            }
        }

        // Добавляем имя человека в AITags фотографии!
        if (targetFace.MediaItem != null)
        {
            AddPersonNameToMediaTags(targetFace.MediaItem, person.Name);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task UnassignPhotoFromPersonAsync(long mediaItemId, int personId, CancellationToken ct = default)
    {
        using var db = new AppDbContext();
        var item = await db.MediaItems
            .Include(m => m.Faces)
            .FirstOrDefaultAsync(m => m.Id == mediaItemId, ct);

        if (item == null) return;

        var person = await db.People.FindAsync(new object[] { personId }, ct);

        foreach (var face in item.Faces.Where(f => f.PersonId == personId))
        {
            face.PersonId = null;
        }

        // Удаляем имя человека из тегов, если у него больше нет привязанных лиц на этом фото
        if (person != null && !item.Faces.Any(f => f.PersonId == personId))
        {
            RemovePersonNameFromMediaTags(item, person.Name);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Автоматическое глубокое распознавание (Few-Shot Multi-Prototype Learning):
    /// Обучается на примерах лиц, размеченных пользователем, и находит всех этих людей по всему архиву.
    /// Имена распознанных людей автоматически добавляются в теги (AITags) фотографий.
    /// </summary>
    public async Task<int> AutoMatchAllKnownPeopleAsync(float threshold = 0.38f, CancellationToken ct = default)
    {
        using var db = new AppDbContext();

        // 1. Загружаем всех людей
        var people = await db.People.ToListAsync(ct);
        if (!people.Any()) return 0;
        var personMap = people.ToDictionary(p => p.Id, p => p.Name);

        // 2. Загружаем все эталонные лица, подтвержденные пользователем
        var knownFaces = await db.PersonFaces
            .Include(f => f.MediaItem)
            .ThenInclude(m => m.StorageSource)
            .Where(f => f.PersonId != null && !f.IsIgnored && !string.IsNullOrEmpty(f.Embedding))
            .ToListAsync(ct);

        if (!knownFaces.Any()) return 0;

        // Если среди эталонов есть старые черновые векторы, пересчитываем их через SFace прямо сейчас
        bool anyRefUpdated = false;
        foreach (var kf in knownFaces)
        {
            var v = DecodeEmbedding(kf.Embedding!);
            if (IsLegacyDummyEmbedding(v) && kf.MediaItem != null && kf.MediaItem.StorageSource != null)
            {
                var fullPath = Path.Combine(kf.MediaItem.StorageSource.RootPath, kf.MediaItem.RelativePath.Replace('/', '\\'));
                var newEmb = ExtractSFaceEmbeddingFromImage(fullPath, kf.BoxX, kf.BoxY, kf.BoxWidth, kf.BoxHeight);
                if (newEmb.Length == EmbeddingSize)
                {
                    kf.Embedding = EncodeEmbedding(newEmb);
                    anyRefUpdated = true;
                }
            }
        }
        if (anyRefUpdated)
        {
            await db.SaveChangesAsync(ct);
        }

        // 3. Формируем базу прототипов (Multi-Prototype Prototype Memory) для каждого человека
        var personPrototypes = new Dictionary<int, List<float[]>>();
        foreach (var face in knownFaces)
        {
            var vec = DecodeEmbedding(face.Embedding!);
            if (vec != null && !IsLegacyDummyEmbedding(vec))
            {
                if (!personPrototypes.ContainsKey(face.PersonId!.Value))
                {
                    personPrototypes[face.PersonId!.Value] = new List<float[]>();
                }
                personPrototypes[face.PersonId!.Value].Add(vec);
            }
        }

        if (!personPrototypes.Any()) return 0;

        // 4. Загружаем неразмеченные лица
        var unassignedFaces = await db.PersonFaces
            .Include(f => f.MediaItem)
            .ThenInclude(m => m.StorageSource)
            .Where(f => f.PersonId == null && !f.IsIgnored && !string.IsNullOrEmpty(f.Embedding))
            .ToListAsync(ct);

        int matchedCount = 0;

        foreach (var unassigned in unassignedFaces)
        {
            if (ct.IsCancellationRequested) break;

            var unassignedVec = DecodeEmbedding(unassigned.Embedding!);

            // Если неразмеченное лицо имеет старый вектор, обновляем через SFace
            if (IsLegacyDummyEmbedding(unassignedVec) && unassigned.MediaItem != null && unassigned.MediaItem.StorageSource != null)
            {
                var fullPath = Path.Combine(unassigned.MediaItem.StorageSource.RootPath, unassigned.MediaItem.RelativePath.Replace('/', '\\'));
                var newEmb = ExtractSFaceEmbeddingFromImage(fullPath, unassigned.BoxX, unassigned.BoxY, unassigned.BoxWidth, unassigned.BoxHeight);
                if (newEmb.Length == EmbeddingSize)
                {
                    unassigned.Embedding = EncodeEmbedding(newEmb);
                    unassignedVec = newEmb;
                }
                else
                {
                    continue;
                }
            }

            if (unassignedVec == null || IsLegacyDummyEmbedding(unassignedVec)) continue;

            int? bestPersonId = null;
            double bestSim = 0.0;
            double secondBestSim = 0.0;

            foreach (var (personId, prototypes) in personPrototypes)
            {
                // Максимальное сходство с любым из подтвержденных образцов этого человека (KNN k=1 prototype)
                double maxPersonSim = 0.0;
                foreach (var refVec in prototypes)
                {
                    double sim = CalculateCosineSimilarity(unassignedVec, refVec);
                    if (sim > maxPersonSim) maxPersonSim = sim;
                }

                if (maxPersonSim > bestSim)
                {
                    secondBestSim = bestSim;
                    bestSim = maxPersonSim;
                    bestPersonId = personId;
                }
                else if (maxPersonSim > secondBestSim)
                {
                    secondBestSim = maxPersonSim;
                }
            }

            // Порог уверенности: не менее threshold (0.38) и с запасом от других людей
            if (bestPersonId.HasValue && bestSim >= threshold)
            {
                // Если есть второй кандидат, требуем отрыв минимум 0.03 либо очень высокое совпадение (> 0.45)
                if (secondBestSim == 0.0 || (bestSim - secondBestSim) >= 0.03 || bestSim >= 0.45)
                {
                    unassigned.PersonId = bestPersonId.Value;
                    matchedCount++;

                    // Автоматически добавляем имя распознанного человека в теги фото (AITags)!
                    if (unassigned.MediaItem != null && personMap.TryGetValue(bestPersonId.Value, out var pName))
                    {
                        AddPersonNameToMediaTags(unassigned.MediaItem, pName);
                    }
                }
            }
        }

        if (matchedCount > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return matchedCount;
    }

    /// <summary>
    /// Синхронизирует имена всех распознанных людей в теги (AITags) всех фотографий
    /// </summary>
    public async Task<int> SyncPersonNamesToMediaTagsAsync(CancellationToken ct = default)
    {
        using var db = new AppDbContext();

        var itemsWithPeople = await db.MediaItems
            .Include(m => m.Faces)
            .ThenInclude(f => f.Person)
            .Where(m => m.Faces.Any(f => f.PersonId != null && !f.IsIgnored))
            .ToListAsync(ct);

        int updatedCount = 0;

        foreach (var item in itemsWithPeople)
        {
            var personNames = item.Faces
                .Where(f => f.Person != null && !f.IsIgnored)
                .Select(f => f.Person!.Name)
                .Distinct()
                .ToList();

            string before = item.AITags ?? "";
            foreach (var name in personNames)
            {
                AddPersonNameToMediaTags(item, name);
            }
            if (before != (item.AITags ?? ""))
            {
                updatedCount++;
            }
        }

        if (updatedCount > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return updatedCount;
    }

    /// <summary>
    /// Пересчитывает нейросетевые отпечатки SFace для всех найденных лиц в архиве
    /// </summary>
    public async Task<int> RecomputeAllEmbeddingsAsync(IProgress<(int processed, int total)>? progress = null, CancellationToken ct = default)
    {
        using var db = new AppDbContext();

        var faces = await db.PersonFaces
            .Include(f => f.MediaItem)
            .ThenInclude(m => m.StorageSource)
            .Where(f => !f.IsIgnored)
            .ToListAsync(ct);

        int total = faces.Count;
        int updated = 0;
        int processed = 0;

        for (int i = 0; i < faces.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var face = faces[i];
            var curVec = DecodeEmbedding(face.Embedding ?? "");

            // Пересчитываем только если это старый черновой вектор или пустой
            if (IsLegacyDummyEmbedding(curVec) && face.MediaItem != null && face.MediaItem.StorageSource != null)
            {
                var fullPath = Path.Combine(face.MediaItem.StorageSource.RootPath, face.MediaItem.RelativePath.Replace('/', '\\'));
                var newEmb = ExtractSFaceEmbeddingFromImage(fullPath, face.BoxX, face.BoxY, face.BoxWidth, face.BoxHeight);
                if (newEmb.Length == EmbeddingSize)
                {
                    face.Embedding = EncodeEmbedding(newEmb);
                    updated++;
                }
            }

            processed++;
            if (processed % 50 == 0)
            {
                await db.SaveChangesAsync(ct);
                progress?.Report((processed, total));
            }
        }

        if (updated > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        progress?.Report((total, total));
        return updated;
    }

    public async Task IgnoreFaceAsync(long faceId, CancellationToken ct = default)
    {
        using var db = new AppDbContext();
        var targetFace = await db.PersonFaces.FindAsync(new object[] { faceId }, ct);
        if (targetFace != null)
        {
            targetFace.IsIgnored = true;
            targetFace.PersonId = null;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task DeleteFaceAsync(long faceId, CancellationToken ct = default)
    {
        using var db = new AppDbContext();
        var targetFace = await db.PersonFaces.FindAsync(new object[] { faceId }, ct);
        if (targetFace != null)
        {
            db.PersonFaces.Remove(targetFace);
            await db.SaveChangesAsync(ct);
        }

        var cropPath = Path.Combine(FaceCacheDir, $"face_{faceId}.jpg");
        if (File.Exists(cropPath))
        {
            try { File.Delete(cropPath); } catch { }
        }
    }

    public async Task ResetAllAssignmentsAsync(CancellationToken ct = default)
    {
        using var db = new AppDbContext();
        await db.Database.ExecuteSqlRawAsync("UPDATE PersonFaces SET PersonId = NULL;", ct);
    }

    public async Task ClearAllFacesAndResetAsync(CancellationToken ct = default)
    {
        using var db = new AppDbContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM PersonFaces;", ct);

        try
        {
            if (Directory.Exists(FaceCacheDir))
            {
                foreach (var file in Directory.GetFiles(FaceCacheDir))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch { }
    }

    public static void AddPersonNameToMediaTags(MediaItem item, string personName)
    {
        if (string.IsNullOrWhiteSpace(personName)) return;

        var currentTags = (item.AITags ?? "")
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();

        if (!currentTags.Any(t => string.Equals(t, personName, StringComparison.OrdinalIgnoreCase)))
        {
            currentTags.Insert(0, personName);
            item.AITags = string.Join(", ", currentTags);
        }
    }

    public static void RemovePersonNameFromMediaTags(MediaItem item, string personName)
    {
        if (string.IsNullOrWhiteSpace(personName) || string.IsNullOrEmpty(item.AITags)) return;

        var currentTags = item.AITags
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Where(t => !string.Equals(t, personName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        item.AITags = currentTags.Any() ? string.Join(", ", currentTags) : null;
    }

    public static double CalculateCosineSimilarity(float[] emb1, float[] emb2)
    {
        if (emb1.Length != emb2.Length || emb1.Length == 0) return 0.0;

        double dot = 0.0;
        for (int i = 0; i < emb1.Length; i++)
        {
            dot += emb1[i] * emb2[i];
        }

        return dot;
    }

    public static string EncodeEmbedding(float[] embedding)
    {
        var bytes = new byte[embedding.Length * 4];
        Buffer.BlockCopy(embedding, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    public static float[]? DecodeEmbedding(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var floats = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
            return floats;
        }
        catch
        {
            return null;
        }
    }
}
