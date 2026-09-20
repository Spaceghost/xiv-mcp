using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Text.ReadOnly;

namespace XivMcp.Plugin.Providers.Actions;

/// <summary>Reads the rows of the on-screen party list from AgentHUD (framework thread only).</summary>
internal static unsafe class PartyHud
{
    public static List<(uint EntityId, string Name)> Rows()
    {
        var rows = new List<(uint, string)>();
        var hud = AgentHUD.Instance();
        if (hud == null)
            return rows;

        var members = hud->PartyMembers;
        var count = Math.Clamp((int)hud->PartyMemberCount, 0, Math.Min(members.Length, PartyTargeting.MaxSlots));
        for (var i = 0; i < count; i++)
        {
            ref var member = ref members[i];
            var name = "";
            if (member.Name.HasValue)
            {
                try
                {
                    name = new ReadOnlySeStringSpan(member.Name.AsSpan()).ExtractText();
                }
                catch
                {
                    name = member.Name.ToString();
                }
            }

            rows.Add((member.EntityId, name));
        }

        return rows;
    }
}
