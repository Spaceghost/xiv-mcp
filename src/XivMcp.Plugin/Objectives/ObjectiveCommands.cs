using XivMcp.Plugin.Providers.Objectives;

namespace XivMcp.Plugin.Objectives;

/// <summary>Parsed "/xivmcp quests ..." command.</summary>
public sealed record QuestCommand(string Verb, string? Argument);

/// <summary>Shared, game-free parts of the objective commands: pack loading and chat command parsing.</summary>
public static class ObjectiveCommands
{
    public const string Usage = "/xivmcp quests [list | done <id> | undo <id> | next <id> | flag <id> | load <file> | clear-done | remove <id> | show | hide]";

    private static readonly HashSet<string> Verbs =
        ["list", "done", "undo", "next", "flag", "load", "clear-done", "remove", "show", "hide"];

    /// <summary>
    /// Parses the text after "quests". Empty means list. Returns null with an error for unknown verbs or a missing argument.
    /// </summary>
    public static QuestCommand? Parse(string text, out string? error)
    {
        error = null;
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return new QuestCommand("list", null);
        var space = trimmed.IndexOf(' ');
        var verb = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        var argument = space < 0 ? null : trimmed[(space + 1)..].Trim();
        if (argument is { Length: 0 })
            argument = null;
        if (!Verbs.Contains(verb))
        {
            error = $"unknown quests command \"{verb}\". Use {Usage}.";
            return null;
        }

        if (verb is "done" or "undo" or "next" or "flag" or "load" or "remove" && argument is null)
        {
            error = $"\"quests {verb}\" needs {(verb == "load" ? "a file path" : "an objective id")}.";
            return null;
        }

        return new QuestCommand(verb, argument);
    }

    /// <summary>Loads a pack from JSON text or a file and upserts it into the store.</summary>
    public static LoadPackResult LoadPack(ObjectiveStore store, string? json, string? path)
    {
        string source;
        if (path is not null)
        {
            var resolved = ObjectivePack.ResolvePath(path, OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable("HOME"));
            if (!resolved.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Pack files must end in .json.");
            FileInfo file;
            try
            {
                file = new FileInfo(resolved);
                if (!file.Exists)
                    throw new ArgumentException($"Pack file not found: {path} (looked at {resolved}).");
                if (file.Length > ObjectivePack.MaxPackBytes)
                    throw new ArgumentException($"Pack file is larger than {ObjectivePack.MaxPackBytes / 1024} KiB.");
                json = File.ReadAllText(resolved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            {
                throw new ArgumentException($"Cannot read {path}: {ex.Message}");
            }

            source = "pack:" + file.Name;
        }
        else
        {
            source = "pack";
        }

        var result = ObjectivePack.Parse(json ?? "", source, store.Now);
        IReadOnlyList<Objective> stored;
        try
        {
            stored = store.Upsert(result.Objectives);
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(ex.Message);
        }

        return new LoadPackResult(result.Title, stored.Count, stored.Select(o => o.Id).ToList(), result.Errors.ToList(), store.Count);
    }
}
