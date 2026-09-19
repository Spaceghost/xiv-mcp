# xiv-mcp protocol implementation

What `XivMcp.Core` implements, checked against the MCP specification sources in
`modelcontextprotocol/modelcontextprotocol` (`schema/<rev>/schema.ts` plus `docs/specification/<rev>/`)
as of 2026-09-16. The latest published revision on that date is **2026-07-28**, a breaking,
stateless redesign. The server is **dual-era**: it serves 2026-07-28 statelessly and serves the
session-based revisions 2025-11-25, 2025-06-18 and 2025-03-26 on the same endpoint.

The Core has no third-party runtime dependencies (BCL only: `TcpListener`/`Socket`,
`System.Text.Json`, `System.Threading.Channels`). It does not use `HttpListener`/http.sys or ASP.NET Core.

Evidence standard: everything below is covered by `tests/XivMcp.Core.Tests` (163 xunit tests),
`tools/conformance/run.py` against `src/XivMcp.DevHost`, and MCP Inspector CLI 2.7.0 runs in the
`legacy`, `auto` and `modern` protocol eras (direct HTTP and through `tools/stdio-bridge`). None of it was
observed inside the game under Wine; see [Not verified](#not-verified).

## Revisions and era selection

| Revision | Era | How a client selects it |
| --- | --- | --- |
| 2026-07-28 | modern (stateless) | Request body carries `params._meta["io.modelcontextprotocol/protocolVersion"] = "2026-07-28"` |
| 2025-11-25 | legacy (session) | `initialize` with that `protocolVersion` |
| 2025-06-18 | legacy (session) | `initialize` with that `protocolVersion` |
| 2025-03-26 | legacy (session) | `initialize` with that `protocolVersion` |

Per POST, in this order:

1. JSON array body: a JSON-RPC batch; accepted only inside a 2025-03-26 session.
2. `initialize` request: legacy handshake. Any other requested version (2024-11-05, 2026-07-28, unknown)
   negotiates to `2025-11-25`, the latest session-based revision.
3. `Mcp-Session-Id` header naming a live session: legacy semantics for that session. A 2026-07-28 request
   that carries an unknown session id is served statelessly and the header is ignored.
4. Otherwise, a request with modern `_meta`, or with a non-legacy `MCP-Protocol-Version` header, is
   handled as 2026-07-28.
5. Anything else is `400` with JSON-RPC `-32600` ("missing Mcp-Session-Id … or send `_meta`").

`server/discover` and `-32022` errors list `supportedVersions`/`supported` as
`["2026-07-28","2025-11-25","2025-06-18","2025-03-26"]`. A per-request `_meta` naming a legacy
version is rejected with `-32022`, because those versions require `initialize`.

## HTTP transport

One endpoint, `McpServerOptions.Path` (default `/mcp`; a trailing slash is tolerated). Other paths → `404`.

### Request handling

- HTTP/1.1 and HTTP/1.0, parsed with RFC 9112 rules: request line, header fields, `Content-Length`,
  `Transfer-Encoding: chunked` bodies with extensions and trailers, and `Expect: 100-continue`.
  Absolute-form targets are accepted.
- Rejected as ambiguous or unsafe: `400` for both TE and CL, conflicting CL values, a non-numeric CL,
  obs-fold, whitespace before the colon, control characters in values, a missing or duplicate `Host`
  on 1.1, or TE on 1.0. `501` for transfer-codings other than `chunked`, `505` for HTTP versions other
  than 1.x, `417` for unknown expectations.
- Limits: 16 KiB of headers and 100 fields (`431`), and a body of `MaxRequestBytes` (default 4 MiB,
  `413`). Beyond 64 concurrent connections, new ones get `503` and are closed.
- Slowloris guards: headers must complete within 10 s of the first byte (`408`), the body within 30 s
  (`408`). A new connection must send something within 10 s. Idle keep-alive connections are closed
  after 45 s; responses advertise `Keep-Alive: timeout=30` so well-behaved clients close first. A
  socket write that stalls for 15 s aborts the connection.
- Keep-alive and pipelining: requests on one connection are processed strictly in order, so pipelined
  requests are answered in order. A background pump keeps reading the socket, so a disconnect is seen
  while a request is running.
- `Connection: close` or HTTP/1.0: after the response the server waits up to 2 s for the client to
  close first, then resets.

### Security gates (in this order)

1. **Host** (DNS rebinding): when bound to a loopback address, a `Host` header whose name is not
   loopback (`localhost`, `*.localhost`, `127.0.0.0/8`, `::1`) or a host from `AllowedOrigins` → `403`.
2. **Origin**: if present, it must be `http(s)://` with a loopback host (any port) or exactly match an
   `AllowedOrigins` entry (scheme://host[:port], case-insensitive, trailing slash ignored). `Origin: null`
   is rejected. A failure is `403` with a JSON-RPC error body without an `id`. An allowed Origin gets
   `Access-Control-Allow-Origin: <origin>`, `Vary: Origin`, and
   `Access-Control-Expose-Headers: Mcp-Session-Id, Mcp-Protocol-Version, WWW-Authenticate`.
3. **CORS preflight**: `OPTIONS` → `204` with `Allow-Methods: GET, POST, DELETE, OPTIONS`, the requested
   headers echoed back, and `Max-Age: 600`. No token is required.
4. **Bearer token** (when `BearerToken` is non-empty): `Authorization: Bearer <token>`, scheme
   case-insensitive. The comparison is constant-time over SHA-256 digests. A missing token gets `401`
   with `WWW-Authenticate: Bearer realm="xiv-mcp"`. A wrong token gets `401` with
   `…, error="invalid_token"`. OAuth discovery (`/.well-known/*`) is not implemented, so clients must
   be configured with a static header. `McpServerOptions.ClientTokens` adds per-client tokens
   (name + SHA-256 hex, read on every request): a request presenting one is authorized the same way and
   carries the name as `ToolContext.AuthenticatedClient` / `ToolCallApprovalRequest.AuthenticatedClient`.
   A client token is also recognized when `BearerToken` is empty.

Rejections at gates 1, 2 and 4 are recorded in the activity feed as failed `http <METHOD>` entries.

### Methods and status codes

| Request | Result |
| --- | --- |
| POST, `Content-Type` not `application/json` | `415` |
| POST, `Accept` admits neither `application/json` nor `text/event-stream` (a missing `Accept` admits both) | `406` |
| POST, invalid JSON | `400`, `-32700`, no id |
| POST, a string or property name with an unpaired UTF-16 surrogate escape (`"\ud800"`) | `400`, `-32700`, no id |
| POST, not a JSON-RPC message (wrong `jsonrpc`, bad id type, …) | `400`, `-32600` |
| POST notification or client response in a legacy session | `202`, empty body |
| POST notification, no session | `202` (ignored) |
| POST client response, no session | `400` |
| POST request | `200`, `application/json`, or `text/event-stream` (see below) |
| GET with session | `200 text/event-stream` (legacy standalone stream) |
| GET without `Mcp-Session-Id` | `400`; 2026-07-28 clients use `subscriptions/listen` |
| GET, `Accept` lacks `text/event-stream` | `406` |
| DELETE with session | `200`; the session, its streams and in-flight calls end |
| DELETE unknown session / no header | `404` / `400` |
| Unknown session id (POST/GET) | `404`, JSON-RPC `-32001` "Session not found" |
| Other HTTP methods | `405` with `Allow` |

**JSON vs SSE per request.** A response starts as `application/json`. The first time a
request-scoped notification (`notifications/progress` or `notifications/message`) has to go out before
the result, and the client accepts `text/event-stream`, the response switches to SSE: headers
`Cache-Control: no-cache, no-transform` and `X-Accel-Buffering: no`, events `event: message` +
`data: <json>`, final JSON-RPC response, end of stream. If the client accepts only JSON, those
notifications are dropped. If the client accepts only SSE, the response is always a stream.

**SSE streams** carry `: keepalive` comments every `SseKeepAliveInterval` (default 15 s). Each stream
has a bounded queue of 1024 frames. A consumer that falls behind is disconnected rather than allowed
to block producers.

**Shutdown** (`StopAsync`, also used for hot reload): the listening socket is closed first, so the
port is free immediately. Then in-flight calls are cancelled, each `subscriptions/listen` stream gets
its graceful completion result, and sessions are closed. Queued frames get up to 300 ms to flush,
then every remaining connection is reset (linger 0, no server-side `TIME_WAIT`). The server waits up
to 2 s for handlers. On non-Windows hosts the listener sets `SO_REUSEADDR`. `StartAsync` retries a
busy port for up to 3 s before throwing. `Start`/`Stop` can be called repeatedly.

## 2026-07-28 (modern, stateless)

Every request must carry `_meta["io.modelcontextprotocol/protocolVersion"]` and
`_meta["io.modelcontextprotocol/clientCapabilities"]` (an object). `clientInfo` is optional and is
used only for the activity feed and status. Checks, in order:

| Check | Failure |
| --- | --- |
| `protocolVersion` present | `400`, `-32602` |
| `protocolVersion` is `2026-07-28` | `400`, `-32022`, `data: {supported, requested}` |
| `MCP-Protocol-Version` header present and equal to `_meta` | `400`, `-32020` (HeaderMismatch) |
| `clientCapabilities` is an object | `400`, `-32602` |
| `io.modelcontextprotocol/logLevel`, if present, is a valid level | `400`, `-32602` |
| `Mcp-Method` header present and equal to `method` | `400`, `-32020` |
| method is implemented | `404`, `-32601` (also `ping`, `initialize`, `logging/setLevel`, `resources/(un)subscribe`) |
| `Mcp-Name` present for `tools/call`/`prompts/get` (`params.name`) and `resources/read` (`params.uri`), after `=?base64?…?=` decoding, equal to the body value | `400`, `-32020` |

No `Mcp-Session-Id` is issued. The server declares no `x-mcp-header` parameters, so no
`Mcp-Param-*` validation applies. `MissingRequiredClientCapability` (`-32021`) is never needed: the
server never asks the client for sampling, elicitation or roots.

Methods: `server/discover`, `tools/list`, `tools/call`, `resources/list`,
`resources/templates/list`, `resources/read`, `prompts/list`, `prompts/get`,
`completion/complete`, `subscriptions/listen`.

Every result carries `resultType: "complete"` and `_meta["io.modelcontextprotocol/serverInfo"]`
(`name`, `title`, `version`). Cacheable results carry `cacheScope: "private"`, because lists depend on
the user's permission and category settings and reads return live game state, and a `ttlMs` of:
`server/discover` 60000; `tools/list`, `resources/list`, `resources/templates/list` and
`prompts/list` 10000; `resources/read` 0.

**Cancellation** means closing the HTTP connection. The call's `CancellationToken` fires and no response
is sent. `notifications/cancelled` POSTed over HTTP is accepted (`202`) and ignored, because request
ids are not unique across stateless clients.

**Logging** is request-scoped. `notifications/message` is emitted only on the response stream of a
request whose `_meta` sets `io.modelcontextprotocol/logLevel`, and only at or above that level. It
comes from `ToolContext.Notifier.Log` during that call. Server-wide `IMcpNotifier.Log` calls never
reach modern clients.

**Progress**: `_meta.progressToken` (string or integer) → `ToolContext.ReportProgress` emits
`notifications/progress {progressToken, progress, total?, message?}`. Non-increasing values are dropped.

**`subscriptions/listen`** requires `Accept` to include `text/event-stream` (`406` otherwise) and
`params.notifications` (`-32602` otherwise). The response is a long-lived SSE stream:

1. `notifications/subscriptions/acknowledged` with `_meta.subscriptionId` equal to the request id,
   echoing the honored subset of `toolsListChanged`, `promptsListChanged`, `resourcesListChanged` and
   `resourceSubscriptions` (all supported; resource URIs are accepted as given).
2. Only the requested `notifications/{tools,prompts,resources}/list_changed` and
   `notifications/resources/updated`, each tagged with `_meta.subscriptionId`.
3. On server stop: the result `{resultType:"complete", _meta:{subscriptionId, serverInfo}}`, then the
   stream ends. A client disconnect ends the subscription.

Not implemented, because nothing here needs it: multi round-trip requests (`InputRequiredResult`), the
tasks extension, icons, and the `extensions` capability. No OpenTelemetry `_meta` propagation either.

## 2025-11-25, 2025-06-18, 2025-03-26 (legacy, sessions)

- `initialize` → `InitializeResult {protocolVersion, capabilities, serverInfo{name, title*, version}, instructions?}`
  with an `Mcp-Session-Id` header (32 lowercase hex characters from a CSPRNG). `initialize` is always
  answered, even while a session header is present, and creates a new session. `*`: `title` is sent
  for 2025-06-18 and later.
- `notifications/initialized` marks the session initialized. Requests before it are not rejected.
- `MCP-Protocol-Version`: optional (absent = the session's version). If present it must equal the
  negotiated version, else `400`.
- Methods: `ping`, `tools/list`, `tools/call`, `resources/list`, `resources/templates/list`,
  `resources/read`, `resources/subscribe`, `resources/unsubscribe`, `prompts/list`, `prompts/get`,
  `completion/complete`, `logging/setLevel`. Anything else, including `server/discover`, is `-32601`
  with HTTP `200`. Results carry no `resultType`, `ttlMs` or `cacheScope`.
- **Batches**: 2025-03-26 sessions only. Requests run concurrently and the reply is a JSON array
  (never SSE). Invalid members get `-32600` with no id. An `initialize` in a batch is rejected. A batch
  of only notifications or responses → `202`. Any other version, or no session → `400`.
- **Cancellation**: `notifications/cancelled {requestId}` cancels that in-flight request's token. No
  response is sent: an SSE response simply ends, a JSON-only client gets `-32603` "Request cancelled".
  A disconnect does **not** cancel a legacy request; the result stays retrievable for resumption.
  Request ids must be unique among the session's in-flight requests (`-32600` otherwise).
- **Standalone GET stream**: one per session. A newer GET replaces the older one, whose response ends.
  It carries `notifications/resources/updated` for `resources/subscribe`d URIs, all `list_changed`
  notifications, and server-wide `IMcpNotifier.Log` at or above the session level. The level is set by
  `logging/setLevel` and defaults to `info`.
- **Resumability**: every event on a session stream has id `<streamId>_<seq>`. `g` is the standalone
  stream; `p<n>` is a POST response stream. Each stream keeps its last 256 events or 1 MiB. GET with
  `Last-Event-ID` replays what came after that event on *that* stream, then continues live (POST
  streams end after their response). Completed POST streams are kept 5 minutes, at most 32 per session.
  Unknown ids open a fresh standalone stream.
- **Priming event**: 2025-11-25 sessions get an `id` with empty `data` at the start of every SSE
  stream. Older versions do not.
- **Sessions** expire after `SessionIdleTimeout` (default 30 min) with no requests, open streams or
  in-flight calls. At most 256 sessions exist; beyond that the least recently active session without
  open streams is evicted. `DELETE` ends a session immediately.
- **Version differences**: 2025-03-26 gets no tool `title`, no `outputSchema`, no `structuredContent`,
  no prompt `title`, and `resource_link` blocks are downgraded to text. Resource not found is `-32002`
  (2026-07-28 uses `-32602`).
- The server never sends JSON-RPC requests (no sampling, elicitation, roots or ping).

Capabilities, identical in both eras:
`{"logging":{}, "completions":{}, "prompts":{"listChanged":true}, "resources":{"subscribe":true,"listChanged":true}, "tools":{"listChanged":true}}`.

## Provider surface → MCP

Providers are discovered from `[McpProvider(category)]` classes, whose public instance and static
methods carry `[McpTool]`, `[McpResource]`, `[McpResourceTemplate]` or `[McpPrompt]`. Registration of
a provider is atomic. It throws `ArgumentException` for: duplicate tool names, URIs, templates or
prompt names; tool names that do not match `^[A-Za-z0-9_.-]{1,128}$`; more than one MCP attribute on a
method; generic or ref/out methods; resource methods that take arguments; template parameters that
are not template variables; prompts that return anything but `string`/`PromptResult`.
`RegisterProviders` registers every provider it can and then throws one `AggregateException` listing
the failures. Registering while the server is running emits `list_changed`. Lists are sorted by name,
URI or template (deterministic).

### Tools

- `tools/list` items: `name`, `title`, `description`, `inputSchema`, `outputSchema`,
  `annotations{title?, readOnlyHint = (Permission == Read), destructiveHint, idempotentHint, openWorldHint}`,
  `_meta{"dev.xivmcp/category", "dev.xivmcp/permission", "dev.xivmcp/requiresLogin"}`.
- Hidden from `tools/list` when `IHostState.IsCategoryEnabled(category)` or `IsPermitted(permission)`
  is false. Calling a hidden tool returns an `isError` result that names the disabled category or
  permission tier and says to enable it in the xiv-mcp plugin settings (`/xivmcp`).
- The server polls those gates every 2 s while any session or listener exists, and sends all three
  `list_changed` notifications when they change.
- Pagination: opaque base64 offset cursors, `ListPageSize` items per page (default 250). A bad or
  stale cursor → `-32602`.
- Unknown tool or missing `name` → JSON-RPC `-32602`. `arguments` that is not an object → `-32602`.
- **Input schema** (no `$schema` keyword, draft 2020-12 vocabulary), `type: object`,
  `additionalProperties: false`, one property per C# parameter named in camelCase.
  `ToolContext`/`CancellationToken` are injected and not listed.
  - `string` → string; `char` → string of length 1; `bool` → boolean.
  - Integers → integer. `byte`/`sbyte`/`short`/`ushort`/`uint` get their range as
    `minimum`/`maximum`, and `ulong` gets `minimum: 0`. `int` and `long` stay unbounded to keep schemas
    readable.
  - `float`/`double`/`decimal` → number.
  - Enums → `{type: string, enum: [camelCase names]}`, honoring `[JsonStringEnumMemberName]`.
  - `Guid` → uuid; `DateTime(Offset)` → date-time; `DateOnly` → date; `TimeOnly` → time; `Uri` → uri;
    `byte[]` → base64 string; `JsonObject` → object; `JsonNode`/`JsonElement`/`object` →
    `{"description":"Any JSON value.","anyOf":[{"type":"object",…},{"type":"array"},{"type":"string"},{"type":"number"},{"type":"boolean"},{"type":"null"}]}`
    (never a bare `{}`).
  - Arrays and `IEnumerable<T>` → `{type: array, items}`; dictionaries → object with
    `additionalProperties`.
  - Records and classes → object with properties from the `McpJson.Options` contract and
    `[Description]` text. A type that refers back to itself (directly, through another record, or through a
    collection) is emitted once under the root schema's `$defs` (key: namespace-qualified type name) and referenced as
    `{"$ref":"#/$defs/<name>"}`; a recursive parameter or output root is also inlined at its position. Nesting deeper
    than 12 levels is described as `{"type":"object"|"array","description":"Nested too deeply…"}`.
  - MCP Inspector CLI 2.7.0 `--method tools/list --strict` reports 0 errors and 0 warnings for the DevHost and for the
    plugin's real tool list served by `tools/catalog serve` (list only; nothing callable).
  - `[McpParam]` adds `description`, `enum` (for arrays, applied to items), and
    `minimum`/`maximum`, which override type ranges. A C# default becomes `default`.
  - Required = no default value and not nullable (`T?` or a nullable reference type).
- **Argument binding** collects all problems and returns them as one `isError` result: `Invalid
  arguments for tool '<name>': <problem>; <problem>. Expected arguments: <signature>.` Problems are
  missing required arguments, unknown arguments (the valid names are listed), wrong JSON types
  (`must be an integer (got string "one")`), non-integral numbers, out-of-range values (by type or by
  `[McpParam]`), unknown enum or `[McpParam(Enum)]` values (the allowed values are listed), and nested
  object errors with their JSON path.
  - Lenient where it cannot be ambiguous: numeric and boolean strings for numbers and booleans,
    numbers and booleans for strings, case-insensitive argument names when there is no exact match,
    enum names matched case-, `_`-, `-`- and space-insensitively, `[McpParam(Enum)]` values matched
    case-insensitively and normalized.
  - JSON `null` for an optional parameter means "use the default".
- **Execution**:
  - `GameThread = true`: the permission/login check and the method body run in one
    `IGameThread.InvokeAsync`, and `IHostState.IsLoggedIn` is read on the framework thread. A returned
    Task is awaited afterwards, off-thread.
  - `GameThread = false`: `IsLoggedIn` is checked via `InvokeAsync` (skipped if `RequiresLogin` is
    false), then the method runs on the thread pool.
  - **Approval** (`McpServer.Approver`, an `IToolCallApprover`, optional): for tools whose `Permission` is
    `Action` or `Chat`, after the category, tier and argument checks and a login pre-check (so nobody is asked about
    a call that cannot run), the server awaits `ApproveToolCallAsync(toolName, permission, clientName,
    argumentsJson, token)` on the thread pool. `false` → `isError` "…was not run: denied in game by the player…";
    no answer within `McpServerOptions.ApprovalTimeout` (default 30 s; the token fires) or a `TimeoutException` →
    `isError` "…not confirmed in game within N s…"; any other exception → `isError` "…Denied for safety" and a log
    entry. Client cancellation or server stop cancels the wait. `CallTimeout` starts only after approval. This
    applies to every path that reaches `tools/call`: 2026-07-28 requests, legacy sessions and 2025-03-26 batches.
  - Every call is bounded by `CallTimeout` (default 30 s) and cancelled by client cancellation or
    server stop. The timeout is also passed to `InvokeAsync`, so a queued frame callback that has not
    started is dropped.
- **Results**:
  - Returns `ToolResult`: `content` blocks as given. `structuredContent` comes from
    `ToolResult.StructuredContent` (2025-06-18 and later). `isError` as given.
  - `void`/`Task`: `content: [{type:text, text:"OK"}]`, no structured content.
  - Returns a record or class (serializes to a JSON object): `structuredContent` = that object;
    `outputSchema` = its schema, where `required` lists only non-nullable value-type members because
    nulls are omitted.
  - Returns anything else (primitives, strings, arrays, lists, `JsonNode`, and nullable object types):
    wrapped. `outputSchema = {type: object, properties: {result: <schema>}, required: [result]}`
    (not required when nullable), `structuredContent = {"result": value}`.
  - The text block mirrors `structuredContent` as compact JSON written with
    `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`, so non-ASCII text (Japanese names, `・`) and `<`, `>`, `&`, `'`
    stay readable instead of `\uXXXX`; quotes, backslashes and control characters are still escaped, so the text
    is valid JSON. The JSON-RPC envelope uses the same encoder: every string stays valid JSON and raw line breaks
    never appear inside an SSE `data:` line. Exception: a `string` return uses the raw string.
  - Content blocks map `text`, `image`/`audio` (`data`, `mimeType`), `resource_link` (`uri`, `name`,
    `mimeType`; `ContentBlock.Text` → `description`) and `resource`
    (`{uri, mimeType, text | blob}`).
- **Tool errors** (`isError: true`, text explains, counted as failed activity):
  - `McpToolException` → its message.
  - Login required → "requires a logged-in character … log in, then retry".
  - Timeout → "timed out after N s …".
  - Any other exception → "failed with an internal error (Type: message)", also logged to the host
    log sink.
  - A non-nullable object return that is `null` → "returned no data".

### Resources

- `resources/list`: `{uri, name, description?, mimeType}` for resources in enabled categories.
- `resources/templates/list`: `{uriTemplate, name, description?, mimeType}`.
- Templates use RFC 6570 level 1 `{var}` (one segment: no `/`, `?` or `#`) plus `{+var}` (rest of the
  URI). Values are percent-decoded and bound to parameters by name, with the same conversions as tool
  arguments. Other operators throw at registration.
- Resources and templates follow the **Read tier** in addition to their category: with Read disabled,
  `resources/list` and `resources/templates/list` are empty, `resources/read` fails like a disabled category, and
  `completion/complete` rejects `ref/resource`. Prompts (text only) follow their category.
- `resources/read`: exact static URI first, then templates in order. Timeout, threading and login rules
  are the same as for tools (`RequiresLogin` defaults: resources true, templates false). Return values:
  - `string` → `text` with the attribute's `MimeType`.
  - `byte[]` → `blob`.
  - Other objects → JSON `text`.
- Resource errors use `-32002` for legacy sessions and `-32602` for 2026-07-28, with `data: {uri}`.
  They cover: not found, category disabled, invalid template value, `McpToolException`, login required,
  and a `null` return. Timeout or another exception → `-32603`.

### Prompts and completion

- `prompts/list`: `{name, title?, description?, arguments: [{name, description?, required}]}`. Enum
  arguments get "One of: …" appended to their description.
- `prompts/get` binds string arguments using the tool conversions and runs on the thread pool, not the
  framework thread (use `ToolContext.Game`). A `string` return becomes one user text message; a
  `PromptResult` maps directly.
  - Unknown prompt or bad arguments → `-32602`; `McpToolException` → `-32602`; timeout or crash → `-32603`.
- `completion/complete` for `ref/prompt` and `ref/resource` (template text): candidates are the
  parameter's enum names or `[McpParam(Enum)]` values, and `true`/`false` for booleans. Prefix matches
  come first, then substring matches, case-insensitive, at most 100, with `total` and `hasMore`. Other
  parameter types return no values. An unknown prompt or template → `-32602`; a static resource URI
  returns no values.

## Notifications, activity, status

- `IMcpNotifier` (`McpServer.Notifier`) is safe from any thread, never throws, never blocks, and does
  nothing when nobody is listening.
  - `ResourceUpdated(uri)` → legacy sessions subscribed to that URI, plus modern listeners with the
    URI in their `resourceSubscriptions`.
  - `*ListChanged()` → every legacy session plus modern listeners that opted in.
  - `Log(level, logger, data)` → legacy sessions at or above their level.
- `ToolContext.Notifier.Log` goes to the current request's stream instead (see the logging sections
  above). Its other methods forward to the server-wide notifier.
- Activity: a ring buffer of `ActivityCapacity` entries (default 500), one per handled JSON-RPC
  request.
  - Entries also cover `initialize`, `GET (sse)` opens, `DELETE (session)`, `subscriptions/listen`, and
    HTTP rejections.
  - `Target` is the tool name, URI, prompt name or rejection status.
  - `Success = false` for JSON-RPC errors, `isError` tool results and cancellations.
  - `ActivityRecorded` is raised on the thread pool. Exceptions thrown by handlers are logged and
    swallowed.
- `GetStatus()`:
  - `Endpoint` is the bound URL (the configured one while stopped).
  - `ActiveSessions` = legacy sessions + distinct 2026-07-28 `clientInfo` identities seen within
    `SessionIdleTimeout`.
  - `ConnectedClients` lists `"name version"` for both.
  - `LastError` is the most recent failed request.
- Deferred approvals (additive): `ISessionAwareToolCallApprover` (the approver also gets the MCP session id and the
  token identity in a `ToolCallApprovalRequest`), `McpServer.CheckToolCall` (gates and argument binding without
  running), `McpServer.ExecuteApprovedToolAsync` (runs an already-approved call with the same gates, login check and
  `CallTimeout`, without asking the approver; returns the `tools/call` result object), `McpServer.RecordHostActivity`
  (host events in the activity feed, not counted as requests), `McpServerOptions.ClientTokens`, `ClientToken`,
  `ToolContext.AuthenticatedClient`. The plugin's use of them is described in [APPROVALS.md](APPROVALS.md).
- Additive public API beyond the frozen contract: `IToolCallApprover`, `McpServer.Approver`,
  `McpServerOptions.ApprovalTimeout`, `ToolContext.ProtocolVersion` (the revision the call is served under:
  negotiated for sessions, `2026-07-28` for stateless requests), `McpJson.Options` / `McpJson.IndentedOptions`,
  `McpServer.ListeningPort`, `McpServer.IsRunning`, `McpServerOptions.SseKeepAliveInterval`,
  `McpServerOptions.ListPageSize`. Options are read at `StartAsync` for binding, token, origins and
  path, and live for everything else.

## Threading contract for hosts

- `IGameThread.InvokeAsync` must honor its `CancellationToken` for work that has not started.
- `IHostState.IsLoggedIn` is called only on the framework thread.
- `IsPermitted` and `IsCategoryEnabled` are called from thread-pool threads: on every list and call,
  and every 2 s by the gate poller. They must be cheap and thread-safe.
- Nothing runs per frame. All socket I/O is async. The framework thread only runs the bodies of tools
  and resources that declare `GameThread = true`, plus the login checks.

## Tooling

- `src/XivMcp.DevHost`: the server on Linux with a 60 Hz simulated framework thread and fake
  providers covering every feature (game-thread and off-thread tools, async hops, progress, logs,
  cancellation, timeouts, login toggle, permission tiers, resource updates, image content, nested
  record input, text/JSON/blob resources, templates, prompts).
  `dotnet xiv-mcp-devhost.dll --port 41811 --token-file /tmp/devtoken`.
- `tools/conformance/run.py --url URL --token-file FILE [--live] [--era both|modern|legacy] [--json report.json]`:
  - Checks: transport and security (401, 403, 400/-32700, 415, GET without session); 2026-07-28
    (discover, list shape, pagination and determinism, `-32602`/`-32022`/`-32020`/`404`, unknown
    resource and tool, invalid arguments as `isError`, listen acknowledgement); each legacy revision
    (initialize, ping, list, session 400/404, bad version header, GET stream, batching rules, DELETE).
  - `--live` calls only tools annotated `readOnlyHint: true` and not `destructiveHint`, using minimal
    schema-valid arguments. It validates `CallToolResult` shape and `structuredContent` against
    `outputSchema`, and reads every static resource.
  - Stdlib only.
- `tools/stdio-bridge/xiv-mcp-stdio.py [--url] [--token-file]` (env `XIV_MCP_URL`, `XIV_MCP_TOKEN`,
  `XIV_MCP_TOKEN_FILE`): a transparent stdio↔HTTP bridge for either era.
  - Legacy: session id capture, GET relay with `Last-Event-ID` reconnects, and `DELETE` on EOF.
  - 2026-07-28: adds the required headers, and a stdio `notifications/cancelled` closes the HTTP
    stream of the referenced request.
  - On stdin EOF, in-flight requests get up to 30 s to finish.
- Interop runs against the DevHost, all passing:
  `npx -y @modelcontextprotocol/inspector --cli http://127.0.0.1:41811/mcp --transport http [--protocol-era legacy|auto|modern] --header "Authorization: Bearer …" --method tools/list|tools/call|resources/read|prompts/get`,
  and the same through `--cli python3 tools/stdio-bridge/xiv-mcp-stdio.py -e XIV_MCP_URL=… -e XIV_MCP_TOKEN_FILE=…`.

## Not verified

- Nothing here was observed inside FINAL FANTASY XIV / Dalamud under Wine: binding, loopback
  reachability from the host, hot-reload port release, and socket cancellation and linger behavior
  were exercised only on native Linux .NET 10.
- Hot reload under Wine is the specific open question. `SO_REUSEADDR` is deliberately not set on
  Windows, where it has port-sharing semantics. The server relies instead on client-first closes,
  resets on stop, and a 3 s bind retry.
- Not tested against Claude Desktop, Claude Code, or other MCP clients besides MCP Inspector CLI
  2.7.0 and the bundled bridge and conformance scripts.
