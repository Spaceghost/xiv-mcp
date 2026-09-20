using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Party;

/// <summary>Names of the character's own social groups. Reads names only; member lists are never touched.</summary>
[McpProvider("party")]
public sealed unsafe class SocialGroupsProvider
{
    private readonly GameDataIndex index;
    private readonly IObjectTable objects;

    public SocialGroupsProvider(IDataManager data, IObjectTable objects)
    {
        index = GameDataIndex.For(data);
        this.objects = objects;
    }

    [McpTool("list_social_groups",
        Sources = ["client:InfoProxyFreeCompany", "client:InfoProxyLinkshell", "client:InfoProxyCrossWorldLinkshell", "dalamud:IObjectTable", "lumina:GrandCompany"],
        Title = "List the character's social groups",
        Description =
            "Names of the groups the logged-in character belongs to, and nothing else: freeCompany {loaded, member, name, tag, rank (the " +
            "company's rank 1-30), grandCompany}, linkshells and crossWorldLinkshells {loaded, count, groups [{slot 1-8, name}]}. No member " +
            "lists and no data about other players. loaded=false means the client has not loaded that information yet (free company details " +
            "load when the Free Company window is opened; the tag is still reported from the nameplate); it is not an error. notExposed " +
            "lists what cannot be read (PvP team, fellowships, the character's own rank title). Use for 'which linkshell number is X' before " +
            "suggesting a /linkshellN message, or 'what FC am I in'. Use get_party for the current party.",
        RequiresLogin = true)]
    public SocialGroupsResult ListSocialGroups()
    {
        var player = objects.LocalPlayer ?? throw new McpToolException("No character is logged in.");
        return SocialGroupsShaper.Build(Read(player.CompanyTag.TextValue));
    }

    private SocialGroupsRecord Read(string? companyTag)
    {
        var fc = InfoProxyFreeCompany.Instance();
        ulong fcId = 0;
        string? fcName = null;
        string? grandCompany = null;
        var fcRank = 0;
        if (fc != null)
        {
            fcId = fc->Id;
            fcName = fc->NameString;
            fcRank = fc->Rank;
            var gc = (uint)fc->GrandCompany;
            if (gc != 0 && index.Row<Sheets.GrandCompany>(gc) is { } gcRow) grandCompany = SheetJson.Text(gcRow.Name);
        }

        List<string?>? linkshells = null;
        var ls = InfoProxyLinkshell.Instance();
        if (ls != null)
        {
            linkshells = [];
            foreach (ref var entry in ls->LinkShells)
            {
                string? name = null;
                if (entry.Id != 0)
                {
                    var pointer = ls->GetLinkshellName(entry.Id);
                    if (pointer.HasValue) name = pointer.ToString();
                }

                linkshells.Add(name);
            }
        }

        List<string?>? crossWorld = null;
        var cwls = InfoProxyCrossWorldLinkshell.Instance();
        if (cwls != null)
        {
            crossWorld = [];
            foreach (ref var entry in cwls->CrossWorldLinkshells)
            {
                crossWorld.Add(entry.Name.StringPtr.Value == null || entry.Name.BufUsed <= 1 ? null : entry.Name.ToString());
            }
        }

        return new SocialGroupsRecord(fc != null, fcId, fcName, companyTag, fcRank, grandCompany, linkshells, crossWorld);
    }
}
