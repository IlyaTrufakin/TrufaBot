using System.Security.Cryptography;
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
            if (bitmap == null || bitmap.Width < 50 || bitmap.Height < 50) return results;

            var detectedFaces = ExtractFaceCrops(bitmap);

            using var db = new AppDbContext();
            var knownFaces = await db.PersonFaces
                .Include(f => f.Person)
                .Where(f => f.PersonId != null && !string.IsNullOrEmpty(f.Embedding))
                .ToListAsync(ct);

            foreach (var face in detectedFaces)
            {
                int? bestPersonId = null;
                string? bestPersonName = null;
                float bestSimilarity = 0f;

                foreach (var known in knownFaces)
                {
                    if (string.IsNullOrEmpty(known.Embedding)) continue;
                    var knownVector = DecodeEmbedding(known.Embedding);
                    if (knownVector == null || knownVector.Length != face.Embedding.Length) continue;

                    var sim = (float)CalculateCosineSimilarity(face.Embedding, knownVector);
                    if (sim > bestSimilarity)
                    {
                        bestSimilarity = sim;
                        if (sim >= 0.60f)
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

    public async Task<int?> MatchFaceEmbeddingAsync(float[] embedding, float threshold = 0.60f, CancellationToken ct = default)
    {
        if (embedding == null || embedding.Length == 0) return null;

        using var db = new AppDbContext();
        var knownFaces = await db.PersonFaces
            .Where(f => f.PersonId != null && !string.IsNullOrEmpty(f.Embedding))
            .ToListAsync(ct);

        int? bestPersonId = null;
        double bestSimilarity = 0;

        foreach (var face in knownFaces)
        {
            if (string.IsNullOrEmpty(face.Embedding)) continue;
            var vector = DecodeEmbedding(face.Embedding);
            if (vector == null || vector.Length != embedding.Length) continue;

            var sim = CalculateCosineSimilarity(embedding, vector);
            if (sim > bestSimilarity && sim >= threshold)
            {
                bestSimilarity = sim;
                bestPersonId = face.PersonId;
            }
        }

        return bestPersonId;
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

                // Добавляем 15% запас вокруг лица
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

    public async Task<int> AssignFaceAndPropagateAsync(long faceId, int personId, float threshold = 0.60f, CancellationToken ct = default)
    {
        using var db = new AppDbContext();
        var targetFace = await db.PersonFaces.FindAsync(new object[] { faceId }, ct);
        if (targetFace == null) return 0;

        targetFace.PersonId = personId;
        int autoAssignedCount = 1;

        if (!string.IsNullOrEmpty(targetFace.Embedding))
        {
            var targetVector = DecodeEmbedding(targetFace.Embedding);
            if (targetVector != null)
            {
                // Находим все неразмеченные лица и сравниваем с этим эталоном
                var unassigned = await db.PersonFaces
                    .Where(f => f.PersonId == null && !string.IsNullOrEmpty(f.Embedding) && f.Id != faceId)
                    .ToListAsync(ct);

                foreach (var face in unassigned)
                {
                    var vec = DecodeEmbedding(face.Embedding!);
                    if (vec != null && vec.Length == targetVector.Length)
                    {
                        var sim = CalculateCosineSimilarity(targetVector, vec);
                        if (sim >= threshold)
                        {
                            face.PersonId = personId;
                            autoAssignedCount++;
                        }
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return autoAssignedCount;
    }

    public async Task<int> AutoMatchAllUnassignedFacesAsync(float threshold = 0.60f, CancellationToken ct = default)
    {
        using var db = new AppDbContext();

        var knownFaces = await db.PersonFaces
            .Where(f => f.PersonId != null && !string.IsNullOrEmpty(f.Embedding))
            .ToListAsync(ct);

        if (!knownFaces.Any()) return 0;

        var unassignedFaces = await db.PersonFaces
            .Where(f => f.PersonId == null && !string.IsNullOrEmpty(f.Embedding))
            .ToListAsync(ct);

        int matchedCount = 0;

        foreach (var unassigned in unassignedFaces)
        {
            var unassignedVec = DecodeEmbedding(unassigned.Embedding!);
            if (unassignedVec == null) continue;

            int? bestPersonId = null;
            double bestSim = 0;

            foreach (var known in knownFaces)
            {
                var knownVec = DecodeEmbedding(known.Embedding!);
                if (knownVec == null || knownVec.Length != unassignedVec.Length) continue;

                var sim = CalculateCosineSimilarity(unassignedVec, knownVec);
                if (sim > bestSim && sim >= threshold)
                {
                    bestSim = sim;
                    bestPersonId = known.PersonId;
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

    public double CalculateCosineSimilarity(float[] emb1, float[] emb2)
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

    private List<DetectedFaceResult> ExtractFaceCrops(SKBitmap bitmap)
    {
        var list = new List<DetectedFaceResult>();
        int w = bitmap.Width;
        int h = bitmap.Height;

        var zones = new[]
        {
            new { X = 0.25f, Y = 0.15f, W = 0.50f, H = 0.60f, Conf = 0.85f },
            new { X = 0.10f, Y = 0.20f, W = 0.40f, H = 0.55f, Conf = 0.75f },
            new { X = 0.50f, Y = 0.20f, W = 0.40f, H = 0.55f, Conf = 0.75f }
        };

        foreach (var zone in zones)
        {
            int cropX = (int)(zone.X * w);
            int cropY = (int)(zone.Y * h);
            int cropW = (int)(zone.W * w);
            int cropH = (int)(zone.H * h);

            if (cropW < 40 || cropH < 40) continue;

            var subset = new SKRectI(cropX, cropY, cropX + cropW, cropY + cropH);
            using var faceBitmap = new SKBitmap();
            if (bitmap.ExtractSubset(faceBitmap, subset))
            {
                var embedding = ComputeNormalizedFeatureVector(faceBitmap, EmbeddingSize);
                list.Add(new DetectedFaceResult
                {
                    BoxX = zone.X,
                    BoxY = zone.Y,
                    BoxWidth = zone.W,
                    BoxHeight = zone.H,
                    Confidence = zone.Conf,
                    Embedding = embedding
                });
            }
        }

        return list;
    }

    private float[] ComputeNormalizedFeatureVector(SKBitmap faceBitmap, int vectorLength)
    {
        using var resized = faceBitmap.Resize(new SKImageInfo(64, 64, SKColorType.Gray8), SKFilterQuality.Medium);
        var source = resized ?? faceBitmap;

        var vector = new float[vectorLength];
        int pixelsPerBucket = (source.Width * source.Height) / vectorLength;
        if (pixelsPerBucket < 1) pixelsPerBucket = 1;

        var pixels = source.GetPixelSpan();
        int bucketIndex = 0;
        float sum = 0;
        int count = 0;

        for (int i = 0; i < pixels.Length && bucketIndex < vectorLength; i++)
        {
            sum += pixels[i];
            count++;
            if (count >= pixelsPerBucket)
            {
                vector[bucketIndex] = sum / count;
                bucketIndex++;
                sum = 0;
                count = 0;
            }
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
