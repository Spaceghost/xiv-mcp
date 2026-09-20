# Hard lines

> Nothing in this repository has been verified inside the running game. This page is about what the
> server is *for*, and what will not be added to it however it is asked for.

XivMcp gives an assistant **information, planning help, UI and quality-of-life assistance, and control of the
owner's own mods**. It does not play the game.

## What will never be a tool

| Not built | Why |
| --- | --- |
| Combat or rotation automation: pressing actions, choosing targets in a fight, reacting to mechanics | This is the game. A tool that does it plays for the player. |
| Gathering, crafting or fishing loops; anything that repeats a game action unattended | Same. `execute_command` also refuses the commands that start such loops in other plugins (the *Automation* command class). |
| Movement, pathing, follow or targeting bots | Same. There is no tool that moves the character, and targeting tools take one named target per approved call. |
| Market sniping, auto-buying, auto-listing, undercutting | Market tools read public price data and never touch the market board UI. |
| Packet injection, packet editing, server-protocol tampering | Every tool works through the client's own UI, commands and memory reads, the way a person at the keyboard would, or through another plugin's published IPC. |
| Reading other players' private data | Tools return what the client already shows on screen (names, jobs, party list, nearby objects). Nothing is derived, stored about other players, or sent anywhere. |
| Circumventing anti-cheat or any limit the Terms of Service enforce | Not negotiable. |
| Installing or updating plugins silently | Dalamud tools can *open* the installer on a search, or put a repository URL in front of the player. The player clicks. |

These are not settings. The approval switch below does not unlock any of them, no permission tier does, and a
request for one of them gets the `refused` error code, not a workaround.

## The approval switch

Settings has one checkbox at the top: **Ask me before anything changes** (`ConfirmActions`, default **on**).

* **On (the shipped default).** Every tool that changes something — targeting, gearsets, teleport, slash commands,
  macros, chat, enabling or disabling a plugin, opening a game or plugin window, the map flag, terminal and desktop
  panels, another mod's IPC command — waits for the player: an in-game prompt that says in one sentence what will
  happen (chat text is shown verbatim), or an approval ticket when the player is away. *Allow for 10 minutes*,
  5-minute allow sessions and the owner's auto-approve rules are shortcuts **under** this switch.
* **Off.** Those tools run at once. The owner of this mod runs it this way; everyone else gets the default.
  Every call is still written to the **action log** (Approvals tab, and `actions.log` in the plugin's config
  folder: what ran, which client and token, when, and that approval was off), and a call that sent chat or
  changed gear still shows a notification.

There is exactly one gate. `McpServer` puts every tool whose tier is Action or Chat, and every Ui tool marked
`RequiresApproval`, to `ConfirmationService.ApproveToolCallAsync`, which reads the checkbox before anything else.
`tests/XivMcp.Plugin.Tests/ToolContractTests.cs` fails the build when a tool that is not read-only is outside that
gate (the short list of Ui tools that only *display* something to the owner — toasts, the agent board, objectives —
is spelled out there), when a gated tool has no approval sentence, or when a chat tool's sentence does not show
the message verbatim.

The permission tiers (Read / Ui / Action / Chat) are a separate question — *which tools exist at all* — and stay
as they were: Action and Chat are off until the player turns them on.

## Read-only tools

Read tools never prompt. They are rate-limited per client (`RateLimitPerMinute`, default 600 a minute, burst a
quarter of that; over the limit a call fails with `rate_limited` and `retryAfterSeconds`), they declare where
their answer comes from (`dataSources` in [tools.json](tools.json): a game data sheet, client memory, a Dalamud
service, another plugin's IPC, an HTTP API, a file), and every list is paged and capped.

## MCP hints

Every tool publishes `readOnlyHint`, `destructiveHint`, `idempotentHint` and `openWorldHint`:

* `readOnlyHint` is true exactly for the Read tier.
* `destructiveHint` is true when the call overwrites or removes something that cannot be put back by calling it
  again (writing over a macro, closing a terminal, disabling a plugin).
* `idempotentHint` is false when repeating the call does more (sending chat twice, opening a second terminal).
* `openWorldHint` is true when other players or an outside server see or serve the result (chat, Universalis).
