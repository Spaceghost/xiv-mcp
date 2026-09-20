# Architecture

> The plugin has not been loaded in the game yet. This describes the implemented design; runtime
> behaviour in game is unverified.

## Projects

| Project | Output | Depends on | Role |
| --- | --- | --- | --- |
| `src/XivMcp.Core` | `XivMcp.Core.dll` (net10.0) | BCL only | MCP protocol + Streamable HTTP transport on `TcpListener`, attribute-driven registry, sessions, activity feed. No Dalamud, no ASP.NET Core (Dalamud's runtime ships neither). |
| `src/XivMcp.Plugin` | `XivMcp.dll` (Dalamud.NET.Sdk 15) | Core, Dalamud API 15 | Plugin shell (config, services, windows, IPC, DTR) and every tool provider. |
| `src/XivMcp.Umbra` | Umbra widget plugin | Umbra, IPC contract | Toolbar widget that talks to the plugin only over Dalamud IPC. |
| `src/Shared/IpcContract.cs` | compiled into Plugin and Umbra | — | IPC gate names and JSON payload shapes. |

Frozen contracts (additive changes only): `Core/Abstractions/Attributes.cs`, `Core/Abstractions/Contracts.cs`,
the public surface of `Core/McpServer.cs`, and `Shared/IpcContract.cs`.

## Plugin shell

```
Plugin (IDalamudPlugin)
 ├─ Configuration                 pluginConfigs/XivMcp.json (token generated on first run)
 ├─ DalamudGameThread : IGameThread   IFramework.RunOnTick / inline when already on the framework thread
 ├─ ConfirmationService : ISessionAwareToolCallApprover   in-game Allow/Deny for Action/Chat calls; 10-minute grants;
 │                                 auto-approve rules for token-identified clients
 ├─ ApprovalSessionService        "allow everything from this client" sessions (memory only, 1-60 min)
 ├─ ApprovalQueue + TicketStore   deferred approval tickets (approval-tickets.json), one-at-a-time execution
 ├─ ApprovalWiring                activity/log entries, session toasts, permission changes, 1 Hz tick
 ├─ HostState : IHostState        tiers, categories, IsLoggedIn
 ├─ NotifierProxy : IMcpNotifier  handed to providers; forwards to the server, never throws
 ├─ AgentBoard                    agent progress posts (post_status / window / IPC / ffxiv://agents)
 ├─ ObjectiveStore + ObjectiveTracker   custom objectives (pluginConfigs/XivMcp/objectives.json), live conditions, flags, toasts
 ├─ ServerHost                    one McpServer for the plugin lifetime; providers; start/stop/restart
 ├─ WindowSystem: MainWindow (Status/Agents/Approvals/Activity/Tools/Settings), ConfirmWindow, ObjectiveOverlay
 ├─ IpcProvider                   IpcContract gates + throttled Changed
 └─ DtrEntry                      "MCP ● n" server info bar entry
```

`Plugin.cs` builds these in order, registers `/xivmcp`, `OpenMainUi`/`OpenConfigUi` and the Draw
handler, and starts the server if autostart is on. If the constructor throws it disposes whatever it
already created, because Dalamud does not call `Dispose` on a failed load.

### Server lifetime

`ServerHost` creates a single `McpServer` and keeps it for the whole plugin lifetime, so providers and
the notifier survive restarts. Start/stop/restart are serialized with a semaphore and run on the
thread pool. Before each start the host copies configuration into `McpServer.Options` (bind addresses, allowed host
names, port, path, token, allowed origins, call timeout, instructions). Failures to start, including bind errors, go to
`ServerHost.LastError` and appear in the window, `/xivmcp status` and the DTR tooltip. Nothing is
thrown into Dalamud.

After a settings change, `ServerHost.ApplyConfigAsync()`:
- updates the call timeout and the approval timeout in place (no restart);
- restarts a running server if the bind plan, port, path, token or origins changed;
- sends `tools/list_changed`, `prompts/list_changed` and `resources/list_changed` and revokes temporary
  confirmation grants if tiers, categories or the confirmation toggle changed, so connected clients re-list.

Safety check: `StartCoreAsync` refuses to start a bind that reaches past this machine unless a bearer
token is required and set.

### Where it listens (bind modes)

`Configuration.BindMode` replaced the schema-2 `Host` string. `BindPlanner.Resolve(mode, customHost,
tailnet)` is pure and turns the mode plus the detected tailnet address into a `BindPlan`: the ordered
address list, the preferred address for an off-machine client, the extra names the `Host` header check
must accept, and whether the bearer token is mandatory. `ServerHost.Plan` / `.Endpoints` /
`.PreferredEndpoint` / `.LoopbackEndpoint` are the **single source of truth** for "where does this server
listen" — the window, the Status snippets and the IPC payload all read them instead of deriving their own
URL.

| Mode | Addresses | Token |
| --- | --- | --- |
| `Loopback` (default) | `127.0.0.1` | optional |
| `LoopbackAndTailnet` | `127.0.0.1` + the tailnet address | required |
| `TailnetOnly` | the tailnet address | required |
| `Custom` | the configured address | required unless it is loopback |

A tailnet mode with no detected address falls back to `127.0.0.1` and reports a notice; it never fails to
start, and it never binds something wider than the mode asked for.

**Detection** (`Core/Net/Tailnet.cs`, BCL only, host-tested through a fake adapter list): an address in
`100.64.0.0/10` or `fd7a:115c:a1e0::/48`, preferring an adapter whose name or description says Tailscale
(`tailscale0`, `ts0`, "Tailscale Tunnel") and one that is up, IPv4 before IPv6. Three routes in order:

1. `NetworkInterface.GetAllNetworkInterfaces()`. **Verified under the XIVLauncher Wine prefix**
   (wine-xiv-staging 10.8, .NET 10 win-x64 console probe): Wine passes the Linux interfaces through with
   their names intact — `Name` and `Description` are both `tailscale0`, and both the CGNAT and the ULA
   address are listed. `NetworkInterfaceType` is reported as `Ppp`, so the type is never used to decide.
2. the local endpoint of a connectionless UDP socket aimed at `100.100.100.100` (no packet is sent), kept
   only when that address is itself a tailnet address. Also observed working under Wine.
3. the `tailscale` CLI. **Not reachable from inside Wine**: executing the Linux binary through `Z:` with
   redirected stdio fails with `E_HANDLE`, so this route only serves native hosts.

The MagicDNS name is a reverse lookup of the detected address (MagicDNS answers PTR, and the resolver is
reachable from inside the prefix), falling back to `tailscale status --json`. It is best effort; nothing
depends on it. `TailnetProbe` caches the result for 20 s. Detection runs on server start and when the
settings window opens, on a background task — never in the draw loop.

### Listening on several addresses

`HttpServer.Start(IReadOnlyList<IPAddress>, port)` binds **one real `TcpListener` per address**, all on
the same port, and runs one accept loop each into the same connection handling (so `LoopbackAndTailnet`
is two listeners, not a wildcard bind with a filter — nothing ever binds `0.0.0.0` unless the owner
explicitly asks for it). With port 0 the first bind picks the port and the rest follow. A bind that fails
part-way closes everything this call opened and rethrows, so a partial bind cannot leave the server up on
some addresses only.

The DNS-rebinding defence moved with it: the `Host` header must name a loopback name, an address the
server actually bound, a name in `McpServerOptions.AllowedHostNames` (the MagicDNS name) or the host of
an allowed origin. It used to run only for a loopback bind; it now runs for every bind except a wildcard
one, where there is no address list to check against. Browser `Origin` is unchanged: loopback origins or
the allow-list, so a browser on another tailnet machine needs its origin listed.

### Provisioning file

`ProvisionFile` + `ProvisionStore` read an optional outside file (`$XIVMCP_PROVISION`, else
`$XDG_CONFIG_HOME/xiv-mcp/provision.json`, else `$HOME/.config/xiv-mcp/provision.json`; Unix paths are
mapped onto Wine's `Z:` drive the way objective packs are). Its values overlay the saved configuration:
`Configuration`'s provisionable settings read `Provision?.X ?? stored`, while Newtonsoft serializes the
**stored** field, so a provisioned value is never written into `XivMcp.json` and removing the file
restores the owner's own values exactly.

The file is read-only to the plugin and polled (every 3 s, from the 1 Hz framework tick) rather than
watched, because `FileSystemWatcher` across Wine's view of the Linux filesystem is not dependable. A
malformed file keeps the last good document, surfaces the message in Settings and the log, and is retried
on the next tick. Contents are never logged — only the path, the provisioned key names and parse errors.
A change that affects the bind restarts the listener through `ApplyConfigAsync`, with no plugin reload.

### Provider discovery and DI

`ServerHost.LoadProviders(typeof(Plugin).Assembly, scopedObjects...)` finds every non-abstract class
marked `[McpProvider]` and, **for each one on its own**:

1. constructs it with `IDalamudPluginInterface.CreateAsync<T>(scopedObjects)`, called through
   reflection because the type is only known at runtime;
2. calls `McpServer.RegisterProvider(instance)`.

A failure at either step is logged and recorded as a `ProviderInfo` error (Status and Tools tabs,
`get_server_info`). The other providers still load. The shell calls `RegisterProvider` once per
instance rather than `RegisterProviders(assembly, factory)`, which would let one bad provider abort the
whole loop.

How Dalamud's IoC resolves constructor parameters (checked by decompiling `Dalamud.IoC.Internal.ServiceContainer`
for API 15):
- Dalamud services (`IClientState`, `IChatGui`, `IPluginLog`, ...) come from the container. Plugin-scoped
  services are created once per plugin scope.
- Otherwise the first scoped object whose runtime type is assignable to the parameter type is used.
  `IDalamudPluginInterface` is always appended.
- The constructor runs on a `LongRunning` thread-pool thread, **not** the framework thread. Provider
  constructors must not touch game memory; subscribing to events is fine.

Scoped objects the shell passes: `Configuration`, `DalamudGameThread` (as `IGameThread`),
`NotifierProxy` (as `IMcpNotifier`), `AgentBoard`, `ServerHost`, `HostState`, `ConfirmationService`,
`ObjectiveTracker`, `ApprovalQueue`, `ApprovalSessionService`.

On unload, providers implementing `IAsyncDisposable`/`IDisposable` are disposed in reverse load order,
after the server has stopped.

### Threading

| Work | Thread |
| --- | --- |
| HTTP accept/read, JSON-RPC dispatch | thread pool (Core) |
| Tools with `GameThread = true` | framework thread via `IGameThread.InvokeAsync` → `IFramework.RunOnTick` (inline if already on it; cancellable while queued) |
| `ActivityRecorded` | thread pool; `ServerHost` only counts, logs and re-raises |
| ImGui windows, confirmation resolve, IPC `Changed`, DTR updates, `ServerHost.Tick` (1 Hz) | framework thread |

Configuration is written only from the framework thread (UI). Collections are replaced rather than
modified in place, so server threads always read a consistent reference.

### Confirmation of Action/Chat calls

`ServerHost` sets `McpServer.Approver` to the `ConfirmationService` (Core contract `IToolCallApprover`, see
[PROTOCOL.md](PROTOCOL.md)). For every Action/Chat tool call that passed the category, tier, argument and login
checks, the server awaits `ApproveToolCallAsync` on the thread pool:

1. Read/Ui calls, and every call while *Ask me before every Action/Chat call* is off, pass immediately.
2. `execute_command` lines are classified with `ChatCommands` (NFKC-folded, format characters removed, split at any
   Unicode whitespace; English spellings plus every TextCommand spelling in all four client languages). Chat commands
   are confirmed and granted as **Chat**. Lines the tool itself refuses (blocked, automation, chat while the Chat tier
   is off) are not shown to the player.
3. An unexpired grant for the same tool, tier and client-reported name passes.
4. Otherwise a `PendingConfirmation` (tool, declared and effective tier, client name, arguments pretty-printed with
   format/bidi/line-separator characters shown as `\uXXXX`, at most 4000 characters) is queued. `ConfirmWindow` draws it
   on the framework thread with **Allow**, **Deny**, **Allow this tool for 10 min** and an auto-deny countdown, and
   the Draw loop calls `Resolve`, which completes the server-side await.

The server enforces `ApprovalTimeout` (= *Auto-deny after (s)*) through the token; the service has a fallback
timeout 2 s later that throws `TimeoutException`. Client cancellation, server stop, `Deny all` and plugin unload
deny. The call timeout starts after approval. Grants are dropped when permissions, categories or the confirmation
toggle change, from Settings (*Revoke all*), and on unload.

Before the grant check, the service also consults the owner's auto-approve rules (only for a request that authenticated
with a per-client token) and the approval sessions. The deferred approval queue, sessions, rules and the resume contract
for clients are described in [APPROVALS.md](APPROVALS.md). Approved tickets run on a single worker on the thread pool
through `McpServer.ExecuteApprovedToolAsync`, which dispatches game work to the framework thread like `tools/call`.

