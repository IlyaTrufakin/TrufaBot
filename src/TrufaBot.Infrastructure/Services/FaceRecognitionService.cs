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

                int cropX = Math.Max(0, (int)(b.X1 * origW));
                int cropY = Math.Max(0, (int)(b.Y1 * origH));
                int cropW = Math.Min(origW - cropX, (int)(bw * origW));
                int cropH = Math.Min(origH - cropY, (int)(bh * origH));

                if (cropW < 20 || cropH < 20) continue;

                var rect = new SKRectI(cropX, cropY, cropX + cropW, cropY + cropH);
                using var faceBitmap = new SKBitmap();
                if (originalBitmap.ExtractSubset(faceBitmap, rect))
                {
                    // Вычисляем глубокий вектор лица через нейросеть SFace
                    var embedding = ComputeDeepSFaceEmbedding(faceBitmap);
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

        return list;
    }

    private float[] ComputeDeepSFaceEmbedding(SKBitmap faceBitmap)
    {
        lock (_lock)
        {
            if (_embeddingSession == null)
            {
                InitializeSessions();
                if (_embeddingSession == null)
                {
                    return ComputeFallbackVector(faceBitmap, EmbeddingSize);
                }
            }

            try
            {
                const int sfaceSize = 112;
                using var resized = faceBitmap.Resize(new SKImageInfo(sfaceSize, sfaceSize, SKColorType.Rgb888x), SKFilterQuality.High);
                if (resized == null) return ComputeFallbackVector(faceBitmap, EmbeddingSize);

                var inputTensor = new DenseTensor<float>(new[] { 1, 112, 112, 3 });
                var pixels = resized.Pixels;

                for (int y = 0; y < sfaceSize; y++)
                {
                    for (int x = 0; x < sfaceSize; x++)
                    {
                        var pixel = pixels[y * sfaceSize + x];
                        inputTensor[0, y, x, 0] = pixel.Red;
                        inputTensor[0, y, x, 1] = pixel.Green;
                        inputTensor[0, y, x, 2] = pixel.Blue;
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

                // L2 Нормализация
                double norm = 0;
                for (int i = 0; i < embedding.Length; i++) norm += embedding[i] * embedding[i];
                norm = Math.Sqrt(norm);
                if (norm > 0)
                {
                    for (int i = 0; i < embedding.Length; i++) embedding[i] = (float)(embedding[i] / norm);
                }

                return embedding;
            }
            catch
            {
                return ComputeFallbackVector(faceBitmap, EmbeddingSize);
            }
        }
    }

    private float[] ComputeFallbackVector(SKBitmap faceBitmap, int vectorLength)
    {
        using var resized = faceBitmap.Resize(new SKImageInfo(32, 32, SKColorType.Gray8), SKFilterQuality.Medium);
        var source = resized ?? faceBitmap;

        var vector = new float[vectorLength];
        var pixels = source.GetPixelSpan();
        int step = pixels.Length / vectorLength;
        if (step < 1) step = 1;

        for (int i = 0; i < vectorLength && (i * step) < pixels.Length; i++)
        {
            vector[i] = pixels[i * step];
        }

        double norm = 0;
        for (int i = 0; i < vector.Length; i++) norm += vector[i] * vector[i];
        norm = Math.Sqrt(norm);
        if (norm > 0)
        {
            for (int i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);
        }

        return vector;
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

                float marginX = boxW * 0.15f;
                float marginY = boxH * 0.15f;

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
        var targetFace = await db.PersonFaces.FindAsync(new object[] { faceId }, ct);
        if (targetFace != null)
        {
            targetFace.PersonId = personId;
            targetFace.IsIgnored = false;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Автоматическое глубокое распознавание по всему архиву на основе размеченных эталонных лиц людей
    /// </summary>
    public async Task<int> AutoMatchAllKnownPeopleAsync(float threshold = 0.42f, CancellationToken ct = default)
    {
        using var db = new AppDbContext();

        // Загружаем все эталонные лица добавленных людей
        var knownFaces = await db.PersonFaces
            .Where(f => f.PersonId != null && !f.IsIgnored && !string.IsNullOrEmpty(f.Embedding))
            .ToListAsync(ct);

        if (!knownFaces.Any()) return 0;

        // Группируем эталоны по каждому человеку
        var personVectors = new Dictionary<int, List<float[]>>();
        foreach (var face in knownFaces)
        {
            var vec = DecodeEmbedding(face.Embedding!);
            if (vec != null)
            {
                if (!personVectors.ContainsKey(face.PersonId!.Value))
                {
                    personVectors[face.PersonId!.Value] = new List<float[]>();
                }
                personVectors[face.PersonId!.Value].Add(vec);
            }
        }

        // Загружаем все неразмеченные лица из архива
        var unassignedFaces = await db.PersonFaces
            .Where(f => f.PersonId == null && !f.IsIgnored && !string.IsNullOrEmpty(f.Embedding))
            .ToListAsync(ct);

        int matchedCount = 0;

        foreach (var unassigned in unassignedFaces)
        {
            var unassignedVec = DecodeEmbedding(unassigned.Embedding!);
            if (unassignedVec == null) continue;

            int? bestPersonId = null;
            double bestSim = 0;

            foreach (var (personId, vectors) in personVectors)
            {
                foreach (var refVec in vectors)
                {
                    if (refVec.Length != unassignedVec.Length) continue;

                    var sim = CalculateCosineSimilarity(unassignedVec, refVec);
                    if (sim > bestSim && sim >= threshold)
                    {
                        bestSim = sim;
                        bestPersonId = personId;
                    }
                }
            }

            if (bestPersonId.HasValue)
            {
                unassigned.PersonId = bestPersonId.Value;
                matchedCount++;
            }
        }

        if (matchedCount > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return matchedCount;
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

    public static double CalculateCosineSimilarity(float[] emb1, float[] emb2)
    {
        if (emb1.Length != emb2.Length || emb1.Length == 0) return 0.0;

        double dot = 0.0;
        double norm1 = 0.0;
        double norm2 = 0.0;

        for (int i = 0; i < emb1.Length; i++)
        {
            dot += emb1[i] * emb2[i];
            norm1 += emb1[i] * emb1[i];
            norm2 += emb2[i] * emb2[i];
        }

        if (norm1 <= 0 || norm2 <= 0) return 0.0;
        return dot / (Math.Sqrt(norm1) * Math.Sqrt(norm2));
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
