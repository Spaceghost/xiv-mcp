namespace XivMcp.Plugin.Objectives;

/// <summary>One step of an objective. Immutable; progress changes replace the objective.</summary>
public sealed record ObjectiveStep(string Text, bool Done);

/// <summary>Where and when an objective can be done. Every part is optional.</summary>
public sealed record ObjectiveConditions
{
    /// <summary>Eorzea time window as text ("21:00-03:00", "any"); validated when the objective is created.</summary>
    public string? EorzeaTime { get; init; }

    /// <summary>Weather names in the client language ("Clear Skies"); empty or "any" means any weather.</summary>
    public IReadOnlyList<string> Weather { get; init; } = [];

    /// <summary>How close to the spot (yalms, on the ground plane) counts as "there".</summary>
    public float Radius { get; init; } = Objective.DefaultRadius;

    public EorzeaTimeWindow TimeWindow => EorzeaTimeWindow.TryParse(EorzeaTime, out var window, out _) ? window : EorzeaTimeWindow.Any;

    /// <summary>Weather names that actually constrain (without "any", blanks and duplicates).</summary>
    public IReadOnlyList<string> WeatherConstraint =>
        Weather.Any(w => ObjectiveText.IsAny(w))
            ? []
            : Weather.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>
/// A custom objective ("quest") posted by an agent, loaded from a pack or created by the player. It is plugin state
/// only: the game's Journal and quest list are server-side and are never touched.
/// </summary>
public sealed record Objective
{
    public const float DefaultRadius = 30f;
    public const int MaxIdLength = 64;
    public const int MaxTitleLength = 120;
    public const int MaxSteps = 30;
    public const int MaxStepLength = 300;
    public const int MaxTextLength = 2000;

    public required string Id { get; init; }

    public required string Title { get; init; }

    public IReadOnlyList<ObjectiveStep> Steps { get; init; } = [];

    /// <summary>Zone name as given (display only; <see cref="TerritoryId"/> is what conditions use).</summary>
    public string? Zone { get; init; }

    public uint? TerritoryId { get; init; }

    /// <summary>Map coordinates as the game displays them.</summary>
    public float? MapX { get; init; }

    public float? MapY { get; init; }

    /// <summary>Free-text description of the spot ("the beach below the villa").</summary>
    public string? Spot { get; init; }

    public string? Description { get; init; }

    /// <summary>Latest short note from whoever updates it (agent commentary shown under the step).</summary>
    public string? Note { get; init; }

    public ObjectiveConditions Conditions { get; init; } = new();

    /// <summary>Who posted it: the MCP client name, "pack:&lt;file&gt;" or "player".</summary>
    public string? Source { get; init; }

    public bool Completed { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public bool HasSpot => TerritoryId is > 0 && MapX is not null && MapY is not null;

    /// <summary>Index of the first step not done, or -1 when every step is done (or there are none).</summary>
    public int CurrentStepIndex
    {
        get
        {
            for (var i = 0; i < Steps.Count; i++)
            {
                if (!Steps[i].Done)
                    return i;
            }

            return -1;
        }
    }

    public string? CurrentStepText => CurrentStepIndex is >= 0 and var i ? Steps[i].Text : null;

    public int StepsDone => Steps.Count(s => s.Done);
}

internal static class ObjectiveText
{
    public static bool IsAny(string? value) =>
        value is not null && (value.Trim().Equals("any", StringComparison.OrdinalIgnoreCase) || value.Trim() == "*");

    public static string Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var chars = value.Where(c => !char.IsControl(c) || c == '\n').ToArray();
        var text = new string(chars).Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    public static string? CleanOrNull(string? value, int max) => Clean(value, max) is { Length: > 0 } text ? text : null;

    /// <summary>Ids are lowercase letters, digits, '-', '_' and '.'; everything else becomes '-'.</summary>
    public static string NormalizeId(string? id)
    {
        var text = (id ?? "").Trim().ToLowerInvariant();
        var chars = text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray();
        var normalized = new string(chars).Trim('-');
        return normalized.Length <= Objective.MaxIdLength ? normalized : normalized[..Objective.MaxIdLength];
    }
}
