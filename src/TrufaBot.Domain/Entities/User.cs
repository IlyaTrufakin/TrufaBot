namespace TrufaBot.Domain.Entities;

public class User
{
    public int Id { get; set; }
    public long TelegramUserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? Username { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsAdmin { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<UserFolderPermission> Permissions { get; set; } = new List<UserFolderPermission>();
}
