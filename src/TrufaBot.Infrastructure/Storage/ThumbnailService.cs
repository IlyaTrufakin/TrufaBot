using SkiaSharp;
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

        return await Task.Run(() =>
        {
            try
            {
                using var codec = SKCodec.Create(originalPath);
                if (codec == null) return originalPath;

                var origInfo = codec.Info;
                float scale = Math.Min((float)width / origInfo.Width, (float)height / origInfo.Height);
                if (scale >= 1.0f) scale = 1.0f;

                int targetW = Math.Max(1, (int)(origInfo.Width * scale));
                int targetH = Math.Max(1, (int)(origInfo.Height * scale));

                using var originalBitmap = SKBitmap.Decode(codec);
                if (originalBitmap == null) return originalPath;

                var imageInfo = new SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var resizedBitmap = originalBitmap.Resize(imageInfo, SKFilterQuality.Medium);
                if (resizedBitmap == null) return originalPath;

                using var image = SKImage.FromBitmap(resizedBitmap);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
                using var stream = File.OpenWrite(thumbPath);
                data.SaveTo(stream);

                return thumbPath;
            }
            catch
            {
                return originalPath;
            }
        });
    }
}
