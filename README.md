# xiv-mcp

A [Model Context Protocol](https://modelcontextprotocol.io) server that runs **inside FINAL FANTASY XIV** as a
Dalamud plugin. MCP clients such as Claude Code connect to it over Streamable HTTP on loopback
(`http://127.0.0.1:41800/mcp`) and get tools, resources and prompts for game state, game data, the
player's UI and — only when the player opts in — actions and chat. An optional Umbra toolbar widget
shows server status and the agent board.

> **Status.** The plugin builds against Dalamud API 15 (Dalamud 15.0.3.4, .NET 10) and the pure
> services have host-side tests, but **it has not yet been loaded in the game**. Nothing below about
> in-game behaviour has been observed; treat it as the design, not as verified results.

## How it fits together

```
MCP client (Claude Code, ...)  --HTTP POST/GET/DELETE /mcp, Bearer token-->  XivMcp plugin (in game, under Wine)
                                                                               |- McpServer (XivMcp.Core, TcpListener, no ASP.NET)
                                                                               |- providers: [McpProvider] classes -> tools/resources/prompts
                                                                               |- IGameThread -> IFramework (game memory only on the framework thread)
                                                                               |- /xivmcp window, confirmation prompt, DTR entry
                                                                               '- Dalamud IPC  <-- XivMcp.Umbra widget
```

Wine maps `127.0.0.1` inside the game to the host's loopback, so host-side clients reach the plugin
directly. Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Install (dev plugin)

Requirements: XIVLauncher.Core with Dalamud API 15, the .NET 10 SDK on the host (`~/.dotnet/dotnet`
is picked up automatically).

```sh
tools/install-dev.sh
```

It builds Release and prints the Wine path of `XivMcp.dll`, e.g.
`Z:\var\home\you\xiv-mcp-build\artifacts\bin\XivMcp.Plugin\release\XivMcp.dll`. In game:

1. `/xlsettings` → **Experimental** → **Dev Plugin Locations**: add that path and save.
2. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins**: enable **XivMcp**.
3. `/xivmcp` opens the window. The server starts automatically (Settings → *Start server when the
   plugin loads*).

The script never edits Dalamud's configuration or copies files into `~/.xlcore`. The optional Umbra
widget has its own installer (`tools/install-umbra.sh`).

## Connect a client

On first load the plugin generates a 256-bit bearer token and stores it in
`~/.xlcore/pluginConfigs/XivMcp.json`. Every request must send `Authorization: Bearer <token>`
(unless you turn *Require bearer token* off).

### Claude Code (recommended)

```sh
tools/claude-mcp-add.sh            # --force to replace an existing "xiv-mcp" entry, --dry-run to preview
```

This registers `xiv-mcp` at **user** scope with `"type": "http"` and a
[`headersHelper`](https://code.claude.com/docs/en/mcp): a small script installed to
`~/.local/share/xiv-mcp/headers-helper` that reads `BearerToken` from the plugin config each time Claude
Code connects. The token is never printed and never stored in Claude's config, and regenerating it in
game only needs a reconnect (`/mcp` in Claude Code).

### Anything else

The Status tab has copy buttons for a `claude mcp add --header ...` command and a generic
`mcpServers` JSON block (token masked until you reveal it):

```json
{
  "mcpServers": {
    "xiv-mcp": {
      "type": "http",
      "url": "http://127.0.0.1:41800/mcp",
      "headers": { "Authorization": "Bearer <token>" }
    }
  }
}
```

Smoke test from the host without printing the token:

```sh
TOKEN="$(python3 -c 'import json,os;print(json.load(open(os.path.expanduser("~/.xlcore/pluginConfigs/XivMcp.json"),encoding="utf-8-sig"))["BearerToken"])')"
curl -s http://127.0.0.1:41800/mcp \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -H 'Accept: application/json, text/event-stream' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}'
```

## Permission model

Every tool declares one tier. Each tier is a separate toggle (Settings → Permissions); disabled tiers
and categories are hidden from `tools/list` and rejected on call.

| Tier | Default | Meaning |
| --- | --- | --- |
| **Read** | on | Observe game state and static game data. |
| **Ui** | on | Local-only visible effects: echo to your own chat log, toasts, map flags, opening windows, the agent board. |
| **Action** | **off** | Changes your client: targeting, gearsets, teleport, arbitrary slash commands. |
| **Chat** | **off** | Sends text other players can see (say, party, tells, FC, ...). |

- **Confirmation.** *Ask me before every Action/Chat call* (default on) is meant to pause each
  Action/Chat call until you click Approve/Deny in game, auto-denying after 20 s. The server-side hook
  it needs is not in the Core contract yet, so **while this option is on, Action and Chat tools stay
  blocked** (fail closed) and the UI says so. Turning confirmation off makes Action/Chat follow their
  tier toggles directly.
- **Categories** (character, chat, gamedata, ui, meta, prompts, ...) can be switched off individually.
- **Network.** The listener binds `127.0.0.1` by default. Any other host is shown with a red warning,
  and the server refuses to start on a non-loopback host without a bearer token. Requests carrying an
  `Origin` header are accepted only from loopback origins or the configured allow-list.
- **Out of scope:** combat rotations, movement, and input automation of any kind.

All settings: [docs/CONFIGURATION.md](docs/CONFIGURATION.md).

## In game

- `/xivmcp` toggles the window; `/xivmcp start|stop|restart|status|settings`.
- **Status**: running state, endpoint, sessions, bind errors, provider load failures, masked token,
  client snippets. **Agents**: the agent board with state colours and progress bars. **Activity**: live
  request feed with filter; failures highlighted. **Tools**: every registered tool by category with its
  tier and whether it is currently available. **Settings**: everything configurable.
- Server info bar entry `MCP ● n` (n = active sessions; `?` when a confirmation is waiting). Click to
  open the window.
- The agent board: agents call `post_status` to show "what I'm doing" in game (and in the Umbra
  widget); a notification appears when an agent posts `done` or `failed`.

## Tool catalog

<!-- ORCHESTRATOR: fill the remaining rows from the provider agents' reports. -->

| Tool | Tier | Category | What it does |
| --- | --- | --- | --- |
| `get_server_info` | Read | meta | Version, endpoint, enabled tiers and categories (with tool counts), Dalamud/game version, login state. Call first. |
| `post_status` | Ui | meta | Create/replace this agent's entry on the in-game agent board (state running/done/failed/info, progress 0..1, detail). |
| `list_status` | Read | meta | Current agent board, newest first. |
| `clear_status` | Ui | meta | Remove one agent's entry or the whole board. |
| _TBD_ | | | _Tools from the character, world, chat, gamedata, ui and actions providers._ |

### Resources

| URI | What |
| --- | --- |
| `ffxiv://agents` | Agent board JSON; `notifications/resources/updated` on every change. |
| _TBD_ | _Resources from other providers._ |

### Prompts

| Prompt | Arguments | Workflow |
| --- | --- | --- |
| `character_overview` | — | Jobs by role, gear average, location, party, notable currencies. |
| `gear_audit` | `job?` | Slot-by-slot item level/materia audit and upgrades already owned. |
| `crafting_plan` | `item`, `quantity?` | Full recipe tree, owned vs. missing materials, crafter level blockers, crafting order. |
| `where_is` | `target` | Find an NPC/object/FATE/place nearby or by game data; offers a map flag. |
| `duty_prep` | `duty` | Requirements vs. character, party composition gaps, readiness checklist. |
| `situation_report` | — | Zone, Eorzea time, weather, FATEs, conditions/duty, party, recent chat. |

Prompts only generate instructions; every game interaction still goes through tools and their tiers.

## Development

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
~/.dotnet/dotnet build XivMcp.slnx -c Release
```

Build output goes to `../xiv-mcp-build/artifacts` (override with `XIVMCP_ARTIFACTS`), never into the
source tree. Writing a provider: [docs/PROVIDERS.md](docs/PROVIDERS.md).

## Troubleshooting

- **"Could not start on http://127.0.0.1:41800/mcp: ... address already in use"** — another process
  (or a previous plugin instance that did not unload) holds the port. Change the port in Settings or
  restart the game.
- **A provider shows "failed to load"** — the Status and Tools tabs list the exception; the other
  providers keep working. `/xllog` has the stack trace.
- **Action/Chat tools missing** — they are off by default, and blocked while confirmation is on (see
  above). `get_server_info` reports why.
- **Claude Code says the connection failed** — the helper exits non-zero if the plugin config does not
  exist yet (load the plugin once) or has no token. Run
  `~/.local/share/xiv-mcp/headers-helper >/dev/null && echo ok` to check without printing the token.
