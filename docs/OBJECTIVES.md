# Custom objectives

> **Status.** The logic (time windows, weather, next window, packs, persistence, progress, tool validation) has host
> tests. The overlay, the map flag click, the toasts and the chat command have **not been observed in the game**. Treat
> everything under "In game" as the design until the checklist at the end has been run.

Agents (and the player) can post objectives that look and behave like tracked quests: a title, the current step, a live
"ready now" / "next window in N min" line, a click that places the map flag, and the game's own quest toasts when one is
added or completed.

## Why they are not real quests

"Can it integrate perfectly into the game quest list?" No. Quests in the Journal, and the ones the Duty List tracks,
come from the server: the client only mirrors quest state it is sent (`QuestManager`, `QuestTodoList` in
FFXIVClientStructs). A client cannot add a quest to the Journal, and faking one inside the client's quest structures
would feed invalid quest ids to game code that talks to the server. XivMcp never writes to quest state. Custom objectives
are plugin data, stored next to the plugin config, visible only to you.

## How they appear: overlay under the Duty List

Two ways were considered.

**(a) Inject native nodes into the Duty List (`_ToDoList`).** Allocate text and image nodes, link them under a
"Custom" header in the addon's node tree, register collision and click events, keep them in place as the list changes,
and unlink them before the addon is torn down and on unload.

**(b) A native-styled overlay pinned under the Duty List.** Read where the Duty List is and draw the entries just below
it in the game's font and colours, without changing the addon.

XivMcp uses **(b)**. Reasons, from what was checked for Dalamud 15.0.3.5 (API 15) and its FFXIVClientStructs:

- `AddonToDoList` (1240 bytes) builds its rows itself from `ToDoListNumberArray`/`ToDoListStringArray` into a
  `ListPanel`, duty/FATE timer text nodes, `DutyFinderTextNodes`, `ObjectiveTimerTextNodes` and `FocusableNodes`. There
  is no slot for plugin rows. Injected nodes would have to be re-placed whenever the game re-lays out the list (quests
  collapse and expand, duties start, timers appear), which means hooking or following its update cycle through
  `IAddonLifecycle` (PostSetup, PostRequestedUpdate, PostRefresh, PreFinalize).
- Click handling on injected nodes needs a native `AtkEventListener` and event registration on the addon; a mistake
  there, or a node still linked when the addon finalizes (zone change, HUD layout change, plugin unload), crashes the
  game rather than failing softly.
