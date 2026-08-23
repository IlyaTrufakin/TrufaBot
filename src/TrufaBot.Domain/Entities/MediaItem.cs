namespace TrufaBot.Domain.Entities;

public enum ClassificationStatus
{
    Pending = 0,
    Processed = 1,
    Failed = 2,
    Skipped = 3
}

public class MediaItem
{
    public long Id { get; set; }
    public int StorageSourceId { get; set; }
    public StorageSource StorageSource { get; set; } = null!;

    public string RelativePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileExtension { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string? FileHash { get; set; }
    
    public DateTime FileCreatedAt { get; set; }
    public DateTime FileModifiedAt { get; set; }
    public bool IsDeleted { get; set; } = false;
    
    public ClassificationStatus ClassificationStatus { get; set; } = ClassificationStatus.Pending;
    public DateTime LastIndexedAt { get; set; } = DateTime.UtcNow;

    public ICollection<MediaClassification> Classifications { get; set; } = new List<MediaClassification>();
}
