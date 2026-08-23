namespace TrufaBot.Domain.Entities;

public class UserFolderPermission
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public int StorageSourceId { get; set; }
    public StorageSource StorageSource { get; set; } = null!;

    public string AllowedRelativePath { get; set; } = "*";
    public bool IsRecursive { get; set; } = true;
    public bool IsDenied { get; set; } = false;
}
