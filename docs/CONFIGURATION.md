# Configuration

Settings live in `~/.xlcore/pluginConfigs/XivMcp.json`. Dalamud writes this file (Newtonsoft JSON with
`$type` annotations). Edit settings in game (`/xivmcp` → **Settings**) rather than by hand: the file
is only read when the plugin loads, and the plugin overwrites it on every change.

On load the plugin clamps out-of-range values and generates `BearerToken` if it is empty. A corrupt
file is replaced with defaults, which also generates a new token.

## Server

| Key | Default | Effect | Applied |
| --- | --- | --- | --- |
| `Enabled` | `true` | Start the server when the plugin loads. Start/Stop buttons and `/xivmcp start\|stop` do not change it. | next load |
| `Host` | `127.0.0.1` | Listen address. Anything that is not loopback (`127.0.0.0/8`, `::1`, `localhost`) shows a red warning, and the server refuses to start on it unless `RequireToken` is on. Under Wine, `127.0.0.1` in game is the host's loopback. | Apply (restarts a running server) |
| `Port` | `41800` | TCP port; the endpoint is `http://<Host>:<Port>/mcp`. | Apply |
| `BearerToken` | random | 256-bit random value, unpadded base64url (43 chars), generated on first run. Clients send `Authorization: Bearer <token>`. Never logged; masked in the UI until revealed. **Regenerate token…** replaces it. | immediately (restart) |
| `RequireToken` | `true` | When off, requests need no token. Any local program can then call every enabled tool. | immediately (restart) |
| `AllowedOrigins` | `[]` | Extra `Origin` values accepted for browser requests (DNS-rebinding defence). Loopback origins are always accepted; requests without `Origin` are not affected. One per line in the UI. | Apply |
| `CallTimeoutSeconds` | `30` (5–600) | Upper bound for one tool/resource/prompt call. For Action/Chat calls it starts after in-game approval. | Apply (no restart) |

"Apply" means the **Apply** / **Apply and restart** button under Server settings. Host, port, timeout
and origins are edited as a draft so typing does not restart the server on every keystroke.

## Permissions

| Key | Default | Tier |
| --- | --- | --- |
| `AllowRead` | `true` | Observe game state and static game data. |
| `AllowUi` | `true` | Local-only visible effects (echo, toasts, map flags, windows, agent board). |
| `AllowAction` | `false` | Changes local client state (target, gearset, teleport, slash commands). |
| `AllowChat` | `false` | Text other players can see. |
| `ConfirmActions` | `true` | Ask in game before every Action/Chat call (Allow / Deny / Allow this tool for 10 min). Off: Action/Chat follow their tier toggles directly. See [ARCHITECTURE.md](ARCHITECTURE.md#confirmation-of-actionchat-calls). Unverified in game. |
| `ConfirmTimeoutSeconds` | `20` (5–300; UI slider 5–120) | Seconds before a pending confirmation is denied automatically (the client gets "not confirmed in game within N s"). |
| `DisabledCategories` | `[]` | Provider categories that are switched off (e.g. `"chat"`). Their tools, resources and prompts are hidden and rejected. |

Tier and category changes take effect immediately. Connected clients receive `tools/list_changed`,
`prompts/list_changed` and `resources/list_changed`, and temporary "allow for 10 min" grants are revoked. Resources
follow the Read tier as well as their category. Grants are kept in memory only (never written to this file).

## Providers and interface

| Key | Default | Effect |
| --- | --- | --- |
| `ChatBufferSize` | `500` (50–5000) | Recent chat lines kept for `read_chat`. The chat provider may only pick up a change after a plugin reload. |
| `ActivityLogLevel` | `Failures` | What each handled request writes to the Dalamud log (`/xllog`): `Off`, `Failures`, `Calls` (failures + `tools/call`, `resources/read`, `prompts/get`), `All`. The Activity tab is independent of this setting: it shows the 300 most recent entries, with filters. Arguments and the token are never logged. |
| `ShowDtrEntry` | `true` | `MCP ● n` in the server info bar (`MCP ○` when stopped, `?` while a confirmation waits). Click to toggle the window. |
| `NotifyAgentCompletion` | `true` | Dalamud notification when an agent's board entry moves to `done` or `failed`. |
| `AgentBoardExpiryMinutes` | `120` (0 = never) | Board entries not updated for this long are removed. |
| `Version` | `1` | Schema version for migrations. |

## Security notes

- Keep `Host` on loopback. The server exposes your game session. With Action or Chat enabled, a caller
  can act on your behalf.
- The token protects against other local users and processes, not against software running as you.
  Regenerate it if it leaks. Clients registered through `tools/claude-mcp-add.sh` read the new token on
  their next connection.
- `pluginConfigs/XivMcp.json` contains the token. Do not publish it or commit it.