**Unverified in game:** the confirmation window, its buttons and the grant list have not been drawn or clicked inside
FINAL FANTASY XIV. The Core hook is covered by `tests/XivMcp.Core.Tests/ApprovalTests.cs` and the service logic by
`tests/XivMcp.Plugin.Tests/ConfirmationServiceTests.cs`.

### Unload / hot reload

`Plugin.Dispose` removes the command, UI handlers and windows, denies pending confirmations, then
disposes (in reverse order) the DTR entry, the IPC provider (unregisters every gate), `ServerHost`
(stops the listener and waits at most 5 s so the port is freed for the reloaded instance, disposes the
server and the providers) and `ConfirmationService`.

## IPC (plugin ↔ Umbra)

Gates from `IpcContract` with their type parameters (a subscriber must use the same ones):

| Gate | Provider type | Payload |
| --- | --- | --- |
| `XivMcp.ApiVersion` | `<int>` func | `IpcContract.Version` |
| `XivMcp.GetStatus` | `<string>` func | `{running, endpoint, endpoints[], activeSessions, totalRequests, failedRequests, lastError, connectedClients[], permissions{read,ui,action,chat}, agents, confirmActions}`. `permissions` holds the tier toggles. `endpoint` is the address to hand a client (the tailnet address when it is bound, else loopback); `endpoints` lists every bound address in bind order. |
| `XivMcp.GetActivity` | `<int, string>` func | newest N (clamped 1..500) activity entries |
| `XivMcp.GetAgentBoard` | `<string>` func | `[{agent, status, state, progress, detail, clientName, updatedAt}]`, newest first |
| `XivMcp.SetRunning` | `<bool, bool>` func | starts/stops on the thread pool without blocking the caller; returns the running state at call time (`Changed` follows when the transition finishes) |
| `XivMcp.ToggleWindow` | `<object>` action | toggles the main window |
| `XivMcp.Changed` | `<object>` message | no arguments; sent from the framework thread, coalesced to ≤ 4 Hz, on server state, activity or board changes |

## Agent board

`AgentBoard` keeps at most 64 posts, keyed by agent name (case-insensitive). Agent names are capped at
64 characters, status at 200 and detail at 2000; progress is clamped to 0..1. Posts expire after the
configured idle time (default 120 min). `AgentBoardProvider` exposes `post_status` (Ui), `list_status`
(Read), `clear_status` (Ui) and the `ffxiv://agents` resource. It sends `resources/updated` on every
change and a Dalamud notification when a post moves into `done`/`failed` (configurable).

## Custom objectives

`ObjectiveStore` holds up to 200 objectives in insertion order and saves them atomically to
`pluginConfigs/XivMcp/objectives.json`. The rules are pure and host-tested (`Objectives/`: time windows, weather tables,
next window, packs, progress). `ObjectiveTracker` snapshots zone, position and live weather on the framework thread,
evaluates every objective once a second (and right after a change), places map flags and shows quest toasts.
`ObjectivesProvider` exposes the tools and `ffxiv://objectives`; `ObjectiveOverlay` draws them under the Duty List,
reading `_ToDoList` without modifying it. Why an overlay and not injected nodes: [OBJECTIVES.md](OBJECTIVES.md).
