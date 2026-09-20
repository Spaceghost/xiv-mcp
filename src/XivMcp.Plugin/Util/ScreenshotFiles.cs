namespace XivMcp.Plugin.Util;

/// <summary>
/// Pure helpers for the game's screenshot folder: which files count as screenshots and which of a set of
/// candidates is the newest. No game memory; host-testable.
/// </summary>
public static class ScreenshotFiles
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg"];

    /// <summary>True for file names the game writes screenshots as (PNG and JPEG only).</summary>
    public static bool IsImage(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName) &&
        Extensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>MIME type for a screenshot file name, or null when it is not a supported image.</summary>
    public static string? MimeType(string? fileName) => fileName switch
    {
        null => null,
        _ when fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) => "image/png",
        _ when fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) => "image/jpeg",
        _ when fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) => "image/jpeg",
        _ => null,
    };

    /// <summary>
    /// The newest image among <paramref name="candidates"/> (name plus last-write time), or null when none
    /// of them is an image. Ties break on the name so the result is deterministic.
    /// </summary>
    public static (string Name, DateTimeOffset Modified)? Newest(IEnumerable<(string Name, DateTimeOffset Modified)> candidates) =>
        candidates
            .Where(c => IsImage(c.Name))
            .OrderByDescending(c => c.Modified)
            .ThenByDescending(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => ((string, DateTimeOffset)?)c)
            .FirstOrDefault();
}
