using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using TrufaBot.Application.Interfaces;
using TrufaBot.Domain.Entities;
using TrufaBot.Infrastructure.Data;

namespace TrufaBot.Infrastructure.Logging;

public class AuditLogger : IAuditLogger
{
    public ObservableCollection<AuditLogEntry> LiveLogs { get; } = new();
    public event Action<AuditLogEntry>? LogAdded;

    public void Log(string level, string category, string message, string? user = null, string? details = null)
    {
        var entry = new AuditLogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            UserIdentifier = user,
            Message = message,
            Details = details
        };

        LogAdded?.Invoke(entry);

        _ = Task.Run(async () =>
        {
            try
            {
                using var db = new AppDbContext();
                db.AuditLogs.Add(entry);
                await db.SaveChangesAsync();
            }
            catch { }
        });
    }
}
