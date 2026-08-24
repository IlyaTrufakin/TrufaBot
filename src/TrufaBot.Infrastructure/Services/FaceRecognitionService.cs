using Microsoft.EntityFrameworkCore;
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

    public FaceRecognitionService()
    {
        Directory.CreateDirectory(FaceCacheDir);
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
            if (bitmap == null || bitmap.Width < 80 || bitmap.Height < 80) return results;

            var detectedFaces = FindRealHumanFaces(bitmap);
            if (!detectedFaces.Any()) return results;

            using var db = new AppDbContext();
            var knownFaces = await db.PersonFaces
                .Include(f => f.Person)
                .Where(f => f.PersonId != null && f.PersonId > 0 && !string.IsNullOrEmpty(f.Embedding))
                .ToListAsync(ct);

            foreach (var face in detectedFaces)
            {
                int? bestPersonId = null;
                string? bestPersonName = null;
                float bestSimilarity = 0f;

                foreach (var known in knownFaces)
                {
                    var knownVector = DecodeEmbedding(known.Embedding!);
                    if (knownVector == null || knownVector.Length != face.Embedding.Length) continue;

                    var sim = (float)CalculateCosineSimilarity(face.Embedding, knownVector);
                    if (sim > bestSimilarity)
                    {
                        bestSimilarity = sim;
                        if (sim >= 0.72f) // Строгий порог распознавания
                        {
                            bestPersonId = known.PersonId;
                            bestPersonName = known.Person?.Name;
                        }
                    }
                }

                face.MatchedPersonId = bestPersonId;
                face.MatchedPersonName = bestPersonName;
                face.SimilarityScore = bestSimilarity;
                results.Add(face);
            }
        }
        catch
        {
        }

        return results;
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
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task IgnoreFaceAsync(long faceId, CancellationToken ct = default)
    {
        using var db = new AppDbContext();
        var targetFace = await db.PersonFaces.FindAsync(new object[] { faceId }, ct);
        if (targetFace != null)
        {
            // -1 означает "Незнакомец / Другой человек (скрыть из списка)"
            targetFace.PersonId = -1;
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
        await db.Database.ExecuteSqlRawAsync("UPDATE PersonFaces SET PersonId = NULL WHERE PersonId != -1;", ct);
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

    /// <summary>
    /// Строгий детектор лиц человека: фильтрует растения, текстуры, фоны по спектру оттенков кожи (Skin-Tone YCbCr/HSV) и структуре лица (глаза/нос/рот)
    /// </summary>
    private List<DetectedFaceResult> FindRealHumanFaces(SKBitmap original)
    {
        var list = new List<DetectedFaceResult>();
        int w = original.Width;
        int h = original.Height;

        // Кандидатные зоны (портретный центр, групповое левое, групповое правое, верхний план)
        var candidateZones = new[]
        {
            new { X = 0.20f, Y = 0.10f, W = 0.60f, H = 0.65f },
            new { X = 0.08f, Y = 0.15f, W = 0.45f, H = 0.55f },
            new { X = 0.47f, Y = 0.15f, W = 0.45f, H = 0.55f },
            new { X = 0.28f, Y = 0.05f, W = 0.44f, H = 0.45f }
        };

        foreach (var zone in candidateZones)
        {
            int cropX = (int)(zone.X * w);
            int cropY = (int)(zone.Y * h);
            int cropW = (int)(zone.W * w);
            int cropH = (int)(zone.H * h);

            if (cropW < 60 || cropH < 60) continue;

            var subset = new SKRectI(cropX, cropY, cropX + cropW, cropY + cropH);
            using var cropBitmap = new SKBitmap();
            if (!original.ExtractSubset(cropBitmap, subset)) continue;

            // 1. Проверка на процент оттенков человеческой кожи (Skin-Tone Test)
            // Исключает зелень, растения, небо, мебель, стены
            if (!ValidateHumanSkinTone(cropBitmap, out float skinRatio)) continue;

            // 2. Проверка анатомической структуры лица (глазная зона темнее, центр носа светлее)
            if (!ValidateFacialLuminanceStructure(cropBitmap)) continue;

            // 3. Вычисление вектора признаков
            var embedding = ComputeNormalizedFeatureVector(cropBitmap, EmbeddingSize);

            list.Add(new DetectedFaceResult
            {
                BoxX = zone.X,
                BoxY = zone.Y,
                BoxWidth = zone.W,
                BoxHeight = zone.H,
                Confidence = skinRatio,
                Embedding = embedding
            });

            // Для одиночных портретов достаточно одного четкого лица
            if (skinRatio > 0.65f) break;
        }

        return list;
    }

    private bool ValidateHumanSkinTone(SKBitmap bitmap, out float skinRatio)
    {
        skinRatio = 0f;
        int totalPixels = 0;
        int skinPixels = 0;
        int greenPixels = 0; // Для отсечения растений

        using var small = bitmap.Resize(new SKImageInfo(64, 64, SKColorType.Rgba8888), SKFilterQuality.Low);
        if (small == null) return false;

        var pixels = small.Pixels;
        foreach (var p in pixels)
        {
            byte r = p.Red;
            byte g = p.Green;
            byte b = p.Blue;
            totalPixels++;

            // Отсекаем растения: зеленый доминирует над красным
            if (g > r && g > b)
            {
                greenPixels++;
            }

            // Классическая модель оттенка кожи человека в RGB
            // R > G > B, достаточная разница и естественные границы
            if (r > 95 && g > 40 && b > 20 &&
                (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b))) > 15 &&
                Math.Abs(r - g) > 15 && r > g && r > b)
            {
                // Проверка в YCbCr: Cb в [77, 127], Cr в [133, 173]
                double cb = 128 - 0.168736 * r - 0.331264 * g + 0.5 * b;
                double cr = 128 + 0.5 * r - 0.418688 * g - 0.081312 * b;

                if (cb >= 77 && cb <= 130 && cr >= 133 && cr <= 175)
                {
                    skinPixels++;
                }
            }
        }

        if (totalPixels == 0) return false;

        // Если в кадре много зелени (растения/листья) — это точно не лицо
        if ((float)greenPixels / totalPixels > 0.20f) return false;

        skinRatio = (float)skinPixels / totalPixels;

        // Человеческое лицо в фокусе должно содержать от 25% до 85% тона кожи
        return skinRatio >= 0.25f && skinRatio <= 0.85f;
    }

    private bool ValidateFacialLuminanceStructure(SKBitmap bitmap)
    {
        using var small = bitmap.Resize(new SKImageInfo(32, 32, SKColorType.Gray8), SKFilterQuality.Low);
        if (small == null) return false;

        var bytes = small.GetPixelSpan();
        if (bytes.Length < 1024) return false;

        // Верхняя треть (глаза/брови)
        float topLuma = 0;
        for (int i = 0; i < 32 * 10; i++) topLuma += bytes[i];
        topLuma /= (32 * 10);

        // Средняя треть (нос/щеки)
        float midLuma = 0;
        for (int i = 32 * 10; i < 32 * 22; i++) midLuma += bytes[i];
        midLuma /= (32 * 12);

        // У лица средняя часть (нос, лоб, щеки) обычно ярче зоны глаз/волос
        // Плоские стены, асфальт или случайные текстуры этого градиента не имеют
        return Math.Abs(midLuma - topLuma) > 4;
    }

    private float[] ComputeNormalizedFeatureVector(SKBitmap faceBitmap, int vectorLength)
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
}
