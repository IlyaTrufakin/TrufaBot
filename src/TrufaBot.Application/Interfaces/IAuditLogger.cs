using System.Collections.ObjectModel;
using TrufaBot.Domain.Entities;

namespace TrufaBot.Application.Interfaces;

public interface IAuditLogger
{
    void Log(string level, string category, string message, string? user = null, string? details = null);
    ObservableCollection<AuditLogEntry> LiveLogs { get; }
}
