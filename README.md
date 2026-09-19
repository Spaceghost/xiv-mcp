# xiv-mcp

A [Model Context Protocol](https://modelcontextprotocol.io) server that runs **inside FINAL FANTASY XIV** as a
Dalamud plugin. MCP clients such as Claude Code connect to it over Streamable HTTP on loopback
(`http://127.0.0.1:41800/mcp`) and get tools, resources and prompts for game state, game data, the
player's UI and — only when the player opts in — actions and chat. An optional Umbra toolbar widget
shows server status and the agent board.

> **Status.** The plugin builds against Dalamud API 15 (Dalamud 15.0.3.4, .NET 10). The MCP runtime and
> the plugin logic that needs no game (confirmation service, chat rules, IPC payloads, map maths) have host-side
> tests, but **the plugin has not yet been observed running in the game**. Nothing below about in-game behaviour
> has been observed; treat it as the design, not as verified results.

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

Requirements: XIVLauncher.Core with Dalamud 15.0.3.5 (API 15), the .NET 10 SDK on the host (`~/.dotnet/dotnet`
is picked up automatically). Reference assemblies come from `~/.xlcore/dalamud/Hooks/dev/`; rebuild after every
Dalamud update.

```sh
tools/install-dev.sh
```

It builds Release, copies the result into `~/xiv-mcp-build/devplugin/` (so Dalamud never sees a half-written
build) and prints the Wine path of the staged DLL, e.g. `Z:\home\<you>\xiv-mcp-build\devplugin\XivMcp.dll`.
In game, once:

1. `/xlsettings` → **Experimental** → **Dev Plugin Locations**: paste that path, click **+**, then **Save and close**.
2. `/xlplugins` → **Dev Tools** → **Installed Dev Plugins**: Dalamud adds dev plugins **disabled**. Enable
   **XivMcp**, and in its entry tick **Start on boot** (otherwise it stays off after the next game start).
3. `/xivmcp` opens the window. The server starts automatically (Settings → *Start server when the
   plugin loads*).

After rebuilding, run `tools/install-dev.sh` again and reload XivMcp from `/xlplugins` (or tick its **Automatic reloading**
option). If `/xivmcp` is an unknown command, the plugin is not loaded: check steps 1–2 and the Dalamud
log (`~/.xlcore/logs/dalamud.log`, search for `XivMcp`).

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

- **Confirmation.** *Ask me before every Action/Chat call* (default on): each Action or Chat call waits for the
  in-game confirmation window, which shows the tool, the client-reported name and the pretty-printed arguments, with
  **Allow**, **Deny** and **Allow this tool for 10 min** (same tool, tier and client name; revoked when permissions
  change, listed under Settings). Unanswered calls are denied after *Auto-deny after (s)* (default 20). The client
  gets `isError` "denied in game by the player" or "not confirmed in game within N s", and the tool did not run.
  `execute_command` lines that post chat are shown and granted as Chat. Turning confirmation off makes Action/Chat
  follow their tier toggles directly. **Unverified in game:** the window and its buttons have not been exercised
  inside FINAL FANTASY XIV yet; the server-side hook and the service logic are covered by host tests.
- **Categories** (character, chat, gamedata, ui, meta, prompts, ...) can be switched off individually.
- **Resources** follow the Read tier as well as their category; prompts only return text and follow their category.
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

Generated from the `[McpTool]`, `[McpResource]`, `[McpResourceTemplate]` and `[McpPrompt]` attributes of the built
plugin by `tools/catalog` (`dotnet run --project tools/catalog -c Release -- readme --write README.md` after building);
do not edit the block by hand.

<!-- BEGIN GENERATED CATALOG: dotnet run --project tools/catalog -- readme --write README.md -->

56 tools, 9 resources and templates, 6 prompts. Tier and category are the in-game switches
(Settings → Permissions / Categories); *Login* means the call fails at the title screen. Descriptions are the first
sentence of what clients see; `tools/list` has the full text and schemas. Behaviour in game is unverified unless
stated elsewhere.

### Tools

| Tool | Tier | Category | Login | What it does |
| --- | --- | --- | --- | --- |
| `list_gearsets` | Read | actions | yes | Lists the character's saved gear sets. |
| `list_macros` | Read | actions | yes | Lists the user's macros from the in-game User Macros window: set individual (this character) or shared (all characters on the account), 100 slots each. |
| `clear_target` | Action | actions | yes | Clears the user's current target (like pressing Escape on a target). |
| `equip_gearset` | Action | actions | yes | Equips one of the character's saved gear sets (which also changes class/job when the set belongs to another job), exactly like /gearset change. |
| `set_focus_target` | Action | actions | yes | Sets the user's focus target (the secondary tracked target shown in the Focus Target bar), or clears it with clear=true. |
| `set_target` | Action | actions | yes | Sets the user's current target, like clicking an object. |
| `teleport` | Action | actions | yes | Starts the Teleport spell to one of the character's attuned aetherytes (or free-company/private estate and apartment entries), exactly like choosing it in the Teleport window. |
| `get_conditions` | Read | character | no | The game's condition flags (what state the client is in). |
| `get_job_gauge` | Read | character | yes | The current job's gauge (the job-specific resource UI: e.g. PLD oath, WAR beast gauge, BLM astral fire/umbral ice and polyglot, SAM sen/kenki, VPR rattling coils/serpent offerings, PCT palette/canvas/motifs). |
| `get_job_levels` | Read | character | yes | Every combat class/job, crafter and gatherer with the logged-in character's level and experience. |
| `get_player` | Read | character | yes | Snapshot of the logged-in player character. |
| `get_target` | Read | character | yes | What the player is targeting. |
| `read_chat` | Read | chat | no | Returns chat lines the plugin has captured since it loaded (not older history), oldest first. |
| `print_echo` | Ui | chat | yes | Prints a line into the user's OWN chat log only (tagged [MCP]); nobody else can see it and nothing is sent to the server. |
| `execute_command` | Action | chat | yes | Runs one slash command as if the user typed it into the chat box, e.g. "/gearset change 3", "/hudlayout 2", "/xlplugins" or another installed plugin's command. |
| `send_chat` | Chat | chat | yes | Sends one line of text that OTHER PLAYERS WILL SEE, on the chosen channel, exactly as if the user typed it into the chat box. |
| `get_dalamud_info` | Read | dalamud | no | Returns environment facts about this game client: dalamudVersion, dalamudApiLevel, dalamudScmVersion/gitHash/betaTrack when known, gameVersion (ffxiv) and expansionVersions, clientLanguage (game data language), dalamu… |
| `list_plugins` | Read | dalamud | no | Lists the Dalamud plugins installed in this game client. |
| `get_duty_state` | Read | duty | yes | Instanced-content status. |
| `get_action` | Read | gamedata | no | Details for one action id: name, tooltip description (plain text; dynamic values such as potency may appear as placeholders), icon, class/job and which classes/jobs can use it, level acquired, category (Spell, Weapons… |
| `get_duty` | Read | gamedata | no | Details for one duty (ContentFinderCondition id): name, description, content type, required level and item level, level/item-level sync, party size and role composition (tanks/healers/dps per party, number of parties)… |
| `get_item` | Read | gamedata | no | Full game-data record for one item id: name, description, icon, UI and market categories, item level, equip level and jobs, equip slots, rarity, stack size, flags (unique, untradable, marketable, HQ-able, collectable,… |
| `get_quest` | Read | gamedata | no | Static details for one quest id: name, level, allowed classes/jobs, expansion, journal genre/category/section, place name, issuer NPC with zone and map X/Y coordinates (when the issuer has a placement in game data), p… |
| `get_recipe` | Read | gamedata | no | Crafting recipe for an item (itemId) or a specific recipe (recipeId): craft type (Carpentry, Smithing, ... |
| `get_sheet_row` | Read | gamedata | no | Reads one row of any game Excel sheet by sheet name and row id and returns it as JSON: numbers/bools as values, text as plain strings, RowRef links as {rowId, sheet, name} (name is the linked row's Name/Singular when… |
| `list_sheets` | Read | gamedata | no | Lists game Excel sheets that have typed column definitions (Lumina.Excel.Sheets), optionally filtered by nameContains, with row count, whether rows have subrows, and column names with types (string, uint8..int64, floa… |
| `search_actions` | Read | gamedata | no | Searches actions players can learn (weaponskills, spells, abilities, role actions, gathering abilities, PvP actions; from the Action sheet — crafting actions such as Basic Synthesis live in the CraftAction sheet, see… |
| `search_duties` | Read | gamedata | no | Searches duties from the Duty Finder data (ContentFinderCondition: dungeons, guildhests, trials, raids, alliance raids, PvP, deep dungeons, variant/criterion, etc.) by name (ranked exact > prefix > word > substring; n… |
| `search_items` | Read | gamedata | no | Searches every item in the game data (not the player's inventory; use find_owned_items for that) by name in the client language, ranked exact match > prefix > word prefix > substring > all words present; a numeric que… |
| `search_quests` | Read | gamedata | no | Searches quests by name (ranked exact > prefix > word > substring; a numeric query matches the quest id). |
| `search_recipes` | Read | gamedata | no | Searches crafting recipes by the crafted item's name (ranked exact > prefix > word > substring; numeric query matches the recipe id), optionally filtered by craftType (crafter name like "Weaving"/"Weaver", abbreviatio… |
| `search_sheet` | Read | gamedata | no | Scans one column of any Excel sheet and returns matching rows as {rowId, subrowId, label, value}, where label is the row's Name/Singular when it has one. |
| `find_owned_items` | Read | inventory | yes | Searches every loaded container (bags, equipped, armory, crystals, currency, key items, saddlebags if opened this session, and the currently/last opened retainer's inventory, equipment and market listings) for items b… |
| `get_currencies` | Read | inventory | yes | The character's currency balances: gil; Grand Company seals for the current company with its cap; and a list of currencies with category (common: ventures, MGP; tomestone: every current tomestone with weeklyAcquired/w… |
| `get_equipment` | Read | inventory | yes | The character's currently equipped gear: for each occupied slot (MainHand, OffHand, Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, RingRight, RingLeft, SoulCrystal) the item id, name, item level, equip level, HQ,… |
| `get_inventory` | Read | inventory | yes | Lists items in the character's containers, slot by slot. containers selects groups: bags (4 main inventory pages), equipped, armory (armoury chest), crystals, currency, keyItems, saddlebag, premiumSaddlebag, retainer… |
| `get_retainers` | Read | inventory | yes | The character's retainers in display order: id, name, whether the slot is available (subscription), class/job and level, gil held, number of items in its inventory and on the market board, market listing expiry, marke… |
| `get_server_info` | Read | meta | no | Describes this XivMcp server: plugin version, endpoint, running state, connected clients, which permission tiers are currently allowed (read, ui, action, chat — and whether Action/Chat calls need in-game approval), ea… |
| `list_status` | Read | meta | no | Returns every entry on the in-game agent board (newest update first) with agent, status, state (running\|done\|failed\|info), progress, detail, the MCP client that posted it and seconds since its last update (ageSeconds). |
| `clear_status` | Ui | meta | no | Removes your entry (pass agent) or every entry (omit agent) from the in-game agent board. |
| `post_status` | Ui | meta | no | Shows your progress inside the player's game: creates or replaces the board entry for `agent` (one entry per agent name, case-insensitive) in the XivMcp window and Umbra toolbar widget. |
| `get_party` | Read | party | yes | The player's party. mode is solo \| party \| crossRealmParty \| alliance. members (the 8-slot party list; empty when solo) each have index, name, contentId (string), entityId, homeWorld, job {abbreviation, name, role}, l… |
| `get_collection_progress` | Read | progress | yes | Unlock progress for one collection kind: mounts, minions, orchestrion (orchestrion rolls), emotes, fashionAccessories (also accepted as ornaments), triadCards (Triple Triad cards), bardings (chocobo barding), glasses… |
| `get_quest_status` | Read | progress | yes | For each quest id (Quest sheet row id; short ids below 65536 are accepted): whether the character has completed it, whether it is currently accepted (in the journal) and its current sequence step, plus name and whethe… |
| `get_addon_text` | Read | ui | yes | Reads every text string shown in one game UI window (addon), including text inside nested components such as lists, buttons and tabs, in reading order (top-to-bottom, left-to-right by screen position). |
| `get_dialogue` | Read | ui | yes | Returns whatever conversation or prompt windows are currently visible, read-only: talk (NPC speaker + dialogue text of the current Talk box), subtitle (cutscene subtitle), selectString / selectIconString (the option l… |
| `list_addons` | Read | ui | yes | Lists the game's loaded UI windows ("addons") with their internal names, which get_addon_text needs. |
| `set_map_flag` | Ui | ui | yes | Places the user's map flag marker (the one shown on the map/minimap and inserted by <flag> in chat) and by default opens the map window on it. |
| `show_notification` | Ui | ui | no | Shows a Dalamud overlay notification card (bottom-right corner, with title, text and a coloured icon for the type) visible only to the user; works on the title screen too. |
| `show_toast` | Ui | ui | yes | Shows a short, transient on-screen message using the game's own toast styles, visible only to the user: normal (small banner near the top of the screen), quest (large centred quest-style text with a chime), error (red… |
| `get_location` | Read | world | yes | Where the player is. |
| `get_time` | Read | world | no | Current Eorzea time and the real-world reset schedule. |
| `get_weather_forecast` | Read | world | no | Weather forecast for a zone computed with the game's own deterministic weather algorithm (weather changes every 8 Eorzea hours = 23m20s real time, at ET 00:00, 08:00 and 16:00). |
| `list_aetherytes` | Read | world | yes | The player's teleport list (the in-game Teleport window): every attuned aetheryte plus housing destinations (own/FC house, shared estates, apartments). |
| `list_fates` | Read | world | yes | FATEs currently known in the player's zone, nearest first. |
| `list_nearby_objects` | Read | world | yes | Game objects loaded around the player (the client only knows objects within roughly 100 yalms, fewer in crowded areas), sorted nearest first; the local player is excluded. |

### Resources

Resources and templates follow the Read tier and their category.

| URI | Category | Login | What |
| --- | --- | --- | --- |
| `ffxiv://player` | character | yes | Same JSON as the get_player tool: the logged-in character's identity, job, level, HP/MP, position, statuses. |
| `ffxiv://target` | character | yes | Same JSON as get_target (target, target of target, soft, focus, mouseover). |
| `ffxiv://chat/recent` | chat | no | The newest 100 captured chat lines (oldest first) in the same shape as read_chat, excluding private tells and battle-log lines. |
| `ffxiv://item/{itemId}` | gamedata | no | Game-data record for an item id (same content as the get_item tool). |
| `ffxiv://sheet/{sheet}/{rowId}` | gamedata | no | One Excel sheet row as JSON (same content as get_sheet_row with default options). |
| `ffxiv://inventory` | inventory | yes | Main inventory bags (4 pages) with per-container usage; updated notifications are sent when the inventory changes. |
| `ffxiv://agents` | meta | no | JSON snapshot of the in-game agent board (same shape as list_status). |
| `ffxiv://party` | party | yes | Same JSON as get_party. |
| `ffxiv://location` | world | yes | Same JSON as get_location. |

### Prompts

Prompts only return instructions; every game interaction still goes through tools and their tiers.

| Prompt | Category | Arguments | Workflow |
| --- | --- | --- | --- |
| `character_overview` | prompts | — | Summarize the logged-in character: jobs and levels, current gear, location, party and notable currencies. |
| `crafting_plan` | prompts | `item`, `quantity?` | Plan how to craft an item: full ingredient tree, what is already owned, what to gather or buy, and crafter level checks. |
| `duty_prep` | prompts | `duty` | Prepare for a duty: requirements vs. my character, party composition, gear readiness and useful reminders. |
| `gear_audit` | prompts | `job?` | Audit equipped gear for a job: item level outliers, missing materia or upgrades available in inventory or gearsets. |
| `situation_report` | prompts | — | What is going on around me right now: zone, Eorzea time, weather, active FATEs, duty and party state, recent chat. |
| `where_is` | prompts | `target` | Locate a nearby object, NPC, player or a place and explain how to get there, optionally flagging the map. |

<!-- END GENERATED CATALOG -->

## Development

```sh
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
~/.dotnet/dotnet build XivMcp.slnx -c Release
```

Build output goes to `../xiv-mcp-build/artifacts` (override with `XIVMCP_ARTIFACTS`), never into the
source tree. Writing a provider: [docs/PROVIDERS.md](docs/PROVIDERS.md).

```sh
~/.dotnet/dotnet test tests/XivMcp.Core.Tests -c Release     # protocol, transport, approval hook (no Dalamud)
~/.dotnet/dotnet test tests/XivMcp.Plugin.Tests -c Release   # plugin logic without the game (needs Dalamud dev hooks)
~/.dotnet/dotnet run --project tools/catalog -c Release -- readme --write README.md   # regenerate the catalog above
```

Host tests prove only what they run; nothing in them loads the plugin in FINAL FANTASY XIV.

### Changelog

`changelog.json` at the top of the repository is the changelog, and the only place entries are
written. Both places a user reads it come from that one file: the plugin embeds it
(`XivMcp.Core.Changelog`) and shows it in the **What's new** tab, and
[CHANGELOG.md](CHANGELOG.md) is rendered from it.

```sh
tools/changelog.py           # rewrite CHANGELOG.md from changelog.json
tools/changelog.py --check   # fails, with a diff, when they drift
```

The convention, and it is not optional: **every change a user can see adds or edits its entry in
`changelog.json` in the same commit as the change**, and regenerates `CHANGELOG.md`. Never edit
`CHANGELOG.md` by hand. `tests/XivMcp.Core.Tests` runs the check (it shells out to
`tools/changelog.py`, so there is only one renderer), which means `dotnet test` and any CI that runs
the tests catch drift.

A status word means exactly the same thing here as in Ghostty for FFXIV's changelog, and nothing
more:

| Status | Shown | Means |
| --- | --- | --- |
| `next` | SOON | still being built, on a branch; not merged. |
| `beta` | BETA | merged, but **not yet verified in game**. |
| `new` / `fix` | NEW / FIX | in a numbered release: seen working in game. |

An entry keeps its `beta` until the thing it describes has been observed working in the game. Say
what is unverified in the entry itself rather than writing around it.

## Troubleshooting

- **"Could not start on http://127.0.0.1:41800/mcp: ... address already in use"** — another process
  (or a previous plugin instance that did not unload) holds the port. Change the port in Settings or
  restart the game.
- **A provider shows "failed to load"** — the Status and Tools tabs list the exception; the other
  providers keep working. `/xllog` has the stack trace.
- **Action/Chat tools missing** — they are off by default (Settings → Permissions). `get_server_info` reports the
  enabled tiers. With confirmation on, a call that nobody approves in game fails after the auto-deny time.
- **Claude Code says the connection failed** — the helper exits non-zero if the plugin config does not
  exist yet (load the plugin once) or has no token. Run
  `~/.local/share/xiv-mcp/headers-helper >/dev/null && echo ok` to check without printing the token.
