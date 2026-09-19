namespace XivMcp.Plugin.Objectives;

/// <summary>Unvalidated input for an objective (from post_objective, a pack or the player).</summary>
public sealed record ObjectiveDraft
{
    public string? Id { get; init; }

    public string? Title { get; init; }

    public IReadOnlyList<string> Steps { get; init; } = [];

    public string? Zone { get; init; }

    public uint? TerritoryId { get; init; }

    public float? MapX { get; init; }

    public float? MapY { get; init; }

    public string? Spot { get; init; }

    public string? Description { get; init; }

    public string? EorzeaTime { get; init; }

    public IReadOnlyList<string> Weather { get; init; } = [];

    public float? Radius { get; init; }

    public string? Source { get; init; }
}

/// <summary>Validation and progress rules for objectives. Pure; throws <see cref="ArgumentException"/> with a readable message.</summary>
public static class ObjectiveFactory
{
    public const float MinRadius = 1f;
    public const float MaxRadius = 1000f;
    public const int MaxWeatherNames = 16;

    public static Objective Create(ObjectiveDraft draft, DateTimeOffset now)
    {
        var id = ObjectiveText.NormalizeId(draft.Id);
        if (id.Length == 0)
            throw new ArgumentException("id must contain at least one letter or digit (e.g. \"hero-costa-night\").");

        var title = ObjectiveText.Clean(draft.Title, Objective.MaxTitleLength);
        if (title.Length == 0)
            throw new ArgumentException($"Objective '{id}': title must not be empty.");

        if (draft.Steps.Count > Objective.MaxSteps)
            throw new ArgumentException($"Objective '{id}': at most {Objective.MaxSteps} steps.");
        var steps = draft.Steps
            .Select(s => ObjectiveText.Clean(s, Objective.MaxStepLength))
            .Where(s => s.Length > 0)
            .Select(s => new ObjectiveStep(s, false))
            .ToArray();

        if ((draft.MapX is null) != (draft.MapY is null))
            throw new ArgumentException($"Objective '{id}': map coordinates need both x and y.");
        if (draft.MapX is { } x && draft.MapY is { } y && (!float.IsFinite(x) || !float.IsFinite(y) || x is < 0.5f or > 45f || y is < 0.5f or > 45f))
            throw new ArgumentException($"Objective '{id}': map coordinates ({x}, {y}) are outside the range the game shows (about 1 to 42).");
        if (draft.MapX is not null && draft.TerritoryId is not > 0)
            throw new ArgumentException($"Objective '{id}': map coordinates need territoryId (the TerritoryType row id of the zone).");
        if (draft.TerritoryId is 0)
            throw new ArgumentException($"Objective '{id}': territoryId 0 is not a zone; omit it instead.");

        if (!EorzeaTimeWindow.TryParse(draft.EorzeaTime, out var window, out var timeError))
            throw new ArgumentException($"Objective '{id}': {timeError}");

        if (draft.Weather.Count > MaxWeatherNames)
            throw new ArgumentException($"Objective '{id}': at most {MaxWeatherNames} weather names.");
        var weather = draft.Weather.Select(w => ObjectiveText.Clean(w, 64)).Where(w => w.Length > 0).ToArray();

        var radius = draft.Radius ?? Objective.DefaultRadius;
        if (!float.IsFinite(radius) || radius < MinRadius || radius > MaxRadius)
            throw new ArgumentException($"Objective '{id}': radius must be between {MinRadius} and {MaxRadius} yalms.");

        return new Objective
        {
            Id = id,
            Title = title,
            Steps = steps,
            Zone = ObjectiveText.CleanOrNull(draft.Zone, 100),
            TerritoryId = draft.TerritoryId,
            MapX = draft.MapX is { } mx ? MathF.Round(mx, 1) : null,
            MapY = draft.MapY is { } my ? MathF.Round(my, 1) : null,
            Spot = ObjectiveText.CleanOrNull(draft.Spot, Objective.MaxStepLength),
            Description = ObjectiveText.CleanOrNull(draft.Description, Objective.MaxTextLength),
            Conditions = new ObjectiveConditions
            {
                EorzeaTime = window.IsAny ? null : window.ToString(),
                Weather = weather,
                Radius = radius,
            },
            Source = ObjectiveText.CleanOrNull(draft.Source, 100),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Replacing an objective keeps its progress (done steps, completion, note, creation time) when its step texts are
    /// unchanged, so re-posting or reloading a pack does not reset the player's work.
    /// </summary>
    public static Objective Merge(Objective? previous, Objective replacement)
    {
        if (previous is null)
            return replacement;
        var sameSteps = previous.Steps.Select(s => s.Text).SequenceEqual(replacement.Steps.Select(s => s.Text));
        if (!sameSteps)
            return replacement with { CreatedAt = previous.CreatedAt };
        return replacement with
        {
            Steps = previous.Steps,
            Completed = previous.Completed,
            CompletedAt = previous.CompletedAt,
            Note = previous.Note,
            CreatedAt = previous.CreatedAt,
        };
    }

    /// <summary>Makes <paramref name="step"/> (0-based) the current step: every step before it is done, the rest are not.</summary>
    public static Objective SetCurrentStep(Objective objective, int step, DateTimeOffset now)
    {
        if (step < 0 || step > objective.Steps.Count)
            throw new ArgumentException($"step must be between 0 and {objective.Steps.Count} (the number of steps; {objective.Steps.Count} means all done).");
        var steps = objective.Steps.Select((s, i) => s with { Done = i < step }).ToArray();
        return WithSteps(objective, steps, now);
    }

    /// <summary>Marks the current step done. Completing the last step completes the objective.</summary>
    public static Objective Advance(Objective objective, DateTimeOffset now)
    {
        var current = objective.CurrentStepIndex;
        if (current < 0)
            return Complete(objective, true, now);
        return SetCurrentStep(objective, current + 1, now);
    }

    public static Objective Complete(Objective objective, bool completed, DateTimeOffset now)
    {
        if (completed == objective.Completed)
            return objective;
        return objective with
        {
            Completed = completed,
            CompletedAt = completed ? now : null,
            Steps = completed ? objective.Steps.Select(s => s with { Done = true }).ToArray() : objective.Steps,
            UpdatedAt = now,
        };
    }

    private static Objective WithSteps(Objective objective, ObjectiveStep[] steps, DateTimeOffset now)
    {
        var allDone = steps.Length > 0 && steps.All(s => s.Done);
        return objective with
        {
            Steps = steps,
            Completed = allDone,
            CompletedAt = allDone ? objective.CompletedAt ?? now : null,
            UpdatedAt = now,
        };
    }
}
