using System.Globalization;
using System.Text;
using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Prompts;

/// <summary>
/// Ready-made workflows that orchestrate the read-only tools of the other providers. Prompts only
/// produce instructions; every game interaction still goes through tools and their permission tiers.
/// </summary>
[McpProvider("prompts")]
public sealed class GuidePromptsProvider
{
    private const string Ground =
        "Ground rules: call get_server_info first if you have not yet this session, and skip any tool whose tier or " +
        "category is disabled instead of retrying it. Use only facts returned by tools; if a tool errors (for example " +
        "no character logged in, or data not loaded), say so plainly rather than guessing. Do not use Action or Chat tier " +
        "tools unless the player explicitly asks. For work that takes more than a few calls, call post_status with " +
        "agent \"{0}\" (state running, with progress), and finish with state done or failed.";

    private static readonly CompositeFormat GroundFormat = CompositeFormat.Parse(Ground);

    private static string Rules(string agent) => string.Format(CultureInfo.InvariantCulture, GroundFormat, agent);

    [McpPrompt("character_overview",
        Title = "Character overview",
        Description = "Summarize the logged-in character: jobs and levels, current gear, location, party and notable currencies.")]
    public PromptResult CharacterOverview()
    {
        var text =
            "Give me a concise overview of my FINAL FANTASY XIV character.\n\n" +
            "1. get_player for name, world, current job, level and basic stats.\n" +
            "2. get_job_levels; group jobs by role (tank, healer, melee, ranged, caster, crafter, gatherer) and point out " +
            "the highest and lowest levelled ones.\n" +
            "3. get_equipment for the current job: average item level and any empty or clearly outdated slots.\n" +
            "4. get_location and get_party for where I am and who I am with.\n" +
            "5. get_currencies: only mention currencies near their cap or that are notably high.\n\n" +
            "Format: a short headline line, then compact sections (Jobs, Gear, Where, Currencies). No filler.\n\n" +
            Rules("character-overview");

        return new PromptResult
        {
            Description = "Character overview",
            Messages = [PromptMessage.User(text)],
        };
    }

    [McpPrompt("gear_audit",
        Title = "Gear audit",
        Description = "Audit equipped gear for a job: item level outliers, missing materia or upgrades available in inventory or gearsets.")]
    public PromptResult GearAudit(
        [McpParam("Job to audit, e.g. \"WHM\" or \"Paladin\". Defaults to the current job.")] string? job = null)
    {
        var target = string.IsNullOrWhiteSpace(job) ? "my current job" : job.Trim();
        var text =
            $"Audit my gear for {target}.\n\n" +
            "1. get_player to confirm the current job and level. If the requested job is not the current one, use " +
            "list_gearsets to find a gearset for it and base the audit on that gearset's items; do NOT equip anything.\n" +
            "2. get_equipment: list each slot with item name and item level; compute the average and flag slots more than " +
            "10 item levels below it, empty slots, and missing materia where the item has melds.\n" +
            "3. For each flagged slot, find_owned_items / get_inventory to look for a better item I already own that this job " +
            "can equip (use get_item to check job and level requirements), including retainers via get_retainers if available.\n" +
            "4. Recommend concrete swaps in priority order. Only suggest acquiring new gear (search_items) when nothing owned fits, " +
            "and say where it comes from only if the tool data states it.\n\n" +
            "Output a table (slot | equipped | ilvl | issue | suggestion), then a 2-3 line summary.\n\n" +
            Rules("gear-audit");

        return new PromptResult
        {
            Description = $"Gear audit for {target}",
            Messages = [PromptMessage.User(text)],
        };
    }

    [McpPrompt("crafting_plan",
        Title = "Crafting plan",
        Description = "Plan how to craft an item: full ingredient tree, what is already owned, what to gather or buy, and crafter level checks.")]
    public PromptResult CraftingPlan(
        [McpParam("Item to craft, by name or item id.")] string item,
        [McpParam("How many to make. Defaults to 1.")] string? quantity = null)
    {
        var count = int.TryParse(quantity, out var q) && q > 0 ? q : 1;
        var text =
            $"Build a crafting plan for {count} × {item}.\n\n" +
            "1. search_recipes (or search_items then get_recipe) to find the recipe; if several match, pick the exact name " +
            "match and mention the alternatives.\n" +
            "2. Expand the full ingredient tree with get_recipe for every intermediate that is itself craftable. Multiply by " +
            "the recipe yield and the requested quantity; merge duplicate materials.\n" +
            "3. find_owned_items for every leaf material (and intermediates) to subtract what I already have in inventory, " +
            "armoury, saddlebag or retainers.\n" +
            "4. get_job_levels: for each recipe step, check the crafter job and level against my levels and flag any I cannot " +
            "craft yet.\n" +
            "5. Classify the remaining leaf materials as gatherable, crystal/shard, or purchase/other based on item data " +
            "(get_item); do not invent vendor prices or locations the tools did not return.\n\n" +
            "Output: (a) the recipe tree as an indented list with quantities, (b) a shopping/gathering list of what is still " +
            "missing, (c) crafting order bottom-up with the crafter job for each step, (d) blockers.\n\n" +
            Rules("crafting-plan");

        return new PromptResult
        {
            Description = $"Crafting plan for {count} × {item}",
            Messages = [PromptMessage.User(text)],
        };
    }

