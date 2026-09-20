using System.Text.Json;
using XivMcp.Core;

namespace XivMcp.Plugin.Services;

/// <summary>One line of the action log.</summary>
/// <param name="Time">When the call finished (UTC).</param>
/// <param name="Tool">Tool name.</param>
/// <param name="Tier">The tool's declared tier.</param>
/// <param name="Summary">What it did, in one sentence, with the arguments filled in.</param>
/// <param name="Client">Client-reported name (not authenticated).</param>
/// <param name="Token">Per-client token name the request authenticated with; null for the main token.</param>
/// <param name="Approval">"asked" (the approval switch was on: a prompt, grant, session or rule let it through), "ticket" (an approved ticket) or "off" (the switch was off).</param>
/// <param name="Success">False when the tool reported an error.</param>
/// <param name="Error">The error, when it failed.</param>
public sealed record ActionLogEntry(DateTimeOffset Time, string Tool, string Tier, string Summary, string? Client, string? Token, string Approval, bool Success, string? Error);

/// <summary>
/// The record of every state-changing call that ran: what, which client and token, when, and whether the player was
/// being asked at the time. Kept whether the approval switch is on or off, so switching it off never means "no trace".
/// An in-memory tail for the window plus an append-only JSON Lines file (<c>actions.log</c>, rotated once at 1 MiB to
/// <c>actions.log.1</c>). No Dalamud types; host-tested. Never throws: a log that cannot be written must not fail the call.
/// </summary>
public sealed class ActionLog
{
    public const int MemoryCapacity = 200;

    public const long MaxFileBytes = 1024 * 1024;

    public const int MaxSummaryLength = 300;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    private readonly object gate = new();
    private readonly LinkedList<ActionLogEntry> recent = new();
    private readonly string? path;
    private readonly TimeProvider time;
    private readonly Action<string, Exception>? onError;
    private bool failed;

    /// <param name="directory">Where actions.log lives; null keeps the log in memory only.</param>
    public ActionLog(string? directory, TimeProvider? time = null, Action<string, Exception>? onError = null)
    {
        path = directory is null ? null : System.IO.Path.Combine(directory, "actions.log");
        this.time = time ?? TimeProvider.System;
        this.onError = onError;
    }

    /// <summary>Raised (caller's thread) after an entry was added.</summary>
    public event Action<ActionLogEntry>? Recorded;

    public string? Path => path;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<ActionLogEntry> Recent()
    {
        lock (gate)
            return recent.ToArray();
    }

    public ActionLogEntry Record(GatedToolExecution execution, bool approvalOn)
    {
        var summary = execution.Summary.Length > MaxSummaryLength ? execution.Summary[..MaxSummaryLength] + "…" : execution.Summary;
        var error = execution.Error is { Length: > MaxSummaryLength } e ? e[..MaxSummaryLength] + "…" : execution.Error;
        var entry = new ActionLogEntry(
            time.GetUtcNow(), execution.ToolName, execution.Permission.ToString(), summary, execution.ClientName, execution.AuthenticatedClient,
            !approvalOn ? "off" : execution.PreApproved ? "ticket" : "asked", execution.Success, error);

        lock (gate)
        {
            recent.AddFirst(entry);
            while (recent.Count > MemoryCapacity)
                recent.RemoveLast();
            Append(entry);
        }

        try
        {
            Recorded?.Invoke(entry);
        }
        catch (Exception ex)
        {
            onError?.Invoke("an action log subscriber threw", ex);
        }

        return entry;
    }

    private void Append(ActionLogEntry entry)
    {
        if (path is null)
            return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxFileBytes)
                File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path, JsonSerializer.Serialize(entry, Json) + "\n");
            failed = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (!failed)
                onError?.Invoke("the action log could not be written", ex);
            failed = true;
        }
    }

    /// <summary>
    /// True for a call the player should be told about even though nobody asked them: it sent chat, or it changed gear
    /// (equip_gearset, or an execute_command line that is /gearset or /gs).
    /// </summary>
    public static bool DeservesToast(string toolName, ToolPermission effectiveTier, string? argumentsJson)
    {
        if (effectiveTier == ToolPermission.Chat)
            return true;
        if (toolName == "equip_gearset")
            return true;
        if (toolName != ConfirmationService.ExecuteCommandTool || ConfirmationService.CommandArgument(argumentsJson) is not { } command)
            return false;
        var verb = command.TrimStart().Split(' ', 2)[0];
        return verb.Equals("/gearset", StringComparison.OrdinalIgnoreCase) || verb.Equals("/gs", StringComparison.OrdinalIgnoreCase);
    }
}
