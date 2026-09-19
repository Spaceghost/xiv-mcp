using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using XivMcp.Core.Protocol;
using XivMcp.Core.Registry;

namespace XivMcp.Core;

public sealed partial class McpServer
{
    private sealed class LoginRequiredException : Exception
    {
    }

    /// <summary>Runs one request. Returns (null, 0) when the request was cancelled and no response should be sent.</summary>
    private async Task<(JsonObject? Response, int HttpStatus)> DispatchAsync(RequestScope scope)
    {
        var started = Stopwatch.GetTimestamp();
        var success = true;
        string? error = null;
        try
        {
            var allowed = scope.Era == Era.Modern ? ModernMethods : LegacyMethods;
            if (!allowed.Contains(scope.Method))
            {
                var hint = scope.Era == Era.Legacy && ModernMethods.Contains(scope.Method)
                    ? $" ({scope.Method} exists only in protocol {ProtocolVersions.LatestModern})"
                    : scope.Era == Era.Modern && LegacyMethods.Contains(scope.Method)
                        ? $" ({scope.Method} was removed in {ProtocolVersions.LatestModern})"
                        : "";
                throw new McpProtocolException(JsonRpcCodes.MethodNotFound, $"Method not found: {scope.Method}{hint}", httpStatus: 404);
            }

            var (result, cacheable) = scope.Method switch
            {
                "ping" => (new JsonObject(), false),
                "server/discover" => (HandleDiscover(), true),
                "tools/list" => (HandleToolsList(scope), true),
                "tools/call" => (await HandleToolsCallAsync(scope).ConfigureAwait(false), false),
                "resources/list" => (HandleResourcesList(scope), true),
                "resources/templates/list" => (HandleTemplatesList(scope), true),
                "resources/read" => (await HandleResourcesReadAsync(scope).ConfigureAwait(false), true),
                "resources/subscribe" => (HandleSubscribe(scope, subscribe: true), false),
                "resources/unsubscribe" => (HandleSubscribe(scope, subscribe: false), false),
                "prompts/list" => (HandlePromptsList(scope), true),
                "prompts/get" => (await HandlePromptsGetAsync(scope).ConfigureAwait(false), false),
                "completion/complete" => (HandleComplete(scope), false),
                "logging/setLevel" => (HandleSetLevel(scope), false),
                _ => throw new McpProtocolException(JsonRpcCodes.MethodNotFound, $"Method not found: {scope.Method}", httpStatus: 404),
            };

            if (scope.ToolError is not null)
            {
                success = false;
                error = scope.ToolError;
            }

            if (scope.Era == Era.Modern)
                DecorateModernResult(result, scope.Method, cacheable);
            return (JsonRpc.Result(scope.Id, result), 200);
        }
        catch (McpProtocolException ex)
        {
            success = false;
            error = ex.Message;
            return (JsonRpc.Error(scope.Id, ex.Code, ex.Message, ex.Data2), scope.Era == Era.Modern ? ex.HttpStatus : 200);
        }
        catch (OperationCanceledException) when (scope.CancellationToken.IsCancellationRequested)
        {
            success = false;
            error = _serverCts.IsCancellationRequested ? "cancelled (server stopping)" : "cancelled by client";
            return (null, 0);
        }
        catch (Exception ex)
        {
            success = false;
            error = $"{ex.GetType().Name}: {ex.Message}";
            LogSink($"internal error handling {scope.Method}", ex);
            return (JsonRpc.Error(scope.Id, JsonRpcCodes.InternalError, "Internal error: " + ex.Message), 200);
        }
        finally
        {
            RecordActivity(scope.Session?.Id, scope.ClientName, scope.Method, scope.Target, success, error, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private void DecorateModernResult(JsonObject result, string method, bool cacheable)
    {
        result.Insert(0, "resultType", "complete");
        var meta = result["_meta"] as JsonObject;
        if (meta is null)
        {
            meta = new JsonObject();
            result["_meta"] = meta;
        }

        meta[MetaKeys.ServerInfo] = ServerInfoJson(ProtocolVersions.LatestModern);
        if (!cacheable)
            return;

        // Lists depend on the user's plugin configuration (permission tiers, categories): private, short TTL,
        // with listChanged notifications as the invalidation signal. Game state is never cached.
        result["ttlMs"] = method switch
        {
            "server/discover" => 60_000,
            "resources/read" => 0,
            _ => 10_000,
        };
        result["cacheScope"] = "private";
    }

    private JsonObject ServerInfoJson(string protocolVersion)
    {
        var info = new JsonObject { ["name"] = Options.ServerName };
        if (ProtocolVersions.HasStructuredOutput(protocolVersion) && !string.IsNullOrWhiteSpace(Options.ServerTitle))
            info["title"] = Options.ServerTitle;
        info["version"] = Options.ServerVersion;
        return info;
    }

    private static JsonObject CapabilitiesJson() => new()
    {
        ["logging"] = new JsonObject(),
        ["completions"] = new JsonObject(),
        ["prompts"] = new JsonObject { ["listChanged"] = true },
        ["resources"] = new JsonObject { ["subscribe"] = true, ["listChanged"] = true },
        ["tools"] = new JsonObject { ["listChanged"] = true },
    };

    private JsonObject HandleDiscover()
    {
        var result = new JsonObject
        {
            ["supportedVersions"] = new JsonArray(ProtocolVersions.All.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
            ["capabilities"] = CapabilitiesJson(),
        };
        if (!string.IsNullOrWhiteSpace(Options.Instructions))
            result["instructions"] = Options.Instructions;
        return result;
    }

    // ---- gating -------------------------------------------------------------------------------------

    private bool CategoryEnabled(string category)
    {
        try
        {
            return HostState.IsCategoryEnabled(category);
        }
        catch (Exception ex)
        {
            LogSink("IHostState.IsCategoryEnabled threw", ex);
            return false;
        }
    }

    private bool Permitted(ToolPermission permission)
    {
        try
        {
            return HostState.IsPermitted(permission);
        }
        catch (Exception ex)
        {
            LogSink("IHostState.IsPermitted threw", ex);
            return false;
        }
    }

    private bool IsToolVisible(ToolDescriptor tool) => CategoryEnabled(tool.Category) && Permitted(tool.Permission);

    /// <summary>Resources and resource templates expose game data, so they follow the Read tier as well as their category.</summary>
    private bool IsResourceVisible(MemberDescriptor member) => CategoryEnabled(member.Category) && Permitted(ToolPermission.Read);

    private static string PermissionLabel(ToolPermission p) => p switch
    {
        ToolPermission.Read => "Read",
        ToolPermission.Ui => "UI",
        ToolPermission.Action => "Action",
        ToolPermission.Chat => "Chat",
        _ => p.ToString(),
    };

    // ---- pagination ---------------------------------------------------------------------------------

    private int PageSize => Math.Clamp(Options.ListPageSize, 1, 10_000);

    private static int DecodeCursor(JsonObject? parameters)
    {
        if (parameters is null || !parameters.TryGetPropertyValue("cursor", out var node) || node is null)
            return 0;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.String)
        {
            var text = v.GetValue<string>();
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text));
                if (decoded.StartsWith("o:", StringComparison.Ordinal) &&
                    int.TryParse(decoded.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) && offset >= 0)
                    return offset;
            }
            catch (FormatException)
            {
            }
        }

        throw new McpProtocolException(JsonRpcCodes.InvalidParams, "Invalid cursor", httpStatus: 200);
    }

