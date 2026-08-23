using SkiaSharp;
using TrufaBot.Infrastructure.Common;

namespace TrufaBot.Infrastructure.Storage;

public interface IThumbnailService
{
    Task<string> GetOrCreateThumbnailAsync(string originalPath, int width = 600, int height = 600);
}

public class ThumbnailService : IThumbnailService
{
    public async Task<string> GetOrCreateThumbnailAsync(string originalPath, int width = 600, int height = 600)
    {
        if (!File.Exists(originalPath)) return string.Empty;

        var fileInfo = new FileInfo(originalPath);
        // Добавлен суффикс _v2 для обновления кэша с правильной ориентацией EXIF
        var thumbFileName = $"{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}_{width}x{height}_v2.jpg";
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

                using var originalBitmap = SKBitmap.Decode(codec);
                if (originalBitmap == null) return originalPath;

                // 1. Автоматическая ориентация по метаданным EXIF (устранение поворота на 90/180/270 градусов)
                var origin = codec.EncodedOrigin;
                using var orientedBitmap = AutoOrient(originalBitmap, origin);

                // 2. Пропорциональное масштабирование
                float scale = Math.Min((float)width / orientedBitmap.Width, (float)height / orientedBitmap.Height);
                if (scale >= 1.0f) scale = 1.0f;

                int targetW = Math.Max(1, (int)(orientedBitmap.Width * scale));
                int targetH = Math.Max(1, (int)(orientedBitmap.Height * scale));

                var imageInfo = new SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var resizedBitmap = orientedBitmap.Resize(imageInfo, SKFilterQuality.Medium);
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

    private static SKBitmap AutoOrient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        switch (origin)
        {
            case SKEncodedOrigin.RightTop: // Поворот 90 по часовой (типично для телефонов)
                return RotateBitmap(bitmap, 90);

            case SKEncodedOrigin.BottomRight: // Поворот 180
                return RotateBitmap(bitmap, 180);

            case SKEncodedOrigin.LeftBottom: // Поворот 270 по часовой (90 против часовой)
                return RotateBitmap(bitmap, 270);

            case SKEncodedOrigin.TopRight: // Отражение по горизонтали
                return FlipBitmap(bitmap, true, false);

            case SKEncodedOrigin.BottomLeft: // Отражение по вертикали
                return FlipBitmap(bitmap, false, true);

            case SKEncodedOrigin.LeftTop: // Transpose
                using (var f1 = FlipBitmap(bitmap, true, false))
                {
                    return RotateBitmap(f1, 90);
                }

            case SKEncodedOrigin.RightBottom: // Transverse
                using (var f2 = FlipBitmap(bitmap, true, false))
                {
                    return RotateBitmap(f2, 270);
                }

            case SKEncodedOrigin.TopLeft:
            default:
                return bitmap.Copy();
        }
    }

    private static SKBitmap RotateBitmap(SKBitmap bitmap, int degrees)
    {
        bool isRotated90or270 = degrees == 90 || degrees == 270;
        int newWidth = isRotated90or270 ? bitmap.Height : bitmap.Width;
        int newHeight = isRotated90or270 ? bitmap.Width : bitmap.Height;

        var rotated = new SKBitmap(newWidth, newHeight, bitmap.ColorType, bitmap.AlphaType);
        using (var canvas = new SKCanvas(rotated))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(newWidth / 2f, newHeight / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-bitmap.Width / 2f, -bitmap.Height / 2f);
            canvas.DrawBitmap(bitmap, 0, 0);
        }

        return rotated;
    }

    private static SKBitmap FlipBitmap(SKBitmap bitmap, bool horizontal, bool vertical)
    {
        var flipped = new SKBitmap(bitmap.Width, bitmap.Height, bitmap.ColorType, bitmap.AlphaType);
        using (var canvas = new SKCanvas(flipped))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(horizontal ? bitmap.Width : 0, vertical ? bitmap.Height : 0);
            canvas.Scale(horizontal ? -1f : 1f, vertical ? -1f : 1f);
            canvas.DrawBitmap(bitmap, 0, 0);
        }

        return flipped;
    }
}
