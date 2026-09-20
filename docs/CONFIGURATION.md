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

- **Server** (always visible): Enabled, and *Where the server listens* — bind mode, custom address, port,
  endpoint path, the detected Tailscale address and the endpoint URLs the mode produces.
- **What clients may do**: the four tiers, *Ask me in game before Action/Chat calls*, and its auto-deny timeout.
- **Categories** (collapsed): per-category switches.
- **Client tokens and auto-approve rules** (collapsed): see [APPROVALS.md](APPROVALS.md). Tokens issued over IPC are marked *via IPC*.
- **Local model** (collapsed): endpoint, model, optional API key (Save/Revert), **Detect**, **Test**, and
  *Let other plugins (Almanac, Ghostty) connect themselves*.
- **Advanced** (collapsed): allowed origins, require token, regenerate token, call timeout,
  chat buffer, activity log verbosity, server info bar entry, agent notifications, agent board expiry, custom objectives.

Every change is saved and applied at once, except the bind settings (mode, address, port, path, origins):
they are edited as a draft, so typing does not restart the server per keystroke, and applied with
**Apply and restart server**, which stops the listener and binds the new endpoints. Everything is
editable in game; nothing needs the file to be edited by hand, and a hand-edited file is not picked up
until the plugin reloads.

## Server

