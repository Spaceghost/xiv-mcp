using System.Text.Json.Nodes;
using XivMcp.Core.Protocol;
using XivMcp.Core.Registry;

namespace XivMcp.Core;

/// <summary>
/// Host-driven tool execution for calls the player approved outside a tools/call request (the plugin's deferred
/// approval queue). Additive public API; nothing here changes how tools/call behaves.
/// </summary>
public sealed partial class McpServer
{
    /// <summary>
    /// Checks, without running anything, whether <paramref name="toolName"/> exists, its category and tier are enabled and
    /// <paramref name="arguments"/> bind. Returns null when the call could run now, otherwise the error text a client
    /// would get from tools/call. <paramref name="permission"/> is the tool's declared tier (Read when unknown).
    /// </summary>
    public string? CheckToolCall(string toolName, JsonObject? arguments, out ToolPermission permission)
    {
        permission = ToolPermission.Read;
        if (!_registry.Snapshot.ToolsByName.TryGetValue(toolName, out var tool))
            return UnknownToolMessage(toolName);
        permission = tool.Permission;
        if (ToolGateError(tool) is { } gateError)
            return gateError;
        var errors = new List<string>();
        ArgumentBinder.Bind(tool.Parameters, arguments, null, CancellationToken.None, errors);
        return errors.Count > 0 ? InvalidArgumentsMessage(tool, errors) : null;
    }

    /// <summary>
    /// Whether a call of <paramref name="toolName"/> goes through the approval gate and, if so, the sentence that tells the
    /// player what it will do. Null for an unknown tool.
    /// </summary>
    public ToolApprovalInfo? DescribeApproval(string toolName, JsonObject? arguments) =>
        _registry.Snapshot.ToolsByName.TryGetValue(toolName, out var tool)
            ? new ToolApprovalInfo(tool.NeedsApproval, tool.Permission, RenderApprovalSummary(tool.ApprovalSummary, toolName, arguments))
            : null;

    /// <summary>
    /// Runs a tool call the player has already approved. Category, tier, argument and login checks and
    /// <see cref="McpServerOptions.CallTimeout"/> apply exactly as for tools/call; <see cref="Approver"/> is not asked again.
    /// Works whether or not the listener is running. Does not record activity (see <see cref="RecordHostActivity"/>).
    /// Throws <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/> fires.
    /// </summary>
    public async Task<ToolExecutionResult> ExecuteApprovedToolAsync(string toolName, JsonObject? arguments, string? clientName, string? sessionId, CancellationToken cancellationToken = default, string? authenticatedClient = null)
    {
        var scope = new RequestScope
        {
            Era = Era.Modern,
            ProtocolVersion = ProtocolVersions.LatestModern,
            Method = "tools/call",
            Id = null,
            ClientName = clientName,
            DetachedSessionId = sessionId,
            AuthenticatedClient = authenticatedClient,
            CancellationToken = cancellationToken,
            Outbound = NullOutbound.Instance,
            Target = toolName,
        };

        JsonObject result;
        if (!_registry.Snapshot.ToolsByName.TryGetValue(toolName, out var tool))
        {
            result = ToolError(scope, UnknownToolMessage(toolName), McpErrorCodes.UnknownTool);
        }
        else
        {
            try
            {
                result = await CallToolAsync(scope, tool, arguments?.DeepClone() as JsonObject, preApproved: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogSink($"approved execution of '{toolName}' failed", ex);
                result = ToolError(scope, $"Tool '{toolName}' failed with an internal error ({ex.GetType().Name}: {ex.Message}).", McpErrorCodes.InternalError);
            }
        }

        return new ToolExecutionResult(result, scope.ToolError is not null, scope.ToolError);
    }

    /// <summary>
    /// Adds a host event (an executed ticket, a started or revoked approval session) to the activity feed and raises
    /// <see cref="ActivityRecorded"/>. Not counted as a request in <see cref="GetStatus"/>. Never pass tool arguments here.
    /// </summary>
    public void RecordHostActivity(string? sessionId, string? clientName, string method, string? target, bool success, string? error, double durationMs = 0)
    {
        if (error is { Length: > 500 })
            error = error[..500] + "…";
        var entry = new ActivityEntry(DateTimeOffset.UtcNow, sessionId, clientName, method, target, success, error, Math.Round(durationMs, 2));
        _activity.Add(entry);
        if (ActivityRecordedHasSubscribers)
            ThreadPool.UnsafeQueueUserWorkItem(static state => state.Server.SafeRaise(state.Entry), (Server: this, Entry: entry), preferLocal: false);
    }
}
