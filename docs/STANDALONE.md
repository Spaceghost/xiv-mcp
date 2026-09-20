# The standalone host (before the game starts)

> Host-tested only. The hand-off to the in-game plugin has been exercised between two processes on one
> machine in `tests/XivMcp.Standalone.Tests`; it has **not** been observed with the real plugin inside the
> running game, under Wine or on Windows.

`xiv-mcp-standalone` is the same MCP server as the plugin — same Core, same tool names and schemas, same
port, path and tokens — for the time the game is **not** running. Planning a craft, reading a quest chain,
looking up where an item comes from or when the weather turns does not need a character on screen.

* Tools that need only the installed game data run for real: items, recipes and recipe trees, item sources
  and uses, quests, duties, actions, gathering, zones and aetherytes, any Excel sheet, weather forecasts and
  windows, Eorzea time and reset timers, the planning prompts and the `ffxiv://item/…`-style resources. In
  [tools.json](tools.json) these are `"availability": "static"`.
* Every other tool is **still listed**, with its real schema, and answers with a structured error:

  ```json
  { "isError": true,
    "content": [{ "type": "text", "text": "Tool 'get_player' needs the running game: FINAL FANTASY XIV is not running …" }],
    "_meta": { "dev.xivmcp/error": { "code": "game_not_running", "retryable": true, "message": "…" } } }
  ```

* `get_server_info` reports `"host": "standalone"`, `"gameRunning": false`.

The static providers are not a copy: `src/XivMcp.Standalone` compiles the plugin's own source files
(`Providers/GameData/**`, the time and weather provider, the prompts) against Lumina from NuGet, pinned to the
version Dalamud ships. A file in those folders that needs Dalamud must be named `Dalamud*.cs`; anything else
that reaches for Dalamud breaks this project's build. `ServedToolsMatchThePluginCatalogueExactly` fails if a
served tool's schema, tier or hints drift from `docs/tools.json`, or if a tool marked static is not served.

## Running it

```sh
dotnet publish src/XivMcp.Standalone -c Release -r linux-x64 --self-contained false -o ~/.local/lib/xiv-mcp
~/.local/lib/xiv-mcp/xiv-mcp-standalone            # finds the game and the plugin's settings by itself
```

(`-r win-x64` on Windows; `--self-contained true` if the .NET 10 runtime is not installed.)

| Option | Default |
| --- | --- |
| `--game PATH` / `$XIVMCP_GAME` | XIVLauncher's configured `GamePath`, `~/.xlcore/ffxiv`, the Flatpak launcher's data directory, Steam's library, the Windows default install. The install root, its `game` directory or `sqpack` itself are all accepted. An explicit path that is wrong is an error, not a fallback. |
| `--language en\|ja\|de\|fr` / `$XIVMCP_LANGUAGE` | `en` |
| `--port`, `--path` | the plugin's saved settings, else `41800` and `/mcp` |
| `--bind loopback\|loopback+tailnet\|tailnet\|ADDRESS` | the plugin's bind mode. Anything off this machine refuses to start without a token. |
| `--token-file F`, `$XIVMCP_TOKEN` | see below |
| `--no-token` | loopback only |
| `--rate-limit N` | 600 calls a minute per client |
| `--catalogue` | print the catalogue this host would serve and exit |

**Tokens.** Settings are layered, later wins: the plugin's saved configuration (`pluginConfigs/XivMcp.json`, read
only — main token, per-client token hashes, port, path, bind mode, origins), the provisioning file
(`~/.config/xiv-mcp/provision.json`, the same one the plugin reads), then `$XIVMCP_TOKEN` / `--token-file`. So a
client configured for the plugin works unchanged, including per-client tokens. The token is never printed or
logged. With no game installed on the machine, the static tools answer `unavailable` instead.

## Hand-off when the game starts

Two processes cannot listen on one port, and a client should not have to care which one is up. The two hosts
settle it between themselves; **nothing is proxied**:

1. The standalone holds the port. Both hosts answer `GET {path}/host` with
   `{server: "xiv-mcp", host: "plugin" | "standalone", version, catalogueVersion, gameRunning}`.
2. The plugin starts and its bind fails with *address in use*. It sends `POST {path}/handoff` with the bearer
   token. The standalone accepts that only from this machine, answers `{yielding: true}`, stops listening, and
   the plugin binds (it retries for three seconds). Anything else holding the port is reported as the bind
   error it always was.
3. While yielded, the standalone probes the port every two seconds. When it has been free three times in a
   row — the game has exited, not just restarted its listener — it binds again. If it ever wins a race against
   a restarting plugin, the plugin's next start simply asks again.

Why yield rather than proxy: while the game runs there is then exactly one listener and no second hop, so
sessions, SSE streams, per-client tokens and the approval flow's view of the caller are all untouched, and a
crashed standalone cannot take the in-game server with it. The cost is that MCP sessions do not survive the
switch: a legacy (2025-era) client gets *session not found* once and re-initialises; stateless 2026-07-28
requests do not notice.

## As a user service

Linux (systemd user unit, `~/.config/systemd/user/xiv-mcp.service`):

```ini
[Unit]
Description=xiv-mcp standalone host (FFXIV game data over MCP while the game is closed)

[Service]
ExecStart=%h/.local/lib/xiv-mcp/xiv-mcp-standalone
Restart=on-failure
RestartSec=5
# Optional: Environment=XIVMCP_GAME=/path/to/ffxiv   Environment=XIVMCP_LANGUAGE=en
# A token that is not in the plugin's configuration belongs in a file, not in the unit:
# EnvironmentFile=%h/.config/xiv-mcp/standalone.env   (XIVMCP_TOKEN=..., mode 0600)

[Install]
WantedBy=default.target
```

```sh
systemctl --user daemon-reload && systemctl --user enable --now xiv-mcp
journalctl --user -u xiv-mcp -f
```

Windows (Task Scheduler, runs at log-on, no console window needed):

```powershell
$exe = "$env:LOCALAPPDATA\xiv-mcp\xiv-mcp-standalone.exe"
Register-ScheduledTask -TaskName "xiv-mcp standalone" -Trigger (New-ScheduledTaskTrigger -AtLogOn) `
  -Action (New-ScheduledTaskAction -Execute $exe) -Settings (New-ScheduledTaskSettingsSet -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1))
```

The service needs no elevated rights, writes nothing (apart from `--token-file` creating its file once) and
opens the game data read-only, so it can stay enabled while the game is patched; restart it after a patch to
pick up new sheets.
