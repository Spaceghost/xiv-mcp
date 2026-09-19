using System.Globalization;
using System.Text.Json;

namespace XivMcp.Plugin.Objectives;

/// <summary>Result of parsing a quest pack: the objectives that validated and one message per entry that did not.</summary>
public sealed record ObjectivePackResult(string? Title, IReadOnlyList<Objective> Objectives, IReadOnlyList<string> Errors);

/// <summary>
/// Parses quest packs. Accepted shapes: <c>{"quests": [...]}</c> (the shot-quests format), <c>{"objectives": [...]}</c>
/// or a bare array. Each entry: id, name|title, zone, territoryId, spot, map {x, y} (or x/y), eorzea_time|eorzeaTime,
/// weather (array or string), radius, description, and either steps (strings) or setup[] + capture, which become
/// "Go to &lt;spot&gt;", the setup lines and "Capture: &lt;capture&gt;". Unknown fields are ignored.
/// </summary>
public static class ObjectivePack
{
    public const int MaxPackBytes = 1024 * 1024;
    public const int MaxEntries = 200;

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 32,
    };

    public static ObjectivePackResult Parse(string json, string? source, DateTimeOffset now)
    {
        if (json.Length > MaxPackBytes)
            throw new ArgumentException($"Pack is larger than {MaxPackBytes / 1024} KiB.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Pack is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            string? title = null;
            JsonElement list;
            if (root.ValueKind == JsonValueKind.Array)
            {
                list = root;
            }
            else if (root.ValueKind == JsonValueKind.Object
                     && (TryGet(root, "quests", out list) || TryGet(root, "objectives", out list))
                     && list.ValueKind == JsonValueKind.Array)
            {
                title = String(root, "title");
            }
            else
            {
                throw new ArgumentException("Pack must be an array of quests or an object with a \"quests\" (or \"objectives\") array.");
            }

            var objectives = new List<Objective>();
            var errors = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var entry in list.EnumerateArray())
            {
                index++;
                if (index > MaxEntries)
                {
                    errors.Add($"Only the first {MaxEntries} entries were read.");
                    break;
                }

                try
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("not an object.");
                    var objective = ObjectiveFactory.Create(ReadDraft(entry, source), now);
                    if (!seen.Add(objective.Id))
                        throw new ArgumentException($"duplicate id '{objective.Id}'.");
                    objectives.Add(objective);
                }
                catch (ArgumentException ex)
                {
                    var id = entry.ValueKind == JsonValueKind.Object ? String(entry, "id") : null;
                    errors.Add($"Entry {index}{(id is null ? "" : $" ({id})")}: {ex.Message}");
                }
            }

            return new ObjectivePackResult(title, objectives, errors);
        }
    }

    public static ObjectiveDraft ReadDraft(JsonElement e, string? source)
    {
        var spot = String(e, "spot");
        var steps = StringList(e, "steps");
        if (steps.Count == 0)
        {
            var built = new List<string>();
            if (spot is not null)
                built.Add($"Go to {spot}");
            built.AddRange(StringList(e, "setup"));
            if (String(e, "capture") is { } capture)
                built.Add($"Capture: {capture}");
            steps = built;
        }

        float? x = null, y = null;
        if (TryGet(e, "map", out var map) && map.ValueKind == JsonValueKind.Object)
        {
            x = Number(map, "x");
            y = Number(map, "y");
        }
        else
        {
            x = Number(e, "x");
            y = Number(e, "y");
        }

        var weather = StringList(e, "weather");
        var territory = Number(e, "territoryId") ?? Number(e, "territory_id");

        return new ObjectiveDraft
        {
            Id = String(e, "id"),
            Title = String(e, "name") ?? String(e, "title"),
            Steps = steps,
            Zone = String(e, "zone"),
            TerritoryId = territory is { } t ? (t >= 0 && t <= uint.MaxValue && t == MathF.Floor(t) ? (uint)t : throw new ArgumentException($"territoryId {t} is not a row id.")) : null,
            MapX = x,
            MapY = y,
            Spot = spot,
            Description = String(e, "description"),
            EorzeaTime = String(e, "eorzea_time") ?? String(e, "eorzeaTime"),
            Weather = weather,
            Radius = Number(e, "radius"),
            Source = source,
        };
    }

    private static bool TryGet(JsonElement e, string name, out JsonElement value)
    {
        foreach (var property in e.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? String(JsonElement e, string name) =>
        TryGet(e, name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static float? Number(JsonElement e, string name)
    {
        if (!TryGet(e, name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => (float)v.GetDouble(),
            JsonValueKind.String when float.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) => f,
            JsonValueKind.Null => null,
            _ => throw new ArgumentException($"{name} must be a number."),
        };
    }

    private static List<string> StringList(JsonElement e, string name)
    {
        if (!TryGet(e, name, out var v))
            return [];
        return v.ValueKind switch
        {
            JsonValueKind.String => [v.GetString()!],
            JsonValueKind.Array => v.EnumerateArray()
                .Select(item => item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object => String(item, "text"),
                    _ => null,
                })
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .ToList(),
            JsonValueKind.Null => [],
            _ => throw new ArgumentException($"{name} must be a string or an array of strings."),
        };
    }

    /// <summary>
    /// Turns a host path into one the plugin can open. Under Wine, "/home/x/pack.json" and "~/pack.json" live on the
    /// Z: drive (the host root); Windows paths pass through unchanged.
    /// </summary>
    public static string ResolvePath(string path, bool windows, string? home)
    {
        var p = path.Trim().Trim('"');
        if (p.Length == 0)
            throw new ArgumentException("path must not be empty.");
        if ((p == "~" || p.StartsWith("~/", StringComparison.Ordinal)) && !string.IsNullOrEmpty(home))
            p = home.TrimEnd('/') + p[1..];
        if (windows && p.StartsWith('/'))
            p = "Z:" + p.Replace('/', '\\');
        return p;
    }
}
