namespace XivMcp.Standalone;

/// <summary>
/// Finds the installed game's <c>sqpack</c> directory. Pure over two probes (does this directory exist, read this text
/// file), so it is tested without a game. Nothing is written and nothing outside the listed places is searched.
/// </summary>
internal static class GameLocator
{
    /// <summary>Accepts the install root, its <c>game</c> directory or the <c>sqpack</c> directory itself; returns the sqpack path or null.</summary>
    public static string? Normalize(string? path, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        path = path.Trim().TrimEnd('/', '\\');
        foreach (var candidate in new[] { path, Path.Combine(path, "sqpack"), Path.Combine(path, "game", "sqpack") })
        {
            if (directoryExists(Path.Combine(candidate, "ffxiv")))
                return candidate;
        }

        return null;
    }

    /// <summary>Places to look, most specific first. <paramref name="explicitPath"/> is --game or $XIVMCP_GAME.</summary>
    public static IEnumerable<string> Candidates(string? explicitPath, string home, string? appData, Func<string, string?> readText)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            // An explicit path that is wrong must not silently fall through to some other install.
            yield return explicitPath;
            yield break;
        }

        // XIVLauncher.Core (Linux, macOS): launcher.ini has GamePath=...
        foreach (var root in new[] { Path.Combine(home, ".xlcore"), Path.Combine(home, ".var", "app", "dev.goats.xivlauncher", "data", "xlcore") })
        {
            if (IniValue(readText(Path.Combine(root, "launcher.ini")), "GamePath") is { } configured)
                yield return configured;
            yield return Path.Combine(root, "ffxiv");
        }

        // XIVLauncher (Windows): launcherConfigV3.json has "GamePath": "..."
        if (!string.IsNullOrEmpty(appData))
        {
            if (JsonValue(readText(Path.Combine(appData, "XIVLauncher", "launcherConfigV3.json")), "GamePath") is { } configured)
                yield return configured;
        }

        yield return Path.Combine(home, ".steam", "steam", "steamapps", "common", "FINAL FANTASY XIV Online");
        yield return Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "FINAL FANTASY XIV Online");
        yield return @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn";
        yield return @"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY XIV Online";
    }

    public static string? Find(string? explicitPath, string home, string? appData, Func<string, bool> directoryExists, Func<string, string?> readText) =>
        Candidates(explicitPath, home, appData, readText).Select(c => Normalize(c, directoryExists)).FirstOrDefault(p => p is not null);

    public static string? Find(string? explicitPath) => Find(
        explicitPath,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("APPDATA"),
        Directory.Exists,
        static path =>
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        });

    internal static string? IniValue(string? text, string key)
    {
        if (text is null)
            return null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(eq + 1)..].Trim() is { Length: > 0 } value ? value : null;
        }

        return null;
    }

    internal static string? JsonValue(string? text, string key)
    {
        if (text is null)
            return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(key, out var value)
                   && value.ValueKind == System.Text.Json.JsonValueKind.String
                   && value.GetString() is { Length: > 0 } found
                ? found
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
