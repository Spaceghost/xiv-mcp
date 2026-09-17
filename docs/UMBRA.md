# Umbra integration (`Umbra.XivMcp.dll`)

An optional [Umbra](https://github.com/una-xiv/umbra) custom plugin that adds two toolbar widgets
for the XivMcp MCP server. It is a separate DLL, loaded by Umbra rather than by Dalamud. It has **no
assembly reference to XivMcp**: it only talks to it over Dalamud IPC (`src/Shared/IpcContract.cs`).

> **Status:** this has been built and host-tested only (payload parsing, text formatting, and a
> stylesheet parse against the real `Una.Drawing.dll`). **It has not been loaded in the game yet.**
> Layout, colours, glyph rendering and the IPC round-trip with a live XivMcp are unverified until
> someone loads it in Umbra.

## Requirements

- Umbra installed through Dalamud. The build references the newest
  `~/.xlcore/installedPlugins/Umbra/<version>/` (3.1.18.0 at the time of writing). Umbra refuses
  plugins built against an older minor version of its assemblies, so rebuild after an Umbra update.
- Dalamud dev assemblies in `~/.xlcore/dalamud/Hooks/dev/` (API 15).
- .NET SDK 10 (`~/.dotnet/dotnet`).
- The XivMcp Dalamud plugin, for the widgets to show anything useful. Without it they still load
  and show that XivMcp is missing.

## Build and install

```sh
tools/install-umbra.sh             # build Release; prints the DLL path and its Z:\ path
tools/install-umbra.sh --install   # also copies Umbra.XivMcp.dll (+ .pdb) to ~/umbra-plugins/
```

Overrides: `UMBRA_PLUGIN_DIR`, `UMBRA_LIB_PATH`, `DALAMUD_LIB_PATH`, `XIVMCP_ARTIFACTS`, `DOTNET`
(see `--help`). The script only copies files. It never edits Umbra or Dalamud configuration.

Then, in the game (one-time):

1. Open Umbra Settings → **Plugins**. Custom plugins are off until you accept the third-party plugin
   warning ("I have read and agreed to the above statement").
2. **Install from file** → **Browse...** → pick `Z:\home\<you>\umbra-plugins\Umbra.XivMcp.dll`
   (Wine maps `/` to `Z:`). Umbra stores this path and reloads the DLL from it on every start.
3. **Restart Umbra** (settings window button, or reload the plugin). Umbra says "Umbra needs to be
   restarted to load this plugin" until then.
4. Umbra Settings → **Toolbar Widgets** → add **MCP Server** and/or **MCP Agents**.

To update, rebuild with `--install` and restart Umbra. Umbra reads the DLL from a stream, so the file
is not locked while the game runs.

## Widgets

### MCP Server

| Label | Meaning |
| --- | --- |
| `MCP ● 2` | Server running, 2 MCP sessions connected (`▶ 1` is added when an agent is running) |
| `MCP ○` | XivMcp loaded, HTTP server stopped |
| `MCP —` | XivMcp not loaded (not installed, disabled, or still starting) |
| `MCP !` | XivMcp loaded but IPC version mismatch, or an IPC call failed (the tooltip says which) |

- **Icon:** game icon 35 (the main-command monitor icon). You can change it with the standard Umbra
  icon settings.
- **State cues:** green label while running, grey when stopped or missing, amber on IPC problems.
  The icon greys out while the server is not running. Both can be turned off, and a custom text
  colour turns off the tinting.
- **Tooltip:** endpoint, session count, connected client names, request and failure counters,
  running agents, last error.
- **Left click:** opens the popup.
- **Right click** (configurable): toggle the XivMcp window (default), start/stop the server, or
  nothing. Ctrl/Shift+right-click is left alone because Umbra uses it for quick settings. Middle
  click takes the same options and does nothing by default.
- **Options:** show session count, show running-agent count, compact mode (drops the `MCP` prefix),
  colour by state, grey icon when off, tooltip on/off, right/middle-click actions. The standard
  two-line layout ("Show subtext") adds `N agents running` or `N sessions · N calls`.

### MCP Agents

`Agents 2 · 45%` means 2 agents report state `running`, with an average progress of 45% across the
running agents that report progress. The standard Umbra progress bar under the label shows the same
average. The second line (optional) shows the newest running agent and its status. You can hide the
widget while no agent is running. Right click works the same way as on MCP Server.

### Popup

Both widgets open the same popup. Its sections can be switched on and off per widget instance under
the "Popup" category:

- **Header:** state dot, `MCP Server · Running`, endpoint (or the problem text), and a
  **Start/Stop** button (`SetRunning`).
- **Server details:** sessions, requests and failures, last error, connected clients, enabled
  permission tiers.
- **Agents:** newest first, up to N (default 8, maximum 20). Each row shows a state-coloured bar
  (running green, done blue, failed red, info grey), agent name, progress % and status text, age
  (`42s`, `5m`, …), a progress bar when progress is reported, and a detail line.
- **Recent activity:** the last N requests (default 10, maximum 25). Each row shows local time, an
  ok/fail dot (hover a failed row for the error), client name, method and target, and duration.
- **Buttons:** **Open XivMcp window** (`ToggleWindow`) and **Copy endpoint**.

The MCP Agents popup starts with only the header and agent board.

## IPC dependency

The plugin subscribes to every gate in `IpcContract` with these exact Dalamud generic signatures.
XivMcp's providers must use the same ones:

| Gate | Subscriber type | Use |
| --- | --- | --- |
| `XivMcp.ApiVersion` | `ICallGateSubscriber<int>` | Presence (`HasFunction`) and version check against `IpcContract.Version` |
| `XivMcp.GetStatus` | `ICallGateSubscriber<string>` | Status JSON |
| `XivMcp.GetActivity` | `ICallGateSubscriber<int, string>` | Called with 25 |
| `XivMcp.GetAgentBoard` | `ICallGateSubscriber<string>` | Board JSON |
| `XivMcp.SetRunning` | `ICallGateSubscriber<bool, bool>` | Start/Stop button and optional click action |
| `XivMcp.ToggleWindow` | `ICallGateSubscriber<object>` → `InvokeAction()` | Window button and default right click |
| `XivMcp.Changed` | `ICallGateSubscriber<object>` → `Subscribe(Action)` | Triggers a refresh |

Behaviour:

- **Refresh:** runs on the framework thread. It runs on the next tick after `Changed` (at most every
  100 ms), and also polls every 1 s as a fallback. `GetActivity` is only called while a popup is
  open. The `Changed` handler only sets a flag, so it is safe from any thread and never throws into
  XivMcp.
- **Missing XivMcp:** if `ApiVersion` has no function registered, the widgets show `—` / "not
  installed". The poll keeps checking, and the `Changed` subscription is retried until it sticks.
  Dalamud keeps subscriptions on the named channel, so they survive XivMcp reloads.
- **Version mismatch:** if `ApiVersion` ≠ `IpcContract.Version`, no other gate is called. The
  widgets show `!` and the tooltip and popup name both versions.
- **Failures:** every IPC call is wrapped. A failing `GetStatus` gives the `!` state. A failing
  `GetActivity` or `GetAgentBoard` keeps the status visible. Problems are logged once each to the
  Dalamud log (`/xllog`), prefixed `[Umbra.XivMcp]`.
- **Payloads:** parsing is lenient. Property names are case-insensitive, and unknown fields are
  ignored. Timestamps (`timestamp`, `updatedAt`) can be ISO-8601 strings or Unix seconds or
  milliseconds. Agent `progress` is treated as a 0..1 fraction up to 1, and as a
  0..100 percentage above 1.
- **Unload:** on Umbra restart or unload, the `Changed` subscription is removed. Umbra loads plugins
  into a collectible load context, so a leftover delegate would pin it.

## Source layout

`src/XivMcp.Umbra/`:

- `XivMcp.Umbra.csproj`: net10.0-windows. Umbra and Dalamud references use `Private=false`. The
  newest Umbra version directory is picked at build time.
- `IpcPayloads.cs`: records and the JSON parser. No Dalamud or Umbra types.
- `McpFormat.cs`: labels, tooltip, ages, colours. No Dalamud or Umbra types.
- `XivMcpClient.cs`: `[Service]` with the IPC subscribers, snapshot cache, and refresh loop.
- `McpServerWidget.cs`, `McpAgentsWidget.cs`: `StandardToolbarWidget`s.
- `McpPopup.cs`: `WidgetPopup` built from Una.Drawing nodes with pooled rows.
