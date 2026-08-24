namespace TrufaBot.Domain.Entities;

public class PersonFace
{
    public long Id { get; set; }

    public int? PersonId { get; set; }
    public Person? Person { get; set; }

    public long MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    public float BoxX { get; set; }
    public float BoxY { get; set; }
    public float BoxWidth { get; set; }
    public float BoxHeight { get; set; }

    public string? Embedding { get; set; }
    public float Confidence { get; set; }

    public bool IsIgnored { get; set; } = false;

    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
}
