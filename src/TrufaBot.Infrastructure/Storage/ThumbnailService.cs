using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using TrufaBot.Infrastructure.Common;

namespace TrufaBot.Infrastructure.Storage;

public interface IThumbnailService
{
    Task<string> GetOrCreateThumbnailAsync(string originalPath, int width = 320, int height = 320);
}

public class ThumbnailService : IThumbnailService
{
    public async Task<string> GetOrCreateThumbnailAsync(string originalPath, int width = 320, int height = 320)
    {
        if (!File.Exists(originalPath)) return string.Empty;

        var fileInfo = new FileInfo(originalPath);
        var thumbFileName = $"{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}_{width}x{height}.jpg";
        var thumbPath = Path.Combine(AppPaths.CacheFolder, thumbFileName);

        if (File.Exists(thumbPath))
        {
            return thumbPath;
        }

        try
        {
            using var image = await Image.LoadAsync(originalPath);
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Max
            }));
            await image.SaveAsJpegAsync(thumbPath);
            return thumbPath;
        }
        catch
        {
            return originalPath;
        }
    }
}
