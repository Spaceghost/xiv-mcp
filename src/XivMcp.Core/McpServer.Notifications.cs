using System.Text.Json;
using System.Text.Json.Nodes;
using XivMcp.Core.Protocol;

namespace XivMcp.Core;

public sealed partial class McpServer
{
    /// <summary>
    /// Server-wide fan-out. Legacy sessions receive messages on their standalone (GET) stream, with
    /// history kept for Last-Event-ID resumption; modern clients receive only what their
    /// <c>subscriptions/listen</c> filter asked for. Never throws, never blocks.
    /// </summary>
    private sealed class ServerNotifier : IMcpNotifier
    {
        private readonly McpServer _server;

        public ServerNotifier(McpServer server) => _server = server;

        public void ResourceUpdated(string uri)
        {
            if (string.IsNullOrEmpty(uri))
                return;
            Guard(() =>
            {
                byte[]? legacy = null;
                foreach (var session in _server._sessions.Values)
                {
                    if (!session.Subscriptions.ContainsKey(uri))
                        continue;
                    legacy ??= JsonRpc.Serialize(JsonRpc.Notification("notifications/resources/updated", new JsonObject { ["uri"] = uri }));
                    session.Standalone.Post(legacy);
                }

                foreach (var listener in _server._listeners.Keys)
                {
                    if (listener.ResourceUris.Contains(uri))
                        listener.Send("notifications/resources/updated", new JsonObject { ["uri"] = uri });
                }
            });
        }

        public void ResourceListChanged() => ListChanged("notifications/resources/list_changed", static l => l.ResourcesListChanged);

        public void ToolListChanged() => ListChanged("notifications/tools/list_changed", static l => l.ToolsListChanged);

        public void PromptListChanged() => ListChanged("notifications/prompts/list_changed", static l => l.PromptsListChanged);

        private void ListChanged(string method, Func<ListenSubscription, bool> wants)
        {
            Guard(() =>
            {
                if (!_server._sessions.IsEmpty)
                {
                    var legacy = JsonRpc.Serialize(JsonRpc.Notification(method));
                    foreach (var session in _server._sessions.Values)
                        session.Standalone.Post(legacy);
                }

                foreach (var listener in _server._listeners.Keys)
                {
                    if (wants(listener))
                        listener.Send(method, null);
                }
            });
        }

        public void Log(McpLogLevel level, string logger, object? data)
        {
            // Modern (2026-07-28) log messages are request-scoped only; see CallNotifier.
            if (_server._sessions.IsEmpty)
                return;
            Guard(() =>
            {
                byte[]? json = null;
                foreach (var session in _server._sessions.Values)
                {
                    if (level < session.MinLogLevel)
                        continue;
                    json ??= JsonRpc.Serialize(BuildLogNotification(level, logger, data));
                    session.Standalone.Post(json);
                }
            });
        }

        private void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _server.LogSink("notification fan-out failed", ex);
            }
        }
    }

    /// <summary>
    /// The notifier handed to a tool/resource/prompt through <see cref="ToolContext"/>. Log messages go to
    /// the response stream of the request being served (when the client asked for them); everything else
    /// is forwarded to the server-wide notifier.
    /// </summary>
    private sealed class CallNotifier : IMcpNotifier
    {
        private readonly McpServer _server;
        private readonly RequestScope _scope;

        public CallNotifier(McpServer server, RequestScope scope)
        {
            _server = server;
            _scope = scope;
        }

        public void ResourceUpdated(string uri) => _server._notifier.ResourceUpdated(uri);

        public void ResourceListChanged() => _server._notifier.ResourceListChanged();

        public void ToolListChanged() => _server._notifier.ToolListChanged();

        public void PromptListChanged() => _server._notifier.PromptListChanged();

        public void Log(McpLogLevel level, string logger, object? data)
        {
            try
            {
                var min = _scope.Session is { } session ? session.MinLogLevel : _scope.LogLevel;
                if (min is null || level < min.Value)
                    return;
                _scope.Outbound.Notify(BuildLogNotification(level, logger, data));
            }
            catch (Exception ex)
            {
                _server.LogSink("request log notification failed", ex);
            }
        }
    }

    private static JsonObject BuildLogNotification(McpLogLevel level, string logger, object? data)
    {
        JsonNode? payload;
        try
        {
            payload = data switch
            {
                null => null,
                JsonNode node => node.DeepClone(),
                string s => JsonValue.Create(s),
                _ => JsonSerializer.SerializeToNode(data, data.GetType(), McpJson.Options),
            };
        }
        catch (Exception ex)
        {
            payload = JsonValue.Create($"<unserializable {data!.GetType().Name}: {ex.Message}>");
        }

        var p = new JsonObject { ["level"] = LogLevels.Name(level) };
        if (!string.IsNullOrEmpty(logger))
            p["logger"] = logger;
        p["data"] = payload;
        return JsonRpc.Notification("notifications/message", p);
    }
}