- The usual way to do this safely is [KamiToolKit](https://github.com/MidoriKami/KamiToolKit) (used by plugins such as
  [VanillaPlus](https://github.com/MidoriKami/VanillaPlus)). It is a large dependency that has to be updated for game
  patches. Dalamud's own native overlay service ([goatcorp/Dalamud#2874](https://github.com/goatcorp/Dalamud/pull/2874))
  is still an open pull request and is not in Dalamud 15.0.3.5.
- None of (a) could be observed from here: the game was running but was off limits for this change. Native node code
  that has never run in game is the part most likely to crash it.

What (b) does to come close:

- `DutyListAnchor` reads `_ToDoList` every frame through `IGameGui.GetAddonByName`: position, scale, `RightAligned`,
  the bottom of its visible text (walking visible text nodes, including inside component nodes) and the text and edge
  colours of its entries. It only reads node fields: no native calls, no hooks, nothing attached. If the addon is rebuilt
  the next frame reads the new one; if it is hidden (cutscenes, HUD hidden, duty cutscenes) the overlay hides too.
- `ObjectiveOverlay` draws each objective with a quest icon, the title in the Duty List's title colour, the current step
  (`2/4  Capture: …`) and a status line, in the game's Axis font scaled by the Duty List's scale, with a one-pixel edge
  like game text. Nothing is drawn when there are no active objectives.
- Clicking an entry places the map flag and opens the map on it (`AgentMap.SetFlagMapMarker` + `OpenMap`, the same
  calls as `set_map_flag`). Right-click: step done, back one step, complete, reopen, place flag without opening the map,
  remove. Hover shows every step, where, the conditions and the next window in local time.
- The game's quest toast (`IToastGui.ShowQuest`) announces a new objective posted by an agent, and a completed one with
  the checkmark and sound. A normal toast says when an objective becomes ready.
- Settings → Advanced → *Custom objectives*: show/hide, pin under the Duty List (off: a movable window), ready toasts,
  keep completed ones listed.

If Dalamud ships a native overlay API, or KamiToolKit is adopted, (a) becomes a drop-in replacement for
`ObjectiveOverlay` + `DutyListAnchor`: the store, rules and tools do not change.

## Conditions

Every part is optional. An objective is **ready** when it is not complete and all given conditions hold:

| Condition | Source | Check |
| --- | --- | --- |
| Zone | `territoryId` (TerritoryType row id) | the current territory |
| Spot | map `x`/`y` as the game displays them, `radius` (yalms, default 30) | distance on the ground plane (X/Z) from the player to the spot, converted with the map's `SizeFactor`/offsets like `set_map_flag` |
| Eorzea time | `eorzeaTime` `"HH:MM-HH:MM"`, start inclusive, end exclusive; wraps past midnight (`"21:00-03:00"`); `"any"`, empty or equal start/end = all day | Eorzea time computed from the clock (the same one `get_weather_forecast` uses) |
| Weather | names such as `"Clear Skies"`; any listed one matches; `"any"` or empty = no constraint | in the zone: the live weather (`WeatherManager`); elsewhere: the forecast from the zone's `WeatherRate` and the game's weather algorithm (`GameMath.WeatherTarget`) |

Weather names match case-insensitively in the client language **and** in English, so English packs also work on
Japanese, German and French clients. A weather the zone can never have is reported as a problem ("Eastern La Noscea
never has Blizzards (possible: …)") instead of waiting forever; so is a weather condition on a zone with fixed weather.

**Window** = time and weather together. The status line shows, in order: `Complete`, a problem, `Ready now (N min
left)`, `Window open: travel to <zone>`, `Window open: N yalms to the spot`, `Next window in N min`. The next window is
the earliest real-time interval in the next 7 days where the Eorzea time window is open and the forecast weather
matches; its end is when the time window closes or the matching weather ends (consecutive matching 8-bell weather
periods are merged). The search works in whole real seconds and is checked second by second in the tests.

## MCP tools (category `objectives`)

| Tool | Tier | What |
| --- | --- | --- |
| `post_objective` | Ui | Create or replace (same id): `id`, `title`, `steps[]`, `territoryId`, `x`, `y`, `zone`, `spot`, `description`, `eorzeaTime`, `weather[]`, `radius`. Re-posting identical steps keeps progress; changed steps reset it. |
| `update_objective` | Ui | `advance` (current step done; after the last step the objective completes), `step` (0-based step to make current), `complete` (true/false), `note` (short line under the step, `""` clears). |
| `list_objectives` | Read | All objectives with steps and live status (`ready`, `summary`, `windowOpen`, `inZone`, `nearSpot`, `distanceYalms`, `currentWeather`, `nextWindowStartUtc`, `nextWindowEndUtc`, `minutesUntilNextWindow`, `problem`). |
| `clear_objectives` | Ui | One id, completed only, or all. |
| `load_objective_pack` | Ui | A pack from `json` text or a `path` on the player's machine. |
| `ffxiv://objectives` | Read | Same as `list_objectives`, with `resources/updated` on every change. |

Limits: 200 objectives, 30 steps of 300 characters, title 120, description 2000, ids up to 64 characters of `a-z 0-9 - _ .`
(other characters become `-`, upper case is lowered).

## Quest packs

A pack is JSON: `{"quests": [...]}`, `{"objectives": [...]}` or a bare array, at most 1 MiB and 200 entries. Each entry
reads `id`, `name` (or `title`), `zone`, `territoryId`, `spot`, `map {x, y}` (or `x`/`y`), `eorzea_time` (or
`eorzeaTime`), `weather` (array or string), `radius`, `description`, and either `steps` (strings or `{text}`) or
`setup[]` + `capture`, which become the steps `Go to <spot>`, each setup line, and `Capture: <capture>`. Other fields
(`kind`, `use`, `features`, top-level `about`, `homepage_video`, …) are ignored. Invalid entries are skipped with a
message; the rest load. Reloading a pack replaces entries by id and keeps progress where the steps did not change.

The first real pack is ghostty-dalamud's `docs/media/shot-quests.json` (16 media shots around La Noscea); it loads
without errors.

Paths: under Wine, `/home/you/pack.json` and `~/pack.json` are opened on the `Z:` drive (the host root); Windows paths
pass through. Only `.json` files are read.

## Chat command

`/xivmcp quests [list | done <id> | undo <id> | next <id> | flag <id> | load <file> | clear-done | remove <id> | show | hide]`

- `list` (or no argument): every objective with its current step and status line.
- `next <id>`: current step done. `done <id>`: complete. `undo <id>`: back one step, or reopen a completed one at its
  last step.
- `flag <id>`: place the map flag and open the map.
- `load <file>`: load a pack (reports skipped entries).
- `clear-done`, `remove <id>`, `show`, `hide`.

## Storage

`pluginConfigs/XivMcp/objectives.json` (`{"version": 1, "objectives": [...]}`), written atomically (temp file + move) on
every change. A file that cannot be read is kept as `objectives.json.bad` and the list starts empty (the Dalamud log
says why). Objectives are local to this machine and never sent anywhere.

## Not verified in game — checklist

1. The overlay sits just below the Duty List's last visible line at 100 %, 150 % and 200 % HUD scale, with the Duty List
   left- and right-aligned, and moves with it when the Duty List grows or shrinks (accept/finish a quest, start a
   duty).
2. The overlay hides with the Duty List (cutscene, Scroll Lock HUD hide, duty cutscene) and when the Duty List is turned
   off in the HUD layout; the unpinned window still works then.
3. The sampled colours look like Duty List quest text; the Axis 14 font size matches Duty List text (adjust if not).
4. Icon 71021 is a sensible quest marker (swap `ObjectiveOverlay.QuestIconId` if not).
5. Clicking an entry places the flag in the right spot of the right zone and opens the map; right-click menu works; the
   click does not also go through to the game.
6. Quest toasts: new objective (from `post_objective`), completed objective (checkmark and sound); ready toast once per
   transition.
7. Live weather names match the forecast and pack names (English client and one other language).
8. `/xivmcp quests load ~/…/shot-quests.json` loads 16 objectives; `list`, `flag`, `next`, `done`, `undo` behave as above.
9. Unload and reload the plugin: no crash, objectives persist, nothing left on screen.

## Screenshots (TODO)

- [ ] Duty List with two quests and three custom objectives below it (one ready, one waiting, one with a note).
- [ ] Hover tooltip with steps and conditions.
- [ ] Right-click menu.
- [ ] Map opened on an objective's flag.
- [ ] Quest toast for a new objective and for a completed one.
- [ ] Unpinned movable window.
- [ ] `/xivmcp quests list` output in chat.
