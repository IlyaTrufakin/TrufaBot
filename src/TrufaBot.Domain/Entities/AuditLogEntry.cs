namespace TrufaBot.Domain.Entities;

public class AuditLogEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "Info";
    public string Category { get; set; } = "System";
    public string? UserIdentifier { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? Details { get; set; }
}
