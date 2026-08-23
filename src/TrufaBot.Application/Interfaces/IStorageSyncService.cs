namespace TrufaBot.Application.Interfaces;

public interface IStorageSyncService
{
    Task SynchronizeSourceAsync(int sourceId, CancellationToken ct = default);
}
