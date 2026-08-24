using TrufaBot.Domain.Entities;

namespace TrufaBot.Application.Interfaces;

public class DetectedFaceResult
{
    public float BoxX { get; set; }
    public float BoxY { get; set; }
    public float BoxWidth { get; set; }
    public float BoxHeight { get; set; }
    public float Confidence { get; set; }
    public float[] Embedding { get; set; } = Array.Empty<float>();
    public int? MatchedPersonId { get; set; }
    public string? MatchedPersonName { get; set; }
    public float SimilarityScore { get; set; }
}

public interface IFaceRecognitionService
{
    Task<List<DetectedFaceResult>> DetectAndRecognizeFacesAsync(string imagePath, CancellationToken ct = default);
    Task<int?> MatchFaceEmbeddingAsync(float[] embedding, float threshold = 0.60f, CancellationToken ct = default);
    double CalculateCosineSimilarity(float[] emb1, float[] emb2);
}
