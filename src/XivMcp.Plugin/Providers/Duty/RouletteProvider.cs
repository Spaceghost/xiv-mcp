using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.GameData;
using XivMcp.Plugin.Util;
using CsPlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;
using Sheets = Lumina.Excel.Sheets;

namespace XivMcp.Plugin.Providers.Duty;

/// <summary>
/// Duty Finder roulette state: which roulettes have already paid their daily bonus this reset. Read from
/// PlayerState on the framework thread; the reset times come from the same maths as <c>get_time</c>.
/// </summary>
[McpProvider("duty")]
public sealed unsafe class RouletteProvider
{
    private readonly GameDataIndex index;

    public RouletteProvider(IDataManager data) => index = GameDataIndex.For(data);

    public sealed record RouletteDto(
        uint Id,
        string Name,
        int RequiredLevel,
        bool BonusClaimed,
        bool InDutyFinder);

    public sealed record RouletteResult(
        int Total,
        int BonusClaimed,
        int BonusAvailable,
        DateTimeOffset NextDailyResetUtc,
        long SecondsUntilReset,
        List<RouletteDto> Roulettes,
        string Note);

    [McpTool("get_roulette_status",
        Title = "Get duty roulette status",
        Description =
            "Which Duty Finder roulettes have already given their daily completion bonus this reset. Returns each roulette as " +
            "{id, name, requiredLevel, bonusClaimed, inDutyFinder}, plus counts and nextDailyResetUtc / secondsUntilReset (15:00 UTC). " +
            "bonusClaimed=false means the adventurer-in-need and daily bonus rewards are still outstanding for that roulette — it does " +
            "not check whether the character actually meets the level or unlock requirements. " +
            "Use get_duty_state for what the player is currently inside, and search_duties for the duties a roulette can pick.")]
    public RouletteResult GetRouletteStatus(
        [McpParam("Only roulettes still worth running today.")] bool unclaimedOnly = false,
        [McpParam("Include roulettes that are not offered in the Duty Finder list.")] bool includeHidden = false)
    {
        var state = CsPlayerState.Instance();
        if (state == null)
        {
            throw new McpToolException("Player state is not loaded yet; try again once the character is in the world.");
        }

        var completion = state->ContentRouletteCompletion;
        var all = new List<RouletteDto>();
        foreach (var row in index.Sheet<Sheets.ContentRoulette>())
        {
            var name = SheetJson.Text(row.Name);
            if (row.RowId == 0 || name.Length == 0)
            {
                continue;
            }

            if (!row.IsInDutyFinder && !includeHidden)
            {
                continue;
            }

            all.Add(new RouletteDto(
                row.RowId,
                name,
                row.RequiredLevel,
                IsClaimed(completion, row.CompletionArrayIndex),
                row.IsInDutyFinder));
        }

        var claimed = all.Count(r => r.BonusClaimed);
        var now = DateTimeOffset.UtcNow;
        var reset = GameMath.NextDailyUtc(now, 15);

        return new RouletteResult(
            all.Count,
            claimed,
            all.Count - claimed,
            reset,
            (long)Math.Ceiling((reset - now).TotalSeconds),
            unclaimedOnly ? all.Where(r => !r.BonusClaimed).ToList() : all,
            "bonusClaimed comes from the client's roulette completion mask and resets daily at 15:00 UTC. The mask-to-roulette mapping has not been verified in game.");
    }

    /// <summary>
    /// The sheet's CompletionArrayIndex indexes a bit in PlayerState's 10-byte roulette completion mask.
    /// Unverified in game: the mapping comes from the column name and the mask's size.
    /// </summary>
    private static bool IsClaimed(Span<byte> completion, sbyte completionArrayIndex)
    {
        if (completionArrayIndex < 0)
        {
            return false;
        }

        var byteIndex = completionArrayIndex / 8;
        return byteIndex < completion.Length && (completion[byteIndex] & (1 << (completionArrayIndex % 8))) != 0;
    }
}
