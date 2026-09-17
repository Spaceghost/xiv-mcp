using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Inventory;

/// <summary>Retainer roster from RetainerManager (framework thread).</summary>
[McpProvider("inventory")]
public sealed unsafe class RetainerProvider
{
    private readonly GameDataIndex index;

    public RetainerProvider(IDataManager data) => index = GameDataIndex.For(data);

    [McpTool("get_retainers",
        Title = "Get retainers",
        Description =
            "The character's retainers in display order: id, name, whether the slot is available (subscription), class/job and level, gil held, " +
            "number of items in its inventory and on the market board, market listing expiry, market town, and the current venture (id, name, " +
            "completion time, whether complete, minutes remaining). Data comes from the client's retainer list, which is only populated after " +
            "a summoning bell has been used this session; loaded=false with a note means that has not happened yet. Also names the retainer " +
            "whose inventory is currently in memory (activeRetainer), which get_inventory/find_owned_items can read.",
        RequiresLogin = true)]
    public RetainersResult GetRetainers()
    {
        var manager = RetainerManager.Instance();
        if (manager == null)
            return new RetainersResult(false, "Retainer manager is not available.", 0, null, []);
        if (!manager->IsReady)
            return new RetainersResult(false, "Retainer data is not loaded: use a summoning bell once this session.", manager->MaxRetainerEntitlement, null, []);

        var now = DateTimeOffset.UtcNow;
        var list = new List<RetainerInfo>();
        var count = manager->GetRetainerCount();
        for (uint i = 0; i < count && i < 10; i++)
        {
            var retainer = manager->GetRetainerBySortedIndex(i);
            if (retainer == null || retainer->RetainerId == 0) continue;

            VentureInfo? venture = null;
            if (retainer->VentureId != 0)
            {
                DateTimeOffset? completes = retainer->VentureComplete > 0 ? DateTimeOffset.FromUnixTimeSeconds(retainer->VentureComplete) : null;
                var complete = completes is { } c && c <= now;
                venture = new VentureInfo(
                    retainer->VentureId,
                    VentureName(retainer->VentureId),
                    completes,
                    complete,
                    completes is { } c2 && !complete ? (int)Math.Ceiling((c2 - now).TotalMinutes) : null);
            }

            var job = retainer->ClassJob != 0 ? index.ClassJob(retainer->ClassJob) : null;
            list.Add(new RetainerInfo(
                retainer->RetainerId.ToString(),
                retainer->NameString,
                retainer->Available,
                job != null ? $"{job.Name} ({job.Abbreviation})" : null,
                retainer->Level,
                retainer->Gil,
                retainer->ItemCount,
                retainer->MarketItemCount,
                retainer->MarketExpire > 0 ? DateTimeOffset.FromUnixTimeSeconds(retainer->MarketExpire) : null,
                Enum.IsDefined(retainer->Town) ? retainer->Town.ToString() : null,
                venture));
        }

        return new RetainersResult(true, null, manager->MaxRetainerEntitlement, InventoryReader.ActiveRetainerName(), list);
    }

    private string? VentureName(uint ventureId)
    {
        try
        {
            if (index.Row<Sheets.RetainerTask>(ventureId) is not { } task) return null;
            var reference = task.Task;
            if (reference.GetValueOrDefault<Sheets.RetainerTaskRandom>() is { } random)
                return GameDataIndex.NullIfEmpty(SheetJson.Text(random.Name));
            if (reference.GetValueOrDefault<Sheets.RetainerTaskNormal>() is { } normal && normal.Item.RowId != 0)
                return index.ItemName(normal.Item.RowId);
            return null;
        }
        catch
        {
            return null;
        }
    }
}
