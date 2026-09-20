# TODO: what is still open

> Rewritten 2026-09-20, after the four parked branches were landed on `master`. Nothing anywhere in this
> file has been verified inside the running game. "Tested" means host-side tests only.
> The rules for all of it are in [HARD-LINES.md](HARD-LINES.md) and [PROVIDERS.md](PROVIDERS.md).

Adding a tool: build the plugin with warnings as errors, run the tests (`ToolContractTests` is the gate
check), regenerate the catalogue with `dotnet run --project tools/catalog -c Release -- all --write .`
(CI fails while `docs/tools.json`, `docs/TOOLS.md` or the README table is stale), add a `beta` line to
`changelog.json` and run `tools/changelog.py`. Regenerate the catalogue **before** running the tests:
`tests/XivMcp.Standalone.Tests` compares what the standalone host serves against `docs/tools.json`.

## Landed on master

| Feature | State |
| --- | --- |
| The single approval checkbox (*Ask me before anything changes*), one gate for every mutating tool, approval sentences, action log, toasts when approval is off | tested; not seen in game |
| Core: error codes, per-client rate limit, data sources / availability / approval metadata, external tools, control routes, catalogue export | tested |
| Standalone pre-game host `xiv-mcp-standalone`, hand-off by yielding the port | tested between native processes only. **Next:** run it once against a real game install (Lumina.Excel from NuGet is 7.5.0, Dalamud ships 7.5.1), and try the hand-off with the real plugin under Wine — does a bind from inside Wine fail against the native listener? |
| Generated `docs/tools.json`, `docs/TOOLS.md`, README table, CI check | tested |
| Gated game tools: `open_game_window`, `write_macro`, `clear_macro`, `target_party_member` | tested. **Next:** in game, check each agent "open" call, that a written macro persists to MACRO.DAT, and the party-list row order. Open points: the `item` detail window has no open API (left out); `write_macro` rejects `lines` over 500 characters of JSON because the approval sentence truncates there (raise the cap in `McpServer.RenderApprovalSummary` for that case); named linkshell/CWLS chat channels not added (slot mapping could not be confirmed). |
| The mod-family bridges (Ghostty, XivDesktop, Almanac), category `bridges` | tested. **Next:** no sibling gate has ever been called for real. The tools written against IPC the siblings do not have yet answer `unavailable` until those ship; see below. |
| Static game-data tools and their templates and prompts | tested. **Next:** check these guesses against real data: the Grand Company supply slot to ClassJob mapping, the meaning of MapMarker DataType 4, roulette matching by English name, and that `get_recipe` output is unchanged after its move onto the shared `RecipeTree` code. Monster drops and quest hand-ins are not in the sheets and are not covered. |
| Live read-only game state: `get_quest_journal`, `list_hotbars`, `get_duty_unlocks`, `list_social_groups`, `get_enmity_list`, `get_character_sheet`, `get_zone_live`, resource `ffxiv://zone/current` | tested. **Next:** every ClientStructs member these read is unverified in game. |
| Dalamud tools (read and gated) | tested. **Next:** see the section below. |

## Dalamud internals

`set_plugin_enabled`, `reload_plugin`, `list_plugin_repositories`, `add_plugin_repository`, the update
flags in `list_plugins` and `get_plugin_stats` reach Dalamud members that have no public API. All of it
goes through `Providers/Dalamud/DalamudInternals.cs` and nowhere else:

* every reflected name is in `DalamudInternals.Names`, and `DalamudInternalsTests` checks that table —
  and the shapes the code assumes — against the `Dalamud.dll` the build references, so a Dalamud release
  that renames or reshapes one fails CI instead of turning into a silent "unavailable" in game. It passed
  against Dalamud 15.0.3.5 on 2026-09-20;
* at run time each feature calls `FirstMissing(...)` first and returns null when this build lacks
  anything it needs, and the tool then answers `unavailable` naming what is missing. Property reads and
  the `Service<T>` lookup swallow reflection failures rather than propagating them, so nothing here
  throws into a game frame. A failure *inside* a Dalamud call that does exist (a plugin throwing while it
  loads) is deliberately surfaced as the tool's error, because that is the answer the caller wants.

**Next:** none of this has run against a live Dalamud. When it is first tried in game, check
`set_plugin_enabled` against a plugin in a non-default collection and `reload_plugin` on a dev plugin.

## Not landed

| Group | State | Next concrete step |
| --- | --- | --- |
| Sibling-mod IPC additions | designed, and patches written for each sibling repo (not applied, not built, not run): see the owner's `xivmcp-sibling-ipc-2026-09-20` working directory. **Ghostty** `GhosttyDalamud.v1.Call`: `api.version`, `theme.list` / `theme.set`, `capture.shot` / `capture.clip` / `capture.status`, `selftest.run` / `selftest.report`, `leakwatch.report`, `gallery.share`. `layout.get` / `layout.set` were **not** written: `/term pin` has no absolute-world-position form, so a saved anchor cannot be written back as arguments that reproduce it — that is a feature to design, not a gate to wire up. **XivDesktop**: `XivDesktop.v1.ApiVersion`, `.Panels`, `.Rescan`. **Almanac**: the `Almanac.v1.Call` gate with all twelve verbs, and deliberately no verb that submits results to a leaderboard. | Apply each patch in its own repo, build it there, then try the bridge tools against it. `get_terminal_layouts` and `apply_terminal_layout` stay `unavailable` until Ghostty grows a way to reproduce a placement. |
| Settings: rate limit | master has the setting (`RateLimitPerMinute`), no UI control | Add an input under Advanced in `MainWindow.Settings.cs`. |
| Minisite | nothing to change in data (it lists no tools) | The plugin description there says changes are "approved in game first"; with the checkbox that is "approved in game first unless you switch approval off". |

## Known flake

`XivMcp.Core.Tests.LegacyProtocolTests.StandaloneStreamResumesWithLastEventId` timed out waiting for an
SSE event once on a loaded build machine on 2026-09-20 and passed on every other run that day. If it
recurs, the timeout in `tests/XivMcp.Core.Tests/Infrastructure/TestServer.cs` is the thing to look at.

## Deliberately not built, and not to be picked up

Combat or rotation helpers, gathering/crafting/fishing loops, movement or targeting bots (`target_party_member`
takes one named member per approved call, no filters), market sniping or buying, packet work, anything that clicks
inside a game window (`FireCallback`), silent plugin install/update or silent repository edits, a cooldown-polling
tool, typing text into a terminal from a client, auto-submitting Almanac benchmark results.
