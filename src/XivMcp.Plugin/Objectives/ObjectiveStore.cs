using System.Text.Json;
using System.Text.Json.Serialization;

namespace XivMcp.Plugin.Objectives;

/// <summary>
/// The player's custom objectives, in insertion order, persisted as JSON (pluginConfigs/XivMcp/objectives.json in game).
/// Thread-safe: MCP tools write from the thread pool, the overlay and chat command from the framework thread.
/// </summary>
public sealed class ObjectiveStore
{
    public const int MaxObjectives = 200;
    public const int FileVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly string? path;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly List<Objective> items = [];

    public ObjectiveStore(string? path, TimeProvider? time = null)
    {
        this.path = path;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after any change, on the thread that made it. Handlers must not throw.</summary>
    public event Action? Changed;

    /// <summary>Raised when an objective becomes completed.</summary>
    public event Action<Objective>? Completed;

    /// <summary>The last load/save failure, or null.</summary>
    public string? LastError { get; private set; }

    public DateTimeOffset Now => time.GetUtcNow();

    public int Count
    {
        get
        {
            lock (gate)
                return items.Count;
        }
    }

    public IReadOnlyList<Objective> Snapshot()
    {
        lock (gate)
            return items.ToArray();
    }

    public Objective? Get(string id)
    {
        var key = ObjectiveText.NormalizeId(id);
        lock (gate)
            return items.FirstOrDefault(o => o.Id == key);
    }

    /// <summary>Adds or replaces (same id) objectives, keeping progress where the steps are unchanged (see <see cref="ObjectiveFactory.Merge"/>).</summary>
    public IReadOnlyList<Objective> Upsert(IEnumerable<Objective> objectives)
    {
        var stored = new List<Objective>();
        lock (gate)
        {
            foreach (var objective in objectives)
            {
                var index = items.FindIndex(o => o.Id == objective.Id);
                if (index < 0 && items.Count >= MaxObjectives)
                    throw new InvalidOperationException($"At most {MaxObjectives} objectives; clear completed ones first.");
                var merged = ObjectiveFactory.Merge(index >= 0 ? items[index] : null, objective);
                if (index >= 0)
                    items[index] = merged;
                else
                    items.Add(merged);
                stored.Add(merged);
            }

            SaveLocked();
        }

        Raise();
        return stored;
    }

    public Objective Upsert(Objective objective) => Upsert([objective])[0];

    /// <summary>Applies <paramref name="change"/> to one objective. Returns null when the id is unknown.</summary>
    public Objective? Update(string id, Func<Objective, Objective> change)
    {
        var key = ObjectiveText.NormalizeId(id);
        Objective? before;
        Objective updated;
        lock (gate)
        {
            var index = items.FindIndex(o => o.Id == key);
            if (index < 0)
                return null;
            before = items[index];
            updated = change(before);
            if (ReferenceEquals(updated, before))
                return before;
            items[index] = updated;
            SaveLocked();
        }

        Raise();
        if (updated.Completed && !before.Completed)
        {
            try
            {
                Completed?.Invoke(updated);
            }
            catch
            {
                // Notification plumbing must never fail an update.
            }
        }

        return updated;
    }

    /// <summary>Removes one objective by id, every completed one, or everything. Returns the number removed.</summary>
    public int Remove(string? id, bool completedOnly = false)
    {
        int removed;
        lock (gate)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                var key = ObjectiveText.NormalizeId(id);
                removed = items.RemoveAll(o => o.Id == key && (!completedOnly || o.Completed));
            }
            else
            {
                removed = items.RemoveAll(o => !completedOnly || o.Completed);
            }

            if (removed > 0)
                SaveLocked();
        }

        if (removed > 0)
            Raise();
        return removed;
    }

    /// <summary>Reads the file (missing file = empty). A damaged file is kept aside as .bad and the store starts empty.</summary>
    public void Load()
    {
        if (path is null)
            return;
        lock (gate)
        {
            items.Clear();
            LastError = null;
            if (!File.Exists(path))
                return;
            try
            {
                var file = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(path), Json);
                foreach (var objective in file?.Objectives ?? [])
                {
                    if (objective is null || string.IsNullOrEmpty(objective.Id) || items.Any(o => o.Id == objective.Id))
                        continue;
                    items.Add(objective with
                    {
                        Steps = objective.Steps ?? [],
                        Conditions = objective.Conditions ?? new ObjectiveConditions(),
                    });
                    if (items.Count >= MaxObjectives)
                        break;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LastError = $"Could not read {Path.GetFileName(path)}: {ex.Message}";
                items.Clear();
                try
                {
                    File.Copy(path, path + ".bad", overwrite: true);
                }
                catch
                {
                    // Keeping the damaged copy is best effort.
                }
            }
        }
    }

    private void SaveLocked()
    {
        if (path is null)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new StoreFile(FileVersion, items.ToList()), Json));
            File.Move(temp, path, overwrite: true);
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"Could not save {Path.GetFileName(path)}: {ex.Message}";
        }
    }

    private void Raise()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // Subscribers are UI/notification plumbing.
        }
    }

    private sealed record StoreFile(int Version, List<Objective>? Objectives);
}
