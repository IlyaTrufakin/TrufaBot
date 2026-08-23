using System.Diagnostics;
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

    private static string? _cachedFfmpegPath;
    private static bool _ffmpegChecked;

    public async Task<string> GetOrCreateThumbnailAsync(string originalPath, int width = 800, int height = 800)
    {
        if (!File.Exists(originalPath)) return string.Empty;

        var fileInfo = new FileInfo(originalPath);
        var ext = fileInfo.Extension.ToLowerInvariant();
        var isVideo = VideoExtensions.Contains(ext);

        // Версия кэша v4 для генерации реальных кадров видео с кнопкой Play
        var thumbFileName = $"{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}_{width}x{height}_v4.jpg";
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
        SKBitmap? videoFrame = ExtractVideoFrame(originalPath);

        if (videoFrame == null && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            videoFrame = ExtractWindowsShellThumbnail(originalPath, width, height);
        }

        if (videoFrame == null)
        {
            return GenerateFallbackVideoCard(thumbPath, fileName, width, height);
        }

        using (videoFrame)
        {
            // Масштабируем до целевого размера
            float scale = Math.Min((float)width / videoFrame.Width, (float)height / videoFrame.Height);
            int targetW = Math.Max(1, (int)(videoFrame.Width * scale));
            int targetH = Math.Max(1, (int)(videoFrame.Height * scale));

            var imageInfo = new SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var resizedBitmap = videoFrame.Resize(imageInfo, SKFilterQuality.Medium) ?? videoFrame.Copy();

            // Рисуем стильную полупрозрачную кнопку Play (▶) поверх реального кадра
            using var surface = SKSurface.Create(new SKImageInfo(resizedBitmap.Width, resizedBitmap.Height));
            var canvas = surface.Canvas;
            canvas.DrawBitmap(resizedBitmap, 0, 0);

            float cx = resizedBitmap.Width / 2f;
            float cy = resizedBitmap.Height / 2f;
            float radius = Math.Min(resizedBitmap.Width, resizedBitmap.Height) * 0.14f;
            if (radius < 26) radius = 26;

            // Круглая полупрозрачная темная подложка
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

            // Сохраняем готовый кадр
            using var finalImage = surface.Snapshot();
            using var data = finalImage.Encode(SKEncodedImageFormat.Jpeg, 85);
            using var stream = File.OpenWrite(thumbPath);
            data.SaveTo(stream);

            return thumbPath;
        }
    }

    private static SKBitmap? ExtractVideoFrame(string originalPath)
    {
        var ffmpeg = GetFfmpegPath();
        if (string.IsNullOrEmpty(ffmpeg)) return null;

        var tempFrame = Path.Combine(Path.GetTempPath(), $"trufathumb_{Guid.NewGuid():N}.jpg");

        try
        {
            // Берем кадр на 1.0 секунде (чтобы пропустить черные вступительные кадры)
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-ss 00:00:01 -i \"{originalPath}\" -vframes 1 -q:v 2 \"{tempFrame}\" -y",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using (var proc = Process.Start(psi))
            {
                if (proc != null)
                {
                    proc.WaitForExit(5000);
                }
            }

            if (File.Exists(tempFrame))
            {
                return SKBitmap.Decode(tempFrame);
            }
        }
        catch
        {
            // Игнорируем ошибки вызова процесса
        }
        finally
        {
            try
            {
                if (File.Exists(tempFrame)) File.Delete(tempFrame);
            }
            catch { }
        }

        return null;
    }

    private static string? GetFfmpegPath()
    {
        if (_ffmpegChecked) return _cachedFfmpegPath;

        // 1. Проверяем PATH
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(1000);
            if (proc?.ExitCode == 0)
            {
                _cachedFfmpegPath = "ffmpeg";
                _ffmpegChecked = true;
                return _cachedFfmpegPath;
            }
        }
        catch { }

        // 2. Проверяем стандартные пути WinGet и системы
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Links\ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"ffmpeg\bin\ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe"
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                _cachedFfmpegPath = path;
                _ffmpegChecked = true;
                return _cachedFfmpegPath;
            }
        }

        _ffmpegChecked = true;
        return null;
    }

    private static string GenerateFallbackVideoCard(string thumbPath, string fileName, int width, int height)
    {
        int w = Math.Min(width, 800);
        int h = (int)(w * 0.6f);

        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        var canvas = surface.Canvas;

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
            Color = new SKColor(220, 53, 69, 220),
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };
        canvas.DrawCircle(cx, cy, radius, circlePaint);

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
            var uuid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"); // IShellItem
            int hr = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref uuid, out var shellItem);
            if (hr != 0 || shellItem == null) return null;

            var bhidThumbnail = new Guid("7b0e45f2-e526-47b2-ac61-19d45676e0ac"); // BHID_ThumbnailHandler
            var iidThumbnailProvider = new Guid("e357fccd-a995-4576-b01f-234630154e96"); // IThumbnailProvider

            hr = shellItem.BindToHandler(IntPtr.Zero, ref bhidThumbnail, ref iidThumbnailProvider, out var providerObj);
            if (hr != 0 || providerObj is not IThumbnailProvider provider) return null;

            hr = provider.GetThumbnail((uint)width, out var hBitmap, out _);
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
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
        int GetParent(out IShellItem ppsi);
        int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("e357fccd-a995-4576-b01f-234630154e96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IThumbnailProvider
    {
        [PreserveSig]
        int GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);
    #endregion
}
