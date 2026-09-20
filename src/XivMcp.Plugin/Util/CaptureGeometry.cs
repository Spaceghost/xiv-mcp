using System.Numerics;

namespace XivMcp.Plugin.Util;

/// <summary>
/// Pure geometry for <c>take_screenshot</c>: fitting a capture into a maximum dimension and turning a
/// screen-pixel rectangle (an addon's bounds) into the 0..1 UV rectangle the viewport capture takes.
/// No game memory, no Dalamud types; host-testable.
/// </summary>
public static class CaptureGeometry
{
    /// <summary>Smallest capture the tool will produce or downscale to.</summary>
    public const int MinDimension = 64;

    /// <summary>Largest capture the tool will return.</summary>
    public const int MaxDimension = 4096;

    /// <summary>
    /// Scales <paramref name="width"/>x<paramref name="height"/> down so neither side exceeds
    /// <paramref name="maxDimension"/>, preserving aspect ratio. Never scales up; never returns 0.
    /// </summary>
    public static (int Width, int Height) Fit(int width, int height, int maxDimension)
    {
        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        maxDimension = Math.Clamp(maxDimension, MinDimension, MaxDimension);
        var longest = Math.Max(width, height);
        if (longest <= maxDimension)
        {
            return (width, height);
        }

        var scale = (double)maxDimension / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// A screen rectangle in pixels → the UV rectangle of a <paramref name="viewportWidth"/> x
    /// <paramref name="viewportHeight"/> viewport, clamped to 0..1. Returns null when the rectangle does not
    /// overlap the viewport or either side is empty after clamping.
    /// </summary>
    public static (Vector2 Uv0, Vector2 Uv1)? RectToUv(
        float x,
        float y,
        float width,
        float height,
        int viewportWidth,
        int viewportHeight,
        int paddingPixels = 0)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || width <= 0 || height <= 0)
        {
            return null;
        }

        var left = x - paddingPixels;
        var top = y - paddingPixels;
        var right = x + width + paddingPixels;
        var bottom = y + height + paddingPixels;

        left = Math.Clamp(left, 0, viewportWidth);
        top = Math.Clamp(top, 0, viewportHeight);
        right = Math.Clamp(right, 0, viewportWidth);
        bottom = Math.Clamp(bottom, 0, viewportHeight);

        if (right - left < 1 || bottom - top < 1)
        {
            return null;
        }

        return (
            new Vector2(left / viewportWidth, top / viewportHeight),
            new Vector2(right / viewportWidth, bottom / viewportHeight));
    }

    /// <summary>Pixel size of a UV rectangle over a viewport, rounded to whole pixels (at least 1).</summary>
    public static (int Width, int Height) UvToPixels(Vector2 uv0, Vector2 uv1, int viewportWidth, int viewportHeight) =>
        (Math.Max(1, (int)Math.Round((uv1.X - uv0.X) * viewportWidth)),
         Math.Max(1, (int)Math.Round((uv1.Y - uv0.Y) * viewportHeight)));
}
