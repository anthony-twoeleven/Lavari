using Lavari.Application.Services;
using Lavari.Domain.Errors;
using Lavari.Domain.Images;
using SkiaSharp;

namespace Lavari.Infrastructure.Media;

/// <summary>
/// Open-source MIT-licensed SkiaSharp image processor enforcing the 1080p aspect-preserving bound,
/// EXIF orientation normalization, metadata stripping, and thumbnail generation.
/// </summary>
public sealed class SkiaImageProcessingService : IImageProcessingService
{
    public const int MaxRawUploadBytes = 25 * 1024 * 1024; // 25 MB input guard

    public ProcessedImageResult ProcessForStorage(ReadOnlySpan<byte> rawBytes, string? declaredContentType = null)
    {
        if (rawBytes.IsEmpty || rawBytes.Length > MaxRawUploadBytes)
        {
            throw new ValidationServiceException(
                "Image file is empty or exceeds the 25 MB upload limit.",
                ErrorCodes.InvalidImageUpload);
        }

        using var data = SKData.CreateCopy(rawBytes);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            throw new ValidationServiceException(
                "The uploaded file is not a valid supported image.",
                ErrorCodes.InvalidImageUpload,
                "Upload a valid JPEG, PNG or WebP image.");
        }

        if (codec.FrameCount > 1)
        {
            throw new ValidationServiceException(
                "Animated images are not supported.",
                ErrorCodes.InvalidImageUpload,
                "Upload a static JPEG, PNG or WebP image.");
        }

        var encodedFormat = codec.EncodedFormat;
        if (encodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
        {
            throw new ValidationServiceException(
                $"Image format '{encodedFormat}' is not supported. Only static JPEG, PNG and WebP images are allowed.",
                ErrorCodes.InvalidImageUpload);
        }

        var info = codec.Info;
        var origin = codec.EncodedOrigin;
        var swapAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        var orientedW = swapAxes ? info.Height : info.Width;
        var orientedH = swapAxes ? info.Width : info.Height;

        var target = ImageResizePolicy.CalculateMax1080pSize(orientedW, orientedH);
        var thumbTarget = ImageResizePolicy.CalculateThumbnailSize(target.Width, target.Height);
        var wasDownscaled = target.Width < orientedW || target.Height < orientedH;

        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null)
        {
            throw new ValidationServiceException(
                "The uploaded image could not be decoded.",
                ErrorCodes.InvalidImageUpload);
        }

        using var oriented = ApplyOrientation(decoded, origin);
        using var resized = ResizeBitmap(oriented, target.Width, target.Height);
        using var thumbnail = ResizeBitmap(resized, thumbTarget.Width, thumbTarget.Height);

        var (outFormat, contentType, extension, quality) = encodedFormat == SKEncodedImageFormat.Png
            ? (SKEncodedImageFormat.Png, "image/png", ".png", 100)
            : encodedFormat == SKEncodedImageFormat.Webp
                ? (SKEncodedImageFormat.Webp, "image/webp", ".webp", 88)
                : (SKEncodedImageFormat.Jpeg, "image/jpeg", ".jpg", 88);

        var processedBytes = EncodeBitmap(resized, outFormat, quality);
        var thumbBytes = EncodeBitmap(thumbnail, SKEncodedImageFormat.Jpeg, 82);

        return new ProcessedImageResult(
            processedBytes,
            contentType,
            extension,
            resized.Width,
            resized.Height,
            thumbBytes,
            thumbnail.Width,
            thumbnail.Height,
            wasDownscaled);
    }

    private static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return source.Copy();
        }

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var w = swap ? source.Height : source.Width;
        var h = swap ? source.Width : source.Height;

        var rotated = new SKBitmap(w, h, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(rotated);
        switch (origin)
        {
            case SKEncodedOrigin.RightTop:
                canvas.Translate(w, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(w, h);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, h);
                canvas.RotateDegrees(270);
                break;
            case SKEncodedOrigin.TopRight:
                canvas.Scale(-1, 1, w / 2f, 0);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Scale(1, -1, 0, h / 2f);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.Translate(0, h);
                canvas.RotateDegrees(270);
                canvas.Scale(-1, 1, source.Width / 2f, 0);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(w, 0);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1, source.Width / 2f, 0);
                break;
        }
        using var srcImg = SKImage.FromBitmap(source);
        canvas.DrawImage(srcImg, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Flush();
        return rotated;
    }

    private static SKBitmap ResizeBitmap(SKBitmap source, int targetW, int targetH)
    {
        if (source.Width == targetW && source.Height == targetH)
        {
            return source.Copy();
        }

        var resized = new SKBitmap(targetW, targetH, source.ColorType, source.AlphaType);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        using var canvas = new SKCanvas(resized);
        using var img = SKImage.FromBitmap(source);
        canvas.DrawImage(img, new SKRect(0, 0, targetW, targetH), sampling);
        canvas.Flush();
        return resized;
    }

    private static byte[] EncodeBitmap(SKBitmap bitmap, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(format, quality);
        if (encoded is null)
        {
            throw new ValidationServiceException("Failed to encode processed image.", ErrorCodes.InvalidImageUpload);
        }
        return encoded.ToArray();
    }
}
