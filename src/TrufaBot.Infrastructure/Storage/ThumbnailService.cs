using System.Runtime.InteropServices;
using SkiaSharp;
using TrufaBot.Infrastructure.Common;

namespace TrufaBot.Infrastructure.Storage;

public interface IThumbnailService
{
    Task<string> GetOrCreateThumbnailAsync(string originalPath, int width = 800, int height = 800);
}

public class ThumbnailService : IThumbnailService
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".avi", ".mkv", ".wmv", ".webm", ".3gp", ".m4v", ".flv", ".mts", ".ts"
    };

    public async Task<string> GetOrCreateThumbnailAsync(string originalPath, int width = 800, int height = 800)
    {
        if (!File.Exists(originalPath)) return string.Empty;

        var fileInfo = new FileInfo(originalPath);
        var ext = fileInfo.Extension.ToLowerInvariant();
        var isVideo = VideoExtensions.Contains(ext);

        var thumbFileName = $"{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}_{width}x{height}_v3.jpg";
        var thumbPath = Path.Combine(AppPaths.CacheFolder, thumbFileName);

        if (File.Exists(thumbPath))
        {
            return thumbPath;
        }

        return await Task.Run(() =>
        {
            try
            {
                if (isVideo)
                {
                    return GenerateVideoThumbnail(originalPath, thumbPath, fileInfo.Name, width, height);
                }
                else
                {
                    return GenerateImageThumbnail(originalPath, thumbPath, width, height);
                }
            }
            catch
            {
                return isVideo ? GenerateFallbackVideoCard(thumbPath, fileInfo.Name, width, height) : originalPath;
            }
        });
    }

    private static string GenerateImageThumbnail(string originalPath, string thumbPath, int width, int height)
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

    private static string GenerateVideoThumbnail(string originalPath, string thumbPath, string fileName, int width, int height)
    {
        SKBitmap? videoFrame = null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            videoFrame = ExtractWindowsShellThumbnail(originalPath, width, height);
        }

        if (videoFrame == null)
        {
            return GenerateFallbackVideoCard(thumbPath, fileName, width, height);
        }

        using (videoFrame)
        {
            // Масштабируем если нужно
            float scale = Math.Min((float)width / videoFrame.Width, (float)height / videoFrame.Height);
            int targetW = Math.Max(1, (int)(videoFrame.Width * scale));
            int targetH = Math.Max(1, (int)(videoFrame.Height * scale));

            var imageInfo = new SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var resizedBitmap = videoFrame.Resize(imageInfo, SKFilterQuality.Medium) ?? videoFrame.Copy();

            // Рисуем стильную полупрозрачную кнопку Play (▶) по центру видео
            using var surface = SKSurface.Create(new SKImageInfo(resizedBitmap.Width, resizedBitmap.Height));
            var canvas = surface.Canvas;
            canvas.DrawBitmap(resizedBitmap, 0, 0);

            float cx = resizedBitmap.Width / 2f;
            float cy = resizedBitmap.Height / 2f;
            float radius = Math.Min(resizedBitmap.Width, resizedBitmap.Height) * 0.14f;
            if (radius < 24) radius = 24;

            // Круглая полупрозрачная подложка
            using var circlePaint = new SKPaint
            {
                Color = new SKColor(0, 0, 0, 160),
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };
            canvas.DrawCircle(cx, cy, radius, circlePaint);

            // Белая обводка круга
            using var strokePaint = new SKPaint
            {
                Color = new SKColor(255, 255, 255, 220),
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(2, radius * 0.08f)
            };
            canvas.DrawCircle(cx, cy, radius, strokePaint);

            // Белый треугольник Play
            using var playPaint = new SKPaint
            {
                Color = SKColors.White,
                IsAntialias = true,
                Style = SKPaintStyle.Fill
            };

            float triSize = radius * 0.7f;
            using var path = new SKPath();
            path.MoveTo(cx - triSize * 0.4f, cy - triSize * 0.6f);
            path.LineTo(cx + triSize * 0.7f, cy);
            path.LineTo(cx - triSize * 0.4f, cy + triSize * 0.6f);
            path.Close();
            canvas.DrawPath(path, playPaint);

            // Сохраняем в JPEG
            using var finalImage = surface.Snapshot();
            using var data = finalImage.Encode(SKEncodedImageFormat.Jpeg, 85);
            using var stream = File.OpenWrite(thumbPath);
            data.SaveTo(stream);

            return thumbPath;
        }
    }

    private static string GenerateFallbackVideoCard(string thumbPath, string fileName, int width, int height)
    {
        int w = Math.Min(width, 800);
        int h = (int)(w * 0.6f);

        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        var canvas = surface.Canvas;

        // Темный градиентный фон
        using var bgPaint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(w, h),
                new[] { new SKColor(24, 28, 36), new SKColor(15, 18, 24) },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, 0, w, h, bgPaint);

        float cx = w / 2f;
        float cy = h / 2f - 20;
        float radius = 40;

        using var circlePaint = new SKPaint
        {
            Color = new SKColor(220, 53, 69, 220), // Стильный рубиновый акцент
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawCircle(cx, cy, radius, circlePaint);

        // Белый треугольник Play
        using var playPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        using var path = new SKPath();
        path.MoveTo(cx - 12, cy - 18);
        path.LineTo(cx + 20, cy);
        path.LineTo(cx - 12, cy + 18);
        path.Close();
        canvas.DrawPath(path, playPaint);

        // Текст названия файла
        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = 20,
            IsAntialias = true,
            TextAlign = SKTextAlign.Center
        };
        canvas.DrawText("🎬 Видеозапись", cx, cy + radius + 30, textPaint);

        using var finalImage = surface.Snapshot();
        using var data = finalImage.Encode(SKEncodedImageFormat.Jpeg, 85);
        using var stream = File.OpenWrite(thumbPath);
        data.SaveTo(stream);

        return thumbPath;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static SKBitmap? ExtractWindowsShellThumbnail(string filePath, int width, int height)
    {
        try
        {
            var uuid = new Guid("bcc18b79-ba16-442f-80c4-8a140df4605f"); // IShellItemImageFactory
            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref uuid, out var factory);
            if (hr != 0 || factory == null) return null;

            var size = new SIZE(width, height);
            hr = factory.GetImage(size, SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_BIGGERSIZEOK, out var hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            try
            {
                using var sysBitmap = System.Drawing.Image.FromHbitmap(hBitmap);
                using var ms = new MemoryStream();
                sysBitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                return SKBitmap.Decode(ms);
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    private static SKBitmap AutoOrient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        switch (origin)
        {
            case SKEncodedOrigin.BottomRight: // 180 deg
                return RotateBitmap(bitmap, 180);

            case SKEncodedOrigin.RightTop: // 90 deg CW
                return RotateBitmap(bitmap, 90);

            case SKEncodedOrigin.LeftBottom: // 270 deg CW (90 CCW)
                return RotateBitmap(bitmap, 270);

            case SKEncodedOrigin.TopRight: // Flip Horizontal
                return FlipBitmap(bitmap, true, false);

            case SKEncodedOrigin.BottomLeft: // Flip Vertical
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
            canvas.Scale(horizontal ? -1 : 1, vertical ? -1 : 1, bitmap.Width / 2f, bitmap.Height / 2f);
            canvas.DrawBitmap(bitmap, 0, 0);
        }
        return flipped;
    }

    #region Win32 P/Invoke
    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a140df4605f")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            [In, MarshalAs(UnmanagedType.Struct)] SIZE size,
            [In] SIIGBF flags,
            [Out] out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
        public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; }
    }

    [Flags]
    private enum SIIGBF
    {
        SIIGBF_RESIZETOFIT = 0x00,
        SIIGBF_BIGGERSIZEOK = 0x01,
        SIIGBF_MEMORYONLY = 0x02,
        SIIGBF_ICONONLY = 0x04,
        SIIGBF_THUMBNAILONLY = 0x08,
        SIIGBF_INCACHEONLY = 0x10
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);
    #endregion
}