| Key | Default | Effect | Applied |
| --- | --- | --- | --- |
| `Enabled` | `true` | Run the server. Ticking it starts the server, unticking stops it, and the choice holds on the next load. The Start/Stop buttons and `/xivmcp start\|stop` are temporary and do not change it. | immediately |
| `Port` | `41800` (1–65535) | TCP port; the endpoint is `http://<address>:<Port><Path>`. | Apply (restarts a running server) |
| `BindMode` | `Loopback` | Which addresses are bound: `Loopback` (`127.0.0.1` only), `LoopbackAndTailnet` (also this machine's Tailscale address), `TailnetOnly`, `Custom` (uses `CustomHost`). Anything that reaches past this machine shows a red warning and forces `RequireToken` on. A tailnet mode with no Tailscale address falls back to `127.0.0.1` with a notice rather than failing to start. Under Wine, `127.0.0.1` in game is the host's loopback. | Apply (restarts) |
| `CustomHost` | `127.0.0.1` | Address bound by `BindMode: Custom`; ignored otherwise. | Apply (restarts) |
| `Host` | `127.0.0.1` | **Legacy (schema 2).** Kept in step with the mode so an older build still starts; not read once `Version` is 3. Migration: `127.0.0.1`/`localhost`/`::1` → `Loopback`, anything else → `Custom` with that address. | — |
| `Path` | `/mcp` | Endpoint path. Clients must use the same one. | Apply (restarts) |
| `BearerToken` | random | 256-bit random value, unpadded base64url (43 chars), generated on first run. Clients send `Authorization: Bearer <token>`. Never logged; hidden in the UI until revealed. **Regenerate token…** (Advanced) replaces it. | immediately (restarts) |
| `RequireToken` | `true` | When off, requests need no token. Any local program can then call every enabled tool. Held on, and not editable, whenever the bind reaches past this machine. | immediately (restarts) |
| `AllowedOrigins` | `[]` | Extra `Origin` values accepted for browser requests (DNS-rebinding defence). Loopback origins are always accepted, a tailnet origin is **not** — list it here if a browser on another tailnet machine needs access. Requests without `Origin` are not affected. One per line. | Apply (restarts) |
| `CallTimeoutSeconds` | `30` (5–600) | Upper bound for one tool/resource/prompt call. For Action/Chat calls it starts after in-game approval. | immediately, no restart |

## Permissions

| Key | Default | Tier |
| --- | --- | --- |
| `AllowRead` | `true` | Read game state and game data (player, location, items, chat log). |
| `AllowUi` | `true` | Things only you see: echo, toasts, map flags, windows, agent board. |
| `AllowAction` | `false` | Act in your client: target, gearsets, teleport, slash commands. |
| `AllowChat` | `false` | Chat other players can see: say, party, tells, FC. |
| `ConfirmActions` | `true` | **Ask me before anything changes** — the one approval switch, at the top of Settings. On: every state-changing tool (Action, Chat, and the Ui tools that ask, such as the map flag) waits for Allow / Deny or a ticket; sessions, grants and auto-approve rules are shortcuts under it. Off: they run at once, are still written to the action log (`actions.log`), and chat or gear changes still show a notification. Ships on; see [HARD-LINES.md](HARD-LINES.md). |
| `RateLimitPerMinute` | `600` | Calls per minute allowed per client (per-client token, else session). `0` = unlimited. |
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
| `ShowObjectives` | `true` | Show custom objectives (see [OBJECTIVES.md](OBJECTIVES.md)). `/xivmcp quests show\|hide` toggles it. |
| `ObjectivesFollowDutyList` | `true` | Pin the objectives under the game's Duty List; off makes them a small movable window. |
| `NotifyObjectiveReady` | `true` | Normal toast when an objective's conditions become ready. |
| `ShowCompletedObjectives` | `false` | Keep completed objectives listed (greyed) until cleared. |
| `Version` | `2` | Schema version for migrations (1 → 2: stop writing computed properties; no value changes). |

## Local model

XivMcp does not run a model. These settings tell companion plugins (Almanac, the Ghostty terminal's `/ask`) which
local OpenAI-compatible server to use; they read them over IPC (`XivMcp.GetLocalModel`, see
[ARCHITECTURE.md](ARCHITECTURE.md#ipc-plugin--umbra)).

| Key | Default | Effect |
| --- | --- | --- |
| `LocalModelEndpoint` | `""` (not configured) | Base URL, e.g. Ollama `http://127.0.0.1:11434/v1`, LM Studio `:1234/v1`, llama.cpp `:8080/v1`, KoboldCpp `:5001/v1`. Trimmed, trailing `/` removed; anything but an absolute http(s) URL is dropped on load. |
| `LocalModelName` | `""` | Model id at that endpoint (trimmed). |
| `LocalModelApiKey` | `""` | Optional. Never logged and never returned over IPC (only `hasApiKey`). Stored in plain text in this file. |
| `AllowIpcClientTokens` | `true` | Other plugins may issue themselves a client token (`XivMcp.ConnectClient`). Off: the gate returns `{"error":"disabled"}`. |

**Detect** sends `GET {base}/models` (1.5 s timeout, falling back to Ollama's `/api/tags`) to the four default ports and
offers what answered. **Test** lists `{endpoint}/models`, checks the model is listed, then posts a `max_tokens: 8`
"Reply with OK" chat completion and shows the result and latency. Both run off the framework thread.

## Client setup

The Status tab shows one Claude Code command (`claude mcp add --scope user --transport http ffxiv …`) and one
generic `mcpServers` JSON block. Both show and copy `<token>` in place of the token until **Reveal token**
is pressed. `tools/claude-mcp-add.sh` registers the same server without storing the token anywhere
(see the README).

## Security notes

- Keep the bind on loopback unless you mean otherwise. The server exposes your game session. With Action
  or Chat enabled, a caller can act on your behalf — and on the tailnet that means anyone with the token
  on any machine in your tailnet, so the tailnet ACL is part of your security boundary.
- The `Host` header must name loopback, an address the server actually bound, or its MagicDNS name;
  anything else is rejected with 403, which is what stops a web page from rebinding DNS onto the bind.
- The token protects against other local users and processes, not against software running as you.
  Regenerate it if it leaks. Clients registered through `tools/claude-mcp-add.sh` read the new token on
  their next connection.
- `pluginConfigs/XivMcp.json` contains the token. Do not publish it or commit it. Client tokens are stored only as
  hashes, but each one grants the same access as the main token while it exists; revoke those you no longer use.

## Provisioning file

An optional outside file overrides these settings while it exists. See
[the README](../README.md#provisioning-file-optional) for the schema and an example.

- **Path:** `$XIVMCP_PROVISION`, else `$XDG_CONFIG_HOME/xiv-mcp/provision.json`, else
  `$HOME/.config/xiv-mcp/provision.json`. Unix paths are reached through Wine's `Z:` drive from inside
  the game.
- **Read-only.** The plugin never writes it. The values it provides are never copied into
  `XivMcp.json` either, so removing the file restores what you had configured in game.
- **Provisionable keys:** `Enabled`, `BindMode`, `CustomHost`, `Port`, `Path`, `RequireToken`,
  `BearerToken`, `AllowedOrigins`, `CallTimeoutSeconds`, `ConfirmTimeoutSeconds`, `DisabledCategories`.
  Every one is optional; unknown keys are ignored. Permission tiers are deliberately **not**
  provisionable — granting Action or Chat stays a decision made in game.
- **Applied live.** Re-read within a few seconds of a change; a bind change restarts the listener with no
  plugin reload. Settings greys out the provisioned controls and names the file.
- **Mode 0600**, because it may carry `BearerToken`. Its contents are never logged: only the path, the
  provisioned key names and parse errors.
- **A malformed file keeps the last good values**, shows the error in Settings and the log, and never
  falls back to a wider bind.