    private static string EncodeCursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes("o:" + offset.ToString(CultureInfo.InvariantCulture)));

    private JsonObject Page<T>(JsonObject? parameters, IReadOnlyList<T> items, string property, Func<T, JsonObject> project)
    {
        var offset = DecodeCursor(parameters);
        if (offset > items.Count)
            throw new McpProtocolException(JsonRpcCodes.InvalidParams, "Invalid cursor (the list changed; start again without a cursor)");
        var size = PageSize;
        var array = new JsonArray();
        for (var i = offset; i < items.Count && i < offset + size; i++)
            array.Add(project(items[i]));
        var result = new JsonObject { [property] = array };
        if (offset + size < items.Count)
            result["nextCursor"] = EncodeCursor(offset + size);
        return result;
    }

    // ---- tools --------------------------------------------------------------------------------------

    private JsonObject HandleToolsList(RequestScope scope)
    {
        var visible = _registry.Snapshot.Tools.Where(IsToolVisible).ToArray();
        return Page(scope.Params, visible, "tools", t => ToolJson(t, scope.ProtocolVersion));
    }

    private static JsonObject ToolJson(ToolDescriptor tool, string protocolVersion)
    {
        var structured = ProtocolVersions.HasStructuredOutput(protocolVersion);
        var json = new JsonObject { ["name"] = tool.Name };
        if (structured && !string.IsNullOrWhiteSpace(tool.Title))
            json["title"] = tool.Title;
        json["description"] = tool.Description;
        json["inputSchema"] = tool.InputSchema.DeepClone();
        if (structured && tool.OutputSchema is not null)
            json["outputSchema"] = tool.OutputSchema.DeepClone();

        var annotations = new JsonObject();
        if (!string.IsNullOrWhiteSpace(tool.Title))
            annotations["title"] = tool.Title;
        annotations["readOnlyHint"] = tool.Permission == ToolPermission.Read;
        annotations["destructiveHint"] = tool.Destructive;
        annotations["idempotentHint"] = tool.Idempotent;
        annotations["openWorldHint"] = tool.OpenWorld;
        json["annotations"] = annotations;
        json["_meta"] = new JsonObject
        {
            ["dev.xivmcp/category"] = tool.Category,
            ["dev.xivmcp/permission"] = JsonNamingPolicy.CamelCase.ConvertName(tool.Permission.ToString()),
            ["dev.xivmcp/requiresLogin"] = tool.RequiresLogin,
        };
        return json;
    }

    private ToolContext CreateContext(RequestScope scope, CancellationToken token) => new()
    {
        Game = GameThread,
        Notifier = new CallNotifier(this, scope),
        CancellationToken = token,
        SessionId = scope.Session?.Id ?? scope.DetachedSessionId,
        ClientName = scope.ClientName,
        AuthenticatedClient = scope.AuthenticatedClient,
        ProtocolVersion = scope.ProtocolVersion,
        ReportProgress = scope.ProgressToken is null
            ? static (_, _, _) => Task.CompletedTask
            : (progress, total, message) =>
            {
                try
                {
                    if (!double.IsFinite(progress) || !scope.TryAdvanceProgress(progress))
                        return Task.CompletedTask;
                    var p = new JsonObject { ["progressToken"] = scope.ProgressToken.DeepClone(), ["progress"] = progress };
                    if (total is { } t && double.IsFinite(t))
                        p["total"] = t;
                    if (!string.IsNullOrEmpty(message))
                        p["message"] = message;
                    scope.Outbound.Notify(JsonRpc.Notification("notifications/progress", p));
                }
                catch (Exception ex)
                {
                    LogSink("progress notification failed", ex);
                }

                return Task.CompletedTask;
            },
    };

    private JsonObject ToolError(RequestScope scope, string message)
    {
        scope.ToolError = message;
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
            ["isError"] = true,
        };
    }

    private async Task<JsonObject> HandleToolsCallAsync(RequestScope scope)
    {
        var name = JsonRpc.RequireString(scope.Params, "name");
        scope.Target = name;
        if (!_registry.Snapshot.ToolsByName.TryGetValue(name, out var tool))
            throw new McpProtocolException(JsonRpcCodes.InvalidParams, UnknownToolMessage(name), new JsonObject { ["name"] = name });

        JsonObject? arguments = null;
        if (scope.Params!.TryGetPropertyValue("arguments", out var argsNode) && argsNode is not null)
        {
            arguments = argsNode as JsonObject
                ?? throw new McpProtocolException(JsonRpcCodes.InvalidParams, "params.arguments must be an object");
        }

        return await CallToolAsync(scope, tool, arguments, preApproved: false).ConfigureAwait(false);
    }

    private static string UnknownToolMessage(string name) => $"Unknown tool: '{name}'. Call tools/list for the available tools.";

    /// <summary>Category and tier gate shared by tools/call, <see cref="CheckToolCall"/> and approved executions. Null when the tool may run.</summary>
    private string? ToolGateError(ToolDescriptor tool)
    {
        if (!CategoryEnabled(tool.Category))
            return $"Tool '{tool.Name}' is unavailable: the '{tool.Category}' tool category is disabled in the xiv-mcp plugin settings (/xivmcp). Ask the user to enable it.";
        if (!Permitted(tool.Permission))
            return $"Tool '{tool.Name}' is unavailable: it needs the '{PermissionLabel(tool.Permission)}' permission tier, which is disabled in the xiv-mcp plugin settings (/xivmcp). Ask the user to enable '{PermissionLabel(tool.Permission)}' tools.";
        return null;
    }

    private static string InvalidArgumentsMessage(ToolDescriptor tool, List<string> errors) =>
        $"Invalid arguments for tool '{tool.Name}': {string.Join("; ", errors)}. Expected arguments: {tool.Signature}.";

    /// <summary>
    /// Gates, binds, approves (unless <paramref name="preApproved"/>: the player already approved this exact call) and runs
    /// one tool call. Tool-level failures come back as isError results with <see cref="RequestScope.ToolError"/> set.
    /// </summary>
    private async Task<JsonObject> CallToolAsync(RequestScope scope, ToolDescriptor tool, JsonObject? arguments, bool preApproved)
    {
        var name = tool.Name;
        if (ToolGateError(tool) is { } gateError)
            return ToolError(scope, gateError);

        var timeout = Options.CallTimeout > TimeSpan.Zero ? Options.CallTimeout : TimeSpan.FromSeconds(30);

        // The call timeout starts only once the call may run: time spent waiting for in-game approval does not count.
        using var timeoutCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(scope.CancellationToken, timeoutCts.Token);
        var context = CreateContext(scope, linked.Token);

        var errors = new List<string>();
        var args = ArgumentBinder.Bind(tool.Parameters, arguments, context, linked.Token, errors);
        if (errors.Count > 0)
            return ToolError(scope, InvalidArgumentsMessage(tool, errors));

        if (!preApproved && tool.Permission >= ToolPermission.Action && Approver is { } approver)
        {
            var denial = await ApproveAsync(scope, tool, approver, arguments).ConfigureAwait(false);
            if (denial is not null)
                return denial;
        }

        timeoutCts.CancelAfter(timeout);

        object? value;
        try
        {
            value = await InvokeMemberAsync(tool, tool.GameThread, tool.RequiresLogin, args, linked.Token).ConfigureAwait(false);
        }
        catch (LoginRequiredException)
        {
            return ToolError(scope, $"Tool '{name}' requires a logged-in character, and no character is logged in right now. Ask the user to log in, then retry.");
        }
        catch (McpToolException ex)
        {
            return ToolError(scope, ex.Message);
        }
        catch (OperationCanceledException) when (scope.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return ToolError(scope, $"Tool '{name}' timed out after {timeout.TotalSeconds:0.#} s. The game may be busy (loading screen, cutscene); retry shortly.");
        }
        catch (Exception ex)
        {
            LogSink($"tool '{name}' threw", ex);
            return ToolError(scope, $"Tool '{name}' failed with an internal error ({ex.GetType().Name}: {ex.Message}).");
        }

        try
        {
            return BuildToolResult(scope, tool, value);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            LogSink($"tool '{name}' result could not be serialized", ex);
            return ToolError(scope, $"Tool '{name}' produced a result that could not be serialized ({ex.Message}).");
        }
    }

    /// <summary>
    /// Asks <see cref="Approver"/> whether an Action/Chat call may run. Returns null when approved, otherwise the
    /// isError result to send. Checks login first so the player is never asked about a call that cannot run.
    /// </summary>
    private async Task<JsonObject?> ApproveAsync(RequestScope scope, ToolDescriptor tool, IToolCallApprover approver, JsonObject? arguments)
    {
        var name = tool.Name;
        var approvalTimeout = Options.ApprovalTimeout > TimeSpan.Zero ? Options.ApprovalTimeout : TimeSpan.FromSeconds(30);
        using var approvalCts = new CancellationTokenSource(approvalTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(scope.CancellationToken, approvalCts.Token);
        var notConfirmed = $"Tool '{name}' was not run: it was not confirmed in game within {approvalTimeout.TotalSeconds:0.#} s. " +
                           "The player must click Allow in the xiv-mcp confirmation window; ask them before retrying.";

        try
        {
            if (tool.RequiresLogin)
            {
                var loggedIn = GameThread.IsOnGameThread
                    ? HostState.IsLoggedIn
                    : await GameThread.InvokeAsync(() => HostState.IsLoggedIn, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
                if (!loggedIn)
                    return ToolError(scope, $"Tool '{name}' requires a logged-in character, and no character is logged in right now. Ask the user to log in, then retry.");
            }

            var argumentsJson = arguments?.ToJsonString(McpJson.Options);
            var sessionId = scope.Session?.Id;
            var approved = await Task.Run(
                    () => approver is ISessionAwareToolCallApprover aware
                        ? aware.ApproveToolCallAsync(new ToolCallApprovalRequest(name, tool.Permission, scope.ClientName, sessionId, argumentsJson, scope.AuthenticatedClient), linked.Token)
                        : approver.ApproveToolCallAsync(name, tool.Permission, scope.ClientName, argumentsJson, linked.Token),
                    linked.Token)
                .WaitAsync(linked.Token)
                .ConfigureAwait(false);
            if (approved)
                return null;
            return approvalCts.IsCancellationRequested
                ? ToolError(scope, notConfirmed)
                : ToolError(scope, $"Tool '{name}' was not run: denied in game by the player. Do not retry unless the player asks you to.");
        }
        catch (OperationCanceledException) when (scope.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (approvalCts.IsCancellationRequested)
        {
            return ToolError(scope, notConfirmed);
        }
        catch (TimeoutException)
        {
            return ToolError(scope, notConfirmed);
        }
        catch (Exception ex)
        {
            LogSink($"approval for tool '{name}' failed", ex);
            return ToolError(scope, $"Tool '{name}' was not run: the in-game confirmation failed ({ex.GetType().Name}). Denied for safety.");
        }
    }

    /// <summary>Dispatches to the framework thread when requested, checks login there, applies the call timeout.</summary>
    private async Task<object?> InvokeMemberAsync(MemberDescriptor member, bool gameThread, bool requiresLogin, object?[] args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        object? returned;
        if (gameThread)
        {
            returned = await GameThread.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                if (requiresLogin && !HostState.IsLoggedIn)
                    throw new LoginRequiredException();
                return member.Invoke(args);
            }, token).WaitAsync(token).ConfigureAwait(false);
        }
        else
        {
            if (requiresLogin)
            {
                var loggedIn = GameThread.IsOnGameThread
                    ? HostState.IsLoggedIn
                    : await GameThread.InvokeAsync(() => HostState.IsLoggedIn, token).WaitAsync(token).ConfigureAwait(false);
                if (!loggedIn)
                    throw new LoginRequiredException();
            }

            returned = await Task.Run(() => member.Invoke(args), token).WaitAsync(token).ConfigureAwait(false);
        }

        return await member.CompleteAsync(returned).WaitAsync(token).ConfigureAwait(false);
    }

    private JsonObject BuildToolResult(RequestScope scope, ToolDescriptor tool, object? value)
    {
        var structuredAllowed = ProtocolVersions.HasStructuredOutput(scope.ProtocolVersion);

        if (tool.ReturnsToolResult)
        {
            var explicitResult = value as ToolResult ?? ToolResult.Text("");
            var json = new JsonObject { ["content"] = new JsonArray(explicitResult.Content.Select(c => (JsonNode?)ContentJson(c, scope.ProtocolVersion)).ToArray()) };
            if (structuredAllowed && explicitResult.StructuredContent is not null)
                json["structuredContent"] = explicitResult.StructuredContent.DeepClone();
            if (explicitResult.IsError)
            {
                json["isError"] = true;
                scope.ToolError = explicitResult.Content.FirstOrDefault(c => c.Text is not null)?.Text ?? "tool reported an error";
            }

            return json;
        }

        if (tool.ValueType == typeof(void))
        {
            return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "OK" }) };
        }

        var node = value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), McpJson.Options);
        JsonObject structured;
        if (tool.WrapResult)
        {
            structured = new JsonObject();
            if (node is not null)
                structured["result"] = node;
        }
        else if (node is JsonObject obj)
        {
            structured = obj;
        }
        else
        {
            return ToolError(scope, $"Tool '{tool.Name}' returned no data.");
        }

        var text = value is string s ? s : structured.ToJsonString(McpJson.Options);
        var result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        if (structuredAllowed)
            result["structuredContent"] = structured;
        return result;
    }

    private static JsonObject ContentJson(ContentBlock block, string protocolVersion)
    {
        switch (block.Type)
        {
            case "text":
                return new JsonObject { ["type"] = "text", ["text"] = block.Text ?? "" };
            case "image":
            case "audio":
                return new JsonObject { ["type"] = block.Type, ["data"] = block.Data ?? "", ["mimeType"] = block.MimeType ?? "application/octet-stream" };
            case "resource_link" when ProtocolVersions.HasStructuredOutput(protocolVersion):
            {
                var link = new JsonObject { ["type"] = "resource_link", ["uri"] = block.Uri ?? "", ["name"] = block.Name ?? block.Uri ?? "" };
                if (block.MimeType is not null)
                    link["mimeType"] = block.MimeType;
                if (block.Text is not null)
                    link["description"] = block.Text;
                return link;
            }

            case "resource_link":
                return new JsonObject { ["type"] = "text", ["text"] = $"{block.Name ?? block.Uri}: {block.Uri}" };
            case "resource":
            {
                var inner = new JsonObject { ["uri"] = block.Uri ?? "" };
                if (block.MimeType is not null)
                    inner["mimeType"] = block.MimeType;
                if (block.Data is not null)
                    inner["blob"] = block.Data;
                else
                    inner["text"] = block.Text ?? "";
                return new JsonObject { ["type"] = "resource", ["resource"] = inner };
            }

            default:
                return new JsonObject { ["type"] = "text", ["text"] = block.Text ?? "" };
        }
    }

    // ---- resources ----------------------------------------------------------------------------------

    private JsonObject HandleResourcesList(RequestScope scope)
    {
        var visible = _registry.Snapshot.Resources.Where(IsResourceVisible).ToArray();
        return Page(scope.Params, visible, "resources", r =>
        {
            var json = new JsonObject { ["uri"] = r.Uri, ["name"] = r.Name };
            if (!string.IsNullOrWhiteSpace(r.Description))
                json["description"] = r.Description;
            json["mimeType"] = r.MimeType;
            return json;
        });
    }

    private JsonObject HandleTemplatesList(RequestScope scope)
    {
        var visible = _registry.Snapshot.Templates.Where(IsResourceVisible).ToArray();
        return Page(scope.Params, visible, "resourceTemplates", r =>
        {
            var json = new JsonObject { ["uriTemplate"] = r.Template.Template, ["name"] = r.Name };
            if (!string.IsNullOrWhiteSpace(r.Description))
                json["description"] = r.Description;
            json["mimeType"] = r.MimeType;
            return json;
        });
    }

    private static McpProtocolException ResourceError(RequestScope scope, string uri, string message) =>
        new(scope.Era == Era.Legacy ? JsonRpcCodes.LegacyResourceNotFound : JsonRpcCodes.InvalidParams, message, new JsonObject { ["uri"] = uri });

    private async Task<JsonObject> HandleResourcesReadAsync(RequestScope scope)
    {
        var uri = JsonRpc.RequireString(scope.Params, "uri");
        scope.Target = uri;
        var snapshot = _registry.Snapshot;

        MemberDescriptor? member = null;
        string mimeType = "application/json";
        bool gameThread = true, requiresLogin = false;
        Dictionary<string, string>? variables = null;
        if (snapshot.ResourcesByUri.TryGetValue(uri, out var resource))
        {
            member = resource;
            mimeType = resource.MimeType;
            gameThread = resource.GameThread;
            requiresLogin = resource.RequiresLogin;
        }
        else
        {
            foreach (var template in snapshot.Templates)
            {
                if (template.Template.TryMatch(uri, out var values))
                {
                    member = template;
                    mimeType = template.MimeType;
                    gameThread = template.GameThread;
                    requiresLogin = template.RequiresLogin;
                    variables = values;
                    break;
                }
            }
        }

        if (member is null)
            throw ResourceError(scope, uri, $"Resource not found: {uri}");
        if (!CategoryEnabled(member.Category))
            throw ResourceError(scope, uri, $"Resource {uri} is unavailable: the '{member.Category}' category is disabled in the xiv-mcp plugin settings");
        if (!Permitted(ToolPermission.Read))
            throw ResourceError(scope, uri, $"Resource {uri} is unavailable: the 'Read' permission tier is disabled in the xiv-mcp plugin settings");

        var timeout = Options.CallTimeout > TimeSpan.Zero ? Options.CallTimeout : TimeSpan.FromSeconds(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(scope.CancellationToken, timeoutCts.Token);
        var context = CreateContext(scope, linked.Token);
        var errors = new List<string>();
        var args = variables is null
            ? ArgumentBinder.Bind(member.Parameters, null, context, linked.Token, errors)
            : ArgumentBinder.BindStrings(member.Parameters, variables, context, linked.Token, errors);
        if (errors.Count > 0)
            throw ResourceError(scope, uri, $"Invalid resource URI {uri}: {string.Join("; ", errors)}");

        object? value;
        try
        {
            value = await InvokeMemberAsync(member, gameThread, requiresLogin, args, linked.Token).ConfigureAwait(false);
        }
        catch (LoginRequiredException)
        {
            throw ResourceError(scope, uri, $"Resource {uri} requires a logged-in character; none is logged in");
        }
        catch (McpToolException ex)
        {
            throw ResourceError(scope, uri, ex.Message);
        }
        catch (OperationCanceledException) when (scope.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new McpProtocolException(JsonRpcCodes.InternalError, $"Reading {uri} timed out after {timeout.TotalSeconds:0.#} s");
        }
        catch (Exception ex)
        {
            LogSink($"resource '{uri}' threw", ex);
            throw new McpProtocolException(JsonRpcCodes.InternalError, $"Reading {uri} failed: {ex.GetType().Name}: {ex.Message}");
        }

        if (value is null)
            throw ResourceError(scope, uri, $"Resource not found: {uri}");

        var contents = new JsonObject { ["uri"] = uri };
        switch (value)
        {
            case string text:
                contents["mimeType"] = mimeType;
                contents["text"] = text;
                break;
            case byte[] bytes:
                contents["mimeType"] = mimeType;
                contents["blob"] = Convert.ToBase64String(bytes);
                break;
            default:
                contents["mimeType"] = mimeType;
                contents["text"] = JsonSerializer.Serialize(value, value.GetType(), McpJson.Options);
                break;
        }

        return new JsonObject { ["contents"] = new JsonArray(contents) };
    }

    private JsonObject HandleSubscribe(RequestScope scope, bool subscribe)
    {
        var uri = JsonRpc.RequireString(scope.Params, "uri");
        scope.Target = uri;
        if (subscribe)
            scope.Session!.Subscriptions.TryAdd(uri, 0);
        else
            scope.Session!.Subscriptions.TryRemove(uri, out _);
        return new JsonObject();
    }

    private JsonObject HandleSetLevel(RequestScope scope)
    {
        var level = JsonRpc.RequireString(scope.Params, "level");
        scope.Target = level;
        if (!LogLevels.TryParse(level, out var parsed))
            throw new McpProtocolException(JsonRpcCodes.InvalidParams, $"Invalid log level '{level}' (debug, info, notice, warning, error, critical, alert, emergency)");
        scope.Session!.MinLogLevel = parsed;
        return new JsonObject();
    }

    // ---- prompts ------------------------------------------------------------------------------------

    private JsonObject HandlePromptsList(RequestScope scope)
    {
        var visible = _registry.Snapshot.Prompts.Where(p => CategoryEnabled(p.Category)).ToArray();
        return Page(scope.Params, visible, "prompts", p =>
        {
            var json = new JsonObject { ["name"] = p.Name };
            if (ProtocolVersions.HasStructuredOutput(scope.ProtocolVersion) && !string.IsNullOrWhiteSpace(p.Title))
                json["title"] = p.Title;
            if (!string.IsNullOrWhiteSpace(p.Description))
                json["description"] = p.Description;
            var arguments = new JsonArray();
            foreach (var parameter in p.Parameters)
            {
                if (parameter.Kind != ParameterKind.Argument)
                    continue;
                var arg = new JsonObject { ["name"] = parameter.JsonName };
                var description = parameter.Attribute?.Description;
                if (parameter.EnumValues is { Length: > 0 } values)
                    description = (string.IsNullOrWhiteSpace(description) ? "" : description + " ") + "One of: " + string.Join(", ", values) + ".";
                if (!string.IsNullOrWhiteSpace(description))
                    arg["description"] = description;
                arg["required"] = parameter.IsRequired;
                arguments.Add(arg);
            }

            json["arguments"] = arguments;
            return json;
        });
    }

    private async Task<JsonObject> HandlePromptsGetAsync(RequestScope scope)
    {
        var name = JsonRpc.RequireString(scope.Params, "name");
        scope.Target = name;
        if (!_registry.Snapshot.PromptsByName.TryGetValue(name, out var prompt) || !CategoryEnabled(prompt.Category))
            throw new McpProtocolException(JsonRpcCodes.InvalidParams, $"Unknown prompt: '{name}'", new JsonObject { ["name"] = name });

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (JsonRpc.GetObject(scope.Params, "arguments") is { } arguments)
        {
            foreach (var kv in arguments)
            {
                if (kv.Value is null)
                    continue;
                values[kv.Key] = kv.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : kv.Value.ToJsonString();
            }
        }

        var timeout = Options.CallTimeout > TimeSpan.Zero ? Options.CallTimeout : TimeSpan.FromSeconds(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(scope.CancellationToken, timeoutCts.Token);
        var context = CreateContext(scope, linked.Token);
        var errors = new List<string>();
        var args = ArgumentBinder.BindStrings(prompt.Parameters, values, context, linked.Token, errors);
        if (errors.Count > 0)
            throw new McpProtocolException(JsonRpcCodes.InvalidParams, $"Invalid arguments for prompt '{name}': {string.Join("; ", errors)}");

        object? value;
        try
        {
            value = await InvokeMemberAsync(prompt, gameThread: false, requiresLogin: false, args, linked.Token).ConfigureAwait(false);
        }
        catch (McpToolException ex)
        {
            throw new McpProtocolException(JsonRpcCodes.InvalidParams, ex.Message);
        }
        catch (OperationCanceledException) when (scope.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new McpProtocolException(JsonRpcCodes.InternalError, $"Prompt '{name}' timed out after {timeout.TotalSeconds:0.#} s");
        }
        catch (Exception ex)
        {
            LogSink($"prompt '{name}' threw", ex);
            throw new McpProtocolException(JsonRpcCodes.InternalError, $"Prompt '{name}' failed: {ex.GetType().Name}: {ex.Message}");
        }

        var result = new JsonObject();
        var messages = new JsonArray();
        switch (value)
        {
            case PromptResult pr:
                if (!string.IsNullOrWhiteSpace(pr.Description))
                    result["description"] = pr.Description;
                foreach (var m in pr.Messages)
                    messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = ContentJson(m.Content, scope.ProtocolVersion) });
                break;
            case string text:
                if (!string.IsNullOrWhiteSpace(prompt.Description))
                    result["description"] = prompt.Description;
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } });
                break;
        }

        result["messages"] = messages;
        return result;
    }

    // ---- completion ---------------------------------------------------------------------------------

    private JsonObject HandleComplete(RequestScope scope)
    {
        var reference = JsonRpc.GetObject(scope.Params, "ref")
            ?? throw new McpProtocolException(JsonRpcCodes.InvalidParams, "completion/complete requires params.ref");
        var argument = JsonRpc.GetObject(scope.Params, "argument")
            ?? throw new McpProtocolException(JsonRpcCodes.InvalidParams, "completion/complete requires params.argument");
        var argumentName = JsonRpc.RequireString(argument, "name");
        var argumentValue = JsonRpc.GetString(argument, "value") ?? "";
        var type = JsonRpc.RequireString(reference, "type");
        var snapshot = _registry.Snapshot;

        IReadOnlyList<ParameterBinding> parameters;
        switch (type)
        {
            case "ref/prompt":
            {
                var name = JsonRpc.RequireString(reference, "name");
                scope.Target = name + "." + argumentName;
                if (!snapshot.PromptsByName.TryGetValue(name, out var prompt) || !CategoryEnabled(prompt.Category))
                    throw new McpProtocolException(JsonRpcCodes.InvalidParams, $"Unknown prompt: '{name}'");
                parameters = prompt.Parameters;
                break;
            }

            case "ref/resource":
            {
                var uri = JsonRpc.RequireString(reference, "uri");
                scope.Target = uri + "." + argumentName;
                var template = snapshot.Templates.FirstOrDefault(t => t.Template.Template == uri && IsResourceVisible(t));
                if (template is null)
                {
                    if (snapshot.ResourcesByUri.TryGetValue(uri, out var fixedResource) && IsResourceVisible(fixedResource))
                        return CompletionJson([], argumentValue);
                    throw new McpProtocolException(JsonRpcCodes.InvalidParams, $"Unknown resource template: '{uri}'");
                }

                parameters = template.Parameters;
                break;
            }

            default:
                throw new McpProtocolException(JsonRpcCodes.InvalidParams, $"Unknown completion reference type '{type}' (expected ref/prompt or ref/resource)");
        }

        var parameter = parameters.FirstOrDefault(p => p.Kind == ParameterKind.Argument && string.Equals(p.JsonName, argumentName, StringComparison.OrdinalIgnoreCase));
        string[] candidates = parameter switch
        {
            null => [],
            { EnumValues: { Length: > 0 } values } => values,
            _ when parameter.ValueType == typeof(bool) => ["true", "false"],
            _ => [],
        };
        return CompletionJson(candidates, argumentValue);
    }

    private static JsonObject CompletionJson(string[] candidates, string prefix)
    {
        var matches = candidates.Where(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Concat(candidates.Where(c => !c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && c.Contains(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var values = new JsonArray(matches.Take(100).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        return new JsonObject
        {
            ["completion"] = new JsonObject
            {
                ["values"] = values,
                ["total"] = matches.Length,
                ["hasMore"] = matches.Length > 100,
            },
        };
    }
}
