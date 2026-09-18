# Writing a provider

A provider is a class whose methods become MCP tools, resources and prompts. The plugin discovers
providers by reflection. You do not register them anywhere.

## Skeleton

```csharp
// src/XivMcp.Plugin/Providers/<Area>/<Name>Provider.cs
using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.<Area>;

public sealed record FooDto(string Name, uint RowId, string GameObjectId);

[McpProvider("<category>")]
public sealed class FooProvider : IDisposable
{
    private readonly IClientState clientState;
    private readonly IMcpNotifier notifier;

    public FooProvider(IClientState clientState, IMcpNotifier notifier)
    {
        this.clientState = clientState;
        this.notifier = notifier;
        clientState.TerritoryChanged += OnTerritoryChanged;   // subscribe here...
    }

    [McpTool("get_foo",
        Title = "Get foo",
        Description = "Returns ... Use it when ... Fails with ... when ...")]
    public FooDto GetFoo(
        [McpParam("Name to look up, case-insensitive.")] string name,
        [McpParam("Max results (1-500).", Minimum = 1, Maximum = 500)] int limit = 25)
    {
        // Runs on the framework thread (GameThread = true is the default).
        throw new McpToolException($"No foo named '{name}'.");
    }

    private void OnTerritoryChanged(uint territoryId)
    {
        try { notifier.ResourceUpdated("ffxiv://foo"); }
        catch { /* never let an exception escape an event handler */ }
    }

    public void Dispose() => clientState.TerritoryChanged -= OnTerritoryChanged;   // ...unsubscribe here
}
```

## Rules

**Files and names**
- One public sealed provider class per file, in `src/XivMcp.Plugin/Providers/<Area>/<Name>Provider.cs`,
  namespace `XivMcp.Plugin.Providers.<Area>`, marked `[McpProvider("<category>")]`. The category is the
  player's on/off switch, so keep categories coarse (character, world, chat, gamedata, ui, actions, meta, prompts).
- Tool names are snake_case and unique across the server. Only implement names assigned to you;
  `RegisterProvider` throws on duplicates, and the whole provider then shows as failed.
- Argument names are the C# parameter names (camelCase). Parameters with defaults are optional in the
  schema. Put `ToolContext ctx` and/or `CancellationToken ct` last; with optional parameters before
  them, declare `ToolContext? ctx = null`.

**Constructor injection**
- Any Dalamud service interface (`Dalamud.Plugin.Services.I*`) and `IDalamudPluginInterface`.
- Plugin extras: `XivMcp.Core.IGameThread`, `XivMcp.Core.IMcpNotifier`, `XivMcp.Plugin.Configuration`.
  The shell also exposes `AgentBoard`, `ServerHost`, `HostState` and `ConfirmationService` (used by
  Meta providers).
- `ToolContext.ProtocolVersion` is the MCP revision the call is served under.
- The constructor runs on a thread-pool thread, **not** the framework thread. Do not read game memory
  there.
- Providers live for the plugin's lifetime. Implement `IDisposable` (or `IAsyncDisposable`) to
  unsubscribe; the shell disposes providers after the server stops.
- A constructor that throws marks only that provider as failed (shown in the Status and Tools tabs).
  Still, keep constructors trivial.

**Threads and memory**
- Anything touching game memory (`IObjectTable`, `IClientState` state, `IPartyList`, ClientStructs
  pointers, addons) must run on the framework thread. Keep the default `GameThread = true` for those
  tools.
- Pure Lumina sheet lookups may set `GameThread = false, RequiresLogin = false`.
- Async tools that mix waiting and game access set `GameThread = false` and wrap only the game-touching
  part in `await ctx.Game.InvokeAsync(() => ..., ctx.CancellationToken)`.
- Null-check every pointer. Never keep pointers, `IGameObject` or other game object references past
  the call: copy what you need into DTOs.
- Never let an exception escape an event handler (it can crash the game). Wrap handler bodies in
  try/catch.

**Output**
- Return `sealed record` DTOs. They are serialized as camelCase with nulls omitted.
- 64-bit ids (`GameObjectId`, `ContentId`) are **strings**; 32-bit `EntityId` and sheet row ids are
  numbers.
- Bound every list: take `limit` (default about 25–50, hard max 500) and `offset` where lists can be
  long, and return `truncated: true` when you cut.
- Map coordinates: derive them from `Map.SizeFactor`/`OffsetX`/`OffsetY` the same way Dalamud's
  `MapLinkPayload` does. Reuse the shared helper if one exists (`Util/GameMath`) instead of writing the
  formula again.

**Permissions** (`Permission = ToolPermission.X`)
- `Read`: observes only.
- `Ui`: local-only visible effects (echo, toast, map flag, open a window).
- `Action`: changes local client state (target, gearset, teleport, slash commands). Set
  `Destructive`/`Idempotent` honestly.
- `Chat`: produces text other players can see. Set `OpenWorld = true`.
- No combat rotation, movement or input automation. It is out of scope, whatever the tier.

**Errors**
- Throw `McpToolException` with an actionable message for expected failures: "No target selected",
  "Item 12345 not found", "Inventory not loaded yet — open it once". Do not rely on other exception types
  reaching the client as readable messages.
- `RequiresLogin = true` (default) already rejects calls at the title screen with a clear message.

**Descriptions are the product.** A model sees only the name, the description and the parameter
descriptions. Say exactly what comes back, which units and id formats are used, when to use the tool
instead of a neighbouring one, and what the common failure means.

## Resources and prompts

- `[McpResource("ffxiv://thing")]` returns an object (JSON), a string (text) or `byte[]` (blob).
  `[McpResourceTemplate("ffxiv://item/{id}")]` binds template variables to parameters by name. Call
  `IMcpNotifier.ResourceUpdated(uri)` when the content changes, from any thread.
- `[McpPrompt("name")]` takes string parameters (optional if they have a default) and returns
  `PromptResult` or a string. A prompt should orchestrate existing tools by name and tell the model to
  call `get_server_info` first and skip tiers that are off. See `Providers/Prompts/GuidePromptsProvider.cs`.

## Checking your provider

```sh
XIVMCP_ARTIFACTS=/tmp/xivmcp-art/<you> ~/.dotnet/dotnet build src/XivMcp.Plugin/XivMcp.Plugin.csproj -c Release
```

Host-side tests that need no game go in `tests/XivMcp.Plugin.Tests` (it references the plugin and resolves Dalamud's
assemblies from the dev hooks; see `ChatSendProviderTests` for validation paths exercised with interface fakes). After
adding or renaming tools, regenerate the README catalog with
`dotnet run --project tools/catalog -c Release -- readme --write README.md`, and lint the real schemas with
`dotnet run --project tools/catalog -c Release -- serve --port 41812` plus
`npx -y @modelcontextprotocol/inspector --cli http://127.0.0.1:41812/mcp --transport http --method tools/list --strict`.

Look up exact API 15 member names with `~/xiv-mcp-build/tools/decompile <Full.Type.Name> <Assembly>`
instead of guessing. In game, the **Tools** tab lists what registered, with each tool's tier and
whether it is currently available. The **Status** tab lists providers that failed to construct or
register.
