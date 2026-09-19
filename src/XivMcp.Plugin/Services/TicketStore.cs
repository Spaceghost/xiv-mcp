using System.Text.Json;
using System.Text.Json.Serialization;

namespace XivMcp.Plugin.Services;

/// <summary>
/// Persists approval tickets as JSON in the plugin config directory (next to XivMcp.json, which already holds the bearer
/// token, so ticket arguments gain no new exposure). Writes go to a temp file that replaces the old one. An unreadable file
/// is kept as *.unreadable-&lt;time&gt; and the queue starts empty rather than failing the plugin load.
/// </summary>
public sealed class TicketStore
{
    public const string FileName = "approval-tickets.json";
    private const int Version = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented = false,
    };

    private readonly Action<string, Exception?> log;

    public TicketStore(string path, Action<string, Exception?>? log = null)
    {
        Path = path;
        this.log = log ?? ((_, _) => { });
    }

    public string Path { get; }

    private sealed record FileShape(int Version, List<Ticket> Tickets);

    public IReadOnlyList<Ticket> Load()
    {
        try
        {
            if (!File.Exists(Path))
                return [];
            var text = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(text))
                return [];
            var shape = JsonSerializer.Deserialize<FileShape>(text, Json);
            return shape?.Tickets?.Where(t => t is { Id.Length: > 0, ToolName.Length: > 0 }).ToArray() ?? [];
        }
        catch (Exception ex)
        {
            log($"approval tickets in {Path} could not be read; starting with an empty queue", ex);
            try
            {
                File.Move(Path, $"{Path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}", overwrite: false);
            }
            catch
            {
                // The backup is a courtesy.
            }

            return [];
        }
    }

    public void Save(IReadOnlyList<Ticket> tickets)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new FileShape(Version, tickets.ToList()), Json));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex)
        {
            log($"approval tickets could not be saved to {Path}", ex);
        }
    }
}