    [McpPrompt("where_is",
        Title = "Where is…",
        Description = "Locate a nearby object, NPC, player or a place and explain how to get there, optionally flagging the map.")]
    public PromptResult WhereIs(
        [McpParam("What to find: an NPC, object, player, aetheryte, zone or FATE name.")] string target)
    {
        var text =
            $"Help me find \"{target}\".\n\n" +
            "1. get_location for my current zone, map coordinates and position.\n" +
            "2. list_nearby_objects and look for a name match (case-insensitive, partial allowed). If found, report its kind, " +
            "distance and map coordinates, and the direction relative to me.\n" +
            "3. If it is not nearby: check list_fates for a FATE of that name; otherwise treat it as a place and use " +
            "search_sheet / get_sheet_row (e.g. PlaceName, TerritoryType, ENpcResident) to identify the zone, then " +
            "list_aetherytes to name the closest aetheryte I can teleport to.\n" +
            "4. If you found map coordinates and the Ui tier is enabled, offer to call set_map_flag so the player gets a map " +
            "marker; only call it if the player agrees (or if they asked to be shown). Never call teleport or set_target unless " +
            "the player explicitly asks.\n\n" +
            "Answer in 2-4 lines: where it is, how far, how to get there.\n\n" +
            Rules("where-is");

        return new PromptResult
        {
            Description = $"Where is {target}",
            Messages = [PromptMessage.User(text)],
        };
    }

    [McpPrompt("duty_prep",
        Title = "Duty preparation",
        Description = "Prepare for a duty: requirements vs. my character, party composition, gear readiness and useful reminders.")]
    public PromptResult DutyPrep(
        [McpParam("Duty name, e.g. \"The Aurum Vale\" or \"Anabaseios: The Twelfth Circle\".")] string duty)
    {
        var text =
            $"Help me prepare for the duty \"{duty}\".\n\n" +
            "1. search_duties then get_duty: content type, required level, item level sync/requirement, party size, and " +
            "unlock quest if given. If multiple duties match, list them and use the closest name match.\n" +
            "2. get_player and get_equipment: compare my current job level and average item level with the requirements.\n" +
            "3. If a quest unlocks it, get_quest_status to say whether I have unlocked it.\n" +
            "4. get_party: check the composition against the party size (tanks/healers/DPS) and note gaps.\n" +
            "5. get_duty_state to see whether I am already queued or inside a duty.\n" +
            "6. Readiness checklist: repair/gear, food and potions I own (find_owned_items for common consumables only if the " +
            "player asks), and job-specific reminders based on my current job.\n\n" +
            "Output: requirements vs. me (met / not met), party gaps, and a short checklist. Only state mechanics or strategy " +
            "you are confident about, and label them as general advice rather than tool data.\n\n" +
            Rules("duty-prep");

        return new PromptResult
        {
            Description = $"Duty prep for {duty}",
            Messages = [PromptMessage.User(text)],
        };
    }

    [McpPrompt("situation_report",
        Title = "Situation report",
        Description = "What is going on around me right now: zone, Eorzea time, weather, active FATEs, duty and party state, recent chat.")]
    public PromptResult SituationReport()
    {
        var text =
            "Give me a quick situation report of my current game session.\n\n" +
            "1. get_location and get_time (Eorzea and local time).\n" +
            "2. get_weather_forecast for this zone: current weather and the next changes.\n" +
            "3. list_fates: active FATEs sorted by distance with level, progress and time left.\n" +
            "4. get_conditions and get_duty_state: am I in combat, in a duty, mounted, crafting, bound by duty, etc.\n" +
            "5. get_party and get_target: party members' jobs and HP state, and what I have targeted.\n" +
            "6. read_chat (a small limit) for anything addressed to me: tells, party chat, or system messages about my actions. " +
            "Summarize; do not quote private messages at length.\n\n" +
            "Format as terse bullet points grouped under Where, World, Me, Party, Chat. Omit empty groups.\n\n" +
            Rules("sitrep");

        return new PromptResult
        {
            Description = "Situation report",
            Messages = [PromptMessage.User(text)],
        };
    }
}
