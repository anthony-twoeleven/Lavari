using Lavari.Domain.Errors;

namespace Lavari.Domain.Images;

public readonly record struct ImageDimensions(int Width, int Height);

public static class ImageResizePolicy
{
    public const int LandscapeMaxWidth = 1920;
    public const int LandscapeMaxHeight = 1080;
    public const int PortraitMaxWidth = 1080;
    public const int PortraitMaxHeight = 1920;
    public const int SquareMaxSide = 1080;
    public const int DefaultThumbnailMaxSide = 360;
    public const int MaxInputDimension = 20_000;
    public const long MaxDecodedPixels = 100_000_000L;

    public static ImageDimensions CalculateMax1080pSize(int width, int height)
    {
        ValidateInputDimensions(width, height);

        int maxW;
        int maxH;
        if (width > height)
        {
            maxW = LandscapeMaxWidth;
            maxH = LandscapeMaxHeight;
        }
        else if (height > width)
        {
            maxW = PortraitMaxWidth;
            maxH = PortraitMaxHeight;
        }
        else
        {
            maxW = SquareMaxSide;
            maxH = SquareMaxSide;
        }

        return FitWithinBox(width, height, maxW, maxH);
    }

    public static ImageDimensions CalculateThumbnailSize(int width, int height, int maxSide = DefaultThumbnailMaxSide)
    {
        ValidateInputDimensions(width, height);
        if (maxSide <= 0 || maxSide > LandscapeMaxWidth)
        {
            throw new ValidationServiceException("Invalid thumbnail dimension bound.", ErrorCodes.InvalidImageUpload);
        }

        return FitWithinBox(width, height, maxSide, maxSide);
    }

    public static ImageDimensions FitWithinBox(int width, int height, int maxWidth, int maxHeight)
    {
        ValidateInputDimensions(width, height);

        if (width <= maxWidth && height <= maxHeight)
        {
            return new ImageDimensions(width, height);
        }

        long widthConstrained = (long)maxWidth * height;
        long heightConstrained = (long)maxHeight * width;

        int targetW;
        int targetH;
        if (widthConstrained <= heightConstrained)
        {
            targetW = maxWidth;
            targetH = (int)Math.Max(1L, ((long)height * maxWidth) / width);
        }
        else
        {
            targetH = maxHeight;
            targetW = (int)Math.Max(1L, ((long)width * maxHeight) / height);
        }

        targetW = Math.Clamp(targetW, 1, maxWidth);
        targetH = Math.Clamp(targetH, 1, maxHeight);
        return new ImageDimensions(targetW, targetH);
    }

    private static void ValidateInputDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 || width > MaxInputDimension || height > MaxInputDimension ||
            (long)width * height > MaxDecodedPixels)
        {
            throw new ValidationServiceException(
                "The uploaded image dimensions are invalid or exceed the maximum allowed resolution.",
                ErrorCodes.InvalidImageUpload,
                "Upload a valid static image within supported resolution limits.");
        }
    }
}
