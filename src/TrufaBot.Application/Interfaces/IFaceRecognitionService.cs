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
    Task<string> GetOrCreateFaceCropThumbnailAsync(string originalImagePath, float boxX, float boxY, float boxW, float boxH, long faceId, CancellationToken ct = default);
    Task AssignFaceToPersonAsync(long faceId, int personId, CancellationToken ct = default);
    Task IgnoreFaceAsync(long faceId, CancellationToken ct = default);
    Task DeleteFaceAsync(long faceId, CancellationToken ct = default);
    Task ResetAllAssignmentsAsync(CancellationToken ct = default);
    Task ClearAllFacesAndResetAsync(CancellationToken ct = default);
}
