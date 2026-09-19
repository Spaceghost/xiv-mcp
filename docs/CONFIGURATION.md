# Configuration

Settings live in `~/.xlcore/pluginConfigs/XivMcp.json`. Dalamud writes this file (Newtonsoft JSON with
`$type` annotations). Edit settings in game (`/xivmcp` → **Settings**) rather than by hand: the file
is only read when the plugin loads, and the plugin overwrites it on every change.

On load the plugin migrates old versions, clamps out-of-range values, trims lists, and generates
`BearerToken` only if it is empty. Migrations never change the token or the permission tiers. A file
that cannot be deserialized is copied to `XivMcp.json.unreadable-<UTC timestamp>` and every value that can
still be read is kept (the token is recovered even from a truncated file); only the rest falls back to
defaults. Only stored settings are written: computed values (`HostIsLoopback`, `EndpointUrl`, written by
version 1) are dropped on the first save.

## Settings tab layout

- **Server** (always visible): Enabled, Port.
- **What clients may do**: the four tiers, *Ask me in game before Action/Chat calls*, and its auto-deny timeout.
- **Categories** (collapsed): per-category switches.
- **Advanced** (collapsed): listen host, allowed origins, require token, regenerate token, call timeout,
  chat buffer, activity log verbosity, server info bar entry, agent notifications, agent board expiry.

Every change is saved and applied at once, except port, host and origins: they are edited as a draft
(so typing does not restart the server per keystroke) and applied with **Apply and restart server**,
which stops the listener and binds the new endpoint.

## Server

| Key | Default | Effect | Applied |
| --- | --- | --- | --- |
| `Enabled` | `true` | Run the server. Ticking it starts the server, unticking stops it, and the choice holds on the next load. The Start/Stop buttons and `/xivmcp start\|stop` are temporary and do not change it. | immediately |
| `Port` | `41800` (1–65535) | TCP port; the endpoint is `http://<Host>:<Port>/mcp`. | Apply (restarts a running server) |
| `Host` | `127.0.0.1` | Listen address (Advanced). Anything that is not loopback (`127.0.0.0/8`, `::1`, `localhost`) shows a red warning, and the server refuses to start on it unless `RequireToken` is on. Under Wine, `127.0.0.1` in game is the host's loopback. | Apply (restarts) |
| `BearerToken` | random | 256-bit random value, unpadded base64url (43 chars), generated on first run. Clients send `Authorization: Bearer <token>`. Never logged; hidden in the UI until revealed. **Regenerate token…** (Advanced) replaces it. | immediately (restarts) |
| `RequireToken` | `true` | When off, requests need no token. Any local program can then call every enabled tool. | immediately (restarts) |
| `AllowedOrigins` | `[]` | Extra `Origin` values accepted for browser requests (DNS-rebinding defence). Loopback origins are always accepted; requests without `Origin` are not affected. One per line. | Apply (restarts) |
| `CallTimeoutSeconds` | `30` (5–600) | Upper bound for one tool/resource/prompt call. For Action/Chat calls it starts after in-game approval. | immediately, no restart |

## Permissions

| Key | Default | Tier |
| --- | --- | --- |
| `AllowRead` | `true` | Read game state and game data (player, location, items, chat log). |
| `AllowUi` | `true` | Things only you see: echo, toasts, map flags, windows, agent board. |
| `AllowAction` | `false` | Act in your client: target, gearsets, teleport, slash commands. |
| `AllowChat` | `false` | Chat other players can see: say, party, tells, FC. |
| `ConfirmActions` | `true` | *Ask me in game before Action/Chat calls* (Allow / Deny / Allow this tool for 10 min). Off: Action/Chat follow their tier toggles directly. See [ARCHITECTURE.md](ARCHITECTURE.md#confirmation-of-actionchat-calls). Unverified in game. |
| `ConfirmTimeoutSeconds` | `20` (5–300) | Seconds before a pending confirmation is denied automatically (the client gets "not confirmed in game within N s"). |
| `DisabledCategories` | `[]` | Provider categories that are switched off (e.g. `"chat"`). Their tools, resources and prompts are hidden and rejected. |
| `ApprovalSessionMinutes` | `5` (1–60) | Length of an "Allow everything from this client" approval session started from the Approvals tab. Sessions themselves are never saved. See [APPROVALS.md](APPROVALS.md). |
| `ClientTokens` | `[]` | Per-client bearer tokens: `{Name, TokenSha256, CreatedAt}`. Only the SHA-256 is stored; the token is shown once when generated (Settings → *Client tokens and auto-approve rules*). Revoking removes the entry; the server stops accepting the token on the next request. Entries with an invalid name or hash are dropped on load. |
| `AutoApproveRules` | `[]` | Owner pre-approvals for a token-identified client: `{Enabled, Client, Tool, Argument ("command"), Prefixes[], IncludeChat}`. Matching rules are in [APPROVALS.md](APPROVALS.md#auto-approve-rules-and-client-tokens-ci); prefixes that are not printable ASCII are dropped on load. |

Tier and category changes take effect immediately. Connected clients receive `tools/list_changed`,
`prompts/list_changed` and `resources/list_changed`, and temporary "allow for 10 min" grants are revoked. Resources
follow the Read tier as well as their category. Grants and approval sessions are kept in memory only (never written to
this file); permission changes also revoke approval sessions, deny pending tickets when the Action tier goes off (Chat
tickets when Chat goes off), and leave client tokens and rules alone.

Approval tickets are stored separately in `pluginConfigs/XivMcp/approval-tickets.json` (the plugin config directory).
It holds the arguments of queued calls; treat it like `XivMcp.json`. An unreadable file is renamed to
`approval-tickets.json.unreadable-<time>` and the queue starts empty.

## Providers and interface (Advanced)

| Key | Default | Effect |
| --- | --- | --- |
| `ChatBufferSize` | `500` (50–5000) | Recent chat lines kept for `read_chat`. The buffer resizes when the next chat line arrives. |
| `ActivityLogLevel` | `Failures` | What each handled request writes to the Dalamud log (`/xllog`): `Off`, `Failures`, `Calls` (failures + `tools/call`, `resources/read`, `prompts/get`), `All`. The Activity tab is independent of this setting: it shows the 300 most recent entries, with filters. Arguments and the token are never logged. |
| `ShowDtrEntry` | `true` | `MCP ● n` in the server info bar (`MCP ○` when stopped, `?` while a confirmation waits). Click to toggle the window. |
| `NotifyAgentCompletion` | `true` | Dalamud notification when an agent's board entry moves to `done` or `failed`. |
| `AgentBoardExpiryMinutes` | `120` (0–10080, 0 = never) | Board entries not updated for this long are removed. |
| `Version` | `2` | Schema version for migrations (1 → 2: stop writing computed properties; no value changes). |

## Client setup

The Status tab shows one Claude Code command (`claude mcp add --scope user --transport http ffxiv …`) and one
generic `mcpServers` JSON block. Both show and copy `<token>` in place of the token until **Reveal token**
is pressed. `tools/claude-mcp-add.sh` registers the same server without storing the token anywhere
(see the README).

## Security notes

- Keep `Host` on loopback. The server exposes your game session. With Action or Chat enabled, a caller
  can act on your behalf.
- The token protects against other local users and processes, not against software running as you.
  Regenerate it if it leaks. Clients registered through `tools/claude-mcp-add.sh` read the new token on
  their next connection.
- `pluginConfigs/XivMcp.json` contains the token. Do not publish it or commit it. Client tokens are stored only as
  hashes, but each one grants the same access as the main token while it exists; revoke those you no longer use.
