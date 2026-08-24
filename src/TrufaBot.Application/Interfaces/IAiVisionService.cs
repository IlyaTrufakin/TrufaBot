namespace TrufaBot.Application.Interfaces;

public interface IAiVisionService
{
    Task<(bool Success, string ModelName, string Message)> TestConnectionAsync(string serverUrl, CancellationToken ct = default);
    Task<(string Description, string Tags)> AnalyzePhotoAsync(string imagePath, string serverUrl, string modelName, CancellationToken ct = default);
}
