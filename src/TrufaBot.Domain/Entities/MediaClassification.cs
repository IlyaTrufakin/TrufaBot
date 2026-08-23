namespace TrufaBot.Domain.Entities;

public class MediaClassification
{
    public long Id { get; set; }
    public long MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    public int CategoryId { get; set; }
    public ClassificationCategory Category { get; set; } = null!;

    public float Confidence { get; set; }
    public string ModelName { get; set; } = "ONNX-CLIP";
    public DateTime ClassifiedAt { get; set; } = DateTime.UtcNow;
}
