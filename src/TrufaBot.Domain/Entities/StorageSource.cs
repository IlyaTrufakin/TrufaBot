namespace TrufaBot.Domain.Entities;

public class StorageSource
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RootPath { get; set; } = string.Empty;
    public bool IsNetworkShare { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastScannedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<MediaItem> MediaItems { get; set; } = new List<MediaItem>();
    public ICollection<UserFolderPermission> Permissions { get; set; } = new List<UserFolderPermission>();
}
