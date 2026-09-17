using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using XivMcp.Core;
using CsCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using CsGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using CsTargetSystem = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem;

namespace XivMcp.Plugin.Providers.Character;

[McpProvider("character")]
public sealed class TargetProvider : IDisposable
{
    public const string ResourceUri = "ffxiv://target";

    private const long PollIntervalMs = 500;

    private readonly ITargetManager targets;
    private readonly IObjectTable objects;
    private readonly IClientState clientState;
    private readonly IDataManager data;
    private readonly IFramework framework;
    private readonly IMcpNotifier notifier;
    private readonly IPluginLog log;

    private long nextPoll;
    private ulong lastSignature;
    private bool faulted;

    public TargetProvider(
        ITargetManager targets,
        IObjectTable objects,
        IClientState clientState,
        IDataManager data,
        IFramework framework,
        IMcpNotifier notifier,
        IPluginLog log)
    {
        this.targets = targets;
        this.objects = objects;
        this.clientState = clientState;
        this.data = data;
        this.framework = framework;
        this.notifier = notifier;
        this.log = log;
        framework.Update += OnFrameworkUpdate;
    }

    public sealed record TargetsDto(
        bool HasTarget,
        ObjectDetailDto? Target,
        ObjectDetailDto? TargetOfTarget,
        ObjectDetailDto? SoftTarget,
        ObjectDetailDto? FocusTarget,
        ObjectDetailDto? MouseOver);

    [McpTool("get_target",
        Title = "Get targets",
        Description = "What the player is targeting. Returns target (hard target), targetOfTarget (whoever the target is targeting), softTarget, " +
                      "focusTarget and mouseOver (object under the cursor or its nameplate); each is omitted when empty. Every entry has kind " +
                      "(player|battleNpc|eventNpc|eventObj|treasure|aetheryte|gatheringPoint|companion|...), subKind, name, entityId, " +
                      "gameObjectId (string), baseId (BNpcBase/ENpc/EObj row id), nameId (BNpcName row id for NPCs), level, hp/mp {current,max,percent}, " +
                      "job, home/current world and FC tag for players, distance (3D), horizontalDistance, edgeDistance (horizontal minus both " +
                      "hitbox radii, roughly what action range checks use), position {x,y,z}, mapCoordinates, rotation, isTargetable, isDead, " +
                      "isHostile, inCombat, party/alliance/friend flags, whom it is targeting, cast {actionId, actionName, elapsed/total/remaining " +
                      "seconds, interruptible} and up to statusLimit statuses. Use for 'what is this', 'how far is my target', 'what is it casting'.")]
    public TargetsDto GetTarget(
        [McpParam("Maximum statuses listed per object (0-60).", Minimum = 0, Maximum = 60)] int statusLimit = 20)
    {
        return Build(Math.Clamp(statusLimit, 0, 60));
    }

    [McpResource(ResourceUri,
        Name = "Current targets",
        Description = "Same JSON as get_target (target, target of target, soft, focus, mouseover). Subscribers are notified when the hard, soft or " +
                      "focus target (or the target's target) changes, checked at most twice per second.")]
    public TargetsDto TargetResource() => Build(10);

    private TargetsDto Build(int statusLimit)
    {
        var player = objects.LocalPlayer ?? throw new McpToolException("No character is logged in.");
        var map = MapContext.Current(clientState, data);

        var target = targets.Target;
        IGameObject? targetOfTarget = null;
        if (target != null)
        {
            try
            {
                targetOfTarget = target.TargetObject;
            }
            catch (Exception)
            {
                targetOfTarget = null;
            }
        }

        var mouseOver = targets.MouseOverTarget ?? targets.MouseOverNameplateTarget;

        ObjectDetailDto? Detail(IGameObject? obj) =>
            obj != null && obj.IsValid() ? Snapshots.Detail(data, obj, player, map, statusLimit) : null;

        return new TargetsDto(
            target != null,
            Detail(target),
            Detail(targetOfTarget),
            Detail(targets.SoftTarget),
            Detail(targets.FocusTarget),
            Detail(mouseOver));
    }

    private unsafe void OnFrameworkUpdate(IFramework fw)
    {
        if (faulted)
        {
            return;
        }

        try
        {
            var now = Environment.TickCount64;
            if (now < nextPoll)
            {
                return;
            }

            nextPoll = now + PollIntervalMs;
            var system = CsTargetSystem.Instance();
            if (system == null)
            {
                return;
            }

            var hard = system->Target;
            var signature = Mix(Mix(Mix(17, ObjectKey(hard)), ObjectKey(system->SoftTarget)), ObjectKey(system->FocusTarget));
            if (hard != null && hard->IsCharacter())
            {
                signature = Mix(signature, (ulong)((CsCharacter*)hard)->TargetId);
            }

            if (signature != lastSignature)
            {
                lastSignature = signature;
                notifier.ResourceUpdated(ResourceUri);
            }
        }
        catch (Exception ex)
        {
            faulted = true;
            log.Error(ex, "xiv-mcp: target change polling failed and was disabled");
        }
    }

    private static unsafe ulong ObjectKey(CsGameObject* obj) =>
        obj == null ? 0 : ((ulong)(nint)obj * 31) ^ obj->EntityId ^ ((ulong)obj->BaseId << 32);

    private static ulong Mix(ulong hash, ulong value) => (hash ^ value) * 0x100000001B3UL;

    public void Dispose()
    {
        framework.Update -= OnFrameworkUpdate;
    }
}
