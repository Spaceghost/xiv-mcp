using XivMcp.Core;

namespace XivMcp.Plugin.Providers.Prompts;

/// <summary>
/// Planning workflows built on the static game-data tools, so most of each works on the standalone host while the game
/// is closed. Prompts only produce instructions: nothing here plays the game for the player.
/// </summary>
[McpProvider("prompts")]
public sealed class PlanningPromptsProvider
{
    private const string HostRule =
        "get_server_info tells you the host: when host is \"standalone\" or gameRunning is false, only the static game-data tools " +
        "work, so skip every step marked (live) and say in one line what you could not check. " +
        "This is advice for the player to act on: never gather, craft, buy, travel or queue on their behalf.";

    [McpPrompt("plan_daily_reset",
        Title = "Plan the daily reset",
        Description = "What resets when (daily, weekly, Grand Company, leve allowances) and what is still worth doing today: roulettes, tribal quests, GC turn-ins, custom deliveries, currencies near cap.")]
    public PromptResult PlanDailyReset(
        [McpParam("Minutes available to play, e.g. \"90\". Optional; used to cut the list down.")] string? minutes = null)
    {
        var budget = int.TryParse(minutes, out var m) && m > 0 ? $" I have about {m} minutes." : "";
        var text =
            $"Help me plan what to do around the FINAL FANTASY XIV resets.{budget}\n\n" +
            "1. (live) get_time: report how long until dailyReset (15:00 UTC: roulettes, tribal quest allowances), grandCompanyReset " +
            "(20:00 UTC: supply and provisioning missions), leveAllowances (every 12 hours) and weeklyReset (Tuesday 08:00 UTC: raid " +
            "lockouts, Wondrous Tails, custom deliveries, weekly tomestone cap). If get_time is unavailable, state those fixed UTC times " +
            "instead and convert them from the current date yourself.\n" +
            "2. list_roulettes for the roulette list with required level, item level and the daily bonus tomestone amounts.\n" +
            "3. (live) get_roulette_status: which roulettes still have their daily bonus outstanding; get_job_levels to drop the ones " +
            "no job of mine can enter and to suggest which job gains most from the experience.\n" +
            "4. (live) get_currencies: flag tomestones, scrips or seals near their cap (spend before earning more) and how far the weekly " +
            "capped tomestone is from its weekly limit.\n" +
            "5. Tribal quests, Grand Company turn-ins, leve allowances and custom deliveries: there is no tool that reads their remaining " +
            "allowances, so list them as reminders with their reset time, and say that you cannot see their state.\n" +
            "6. If a roulette I want is locked behind a duty, get_duty_unlock names the quest that opens it.\n\n" +
            "Output: a short table (activity | resets in | status | worth it because), ordered by what expires first, then a " +
            "suggested order that fits the time I have. No filler.\n\n" +
            HostRule + "\n\n" + GuidePromptsProvider.Rules("plan-daily-reset");

        return new PromptResult
        {
            Description = "Daily reset plan",
            Messages = [PromptMessage.User(text)],
        };
    }

    [McpPrompt("what_do_i_need_to_craft",
        Title = "What do I need to craft…",
        Description = "Full shopping list for crafting an item: the whole ingredient tree, what is already owned, where each missing material comes from, and optionally market prices.")]
    public PromptResult WhatDoINeedToCraft(
        [McpParam("Item to craft, by name or item id.")] string item,
        [McpParam("How many to make. Defaults to 1.")] string? quantity = null)
    {
        var count = int.TryParse(quantity, out var q) && q > 0 ? q : 1;
        var text =
            $"Work out everything I need to craft {count} × {item}.\n\n" +
            "1. search_items (isCraftable = true) to resolve the item id; if several match, prefer the exact name and mention the others.\n" +
            $"2. get_recipe_tree with that itemId and quantity {count}. Use craftOrder, rawMaterials and crystals (they pool shared " +
            "intermediates and respect recipe yields); the nested tree is only for showing structure. If depthLimited, nodeCapHit or " +
            "hasCycle is true, say so. If alternativeRecipes exist, mention the other crafters.\n" +
            "3. (live) find_owned_items for every raw material, crystal and intermediate; subtract what I have. An intermediate I " +
            "already own removes its own ingredients too: re-run get_recipe_tree for the reduced quantity rather than guessing.\n" +
            "4. (live) get_job_levels: compare crafterRequirements with my crafter levels and flag steps I cannot do yet.\n" +
            "5. For each material still missing, use the sources the tree returned (gilVendor with price and vendor location, gathering " +
            "hint, specialShop cost, gcSeals). For anything marked other, or when I need every option, call get_item_sources; for " +
            "gathered materials get_gathering_info gives node coordinates and time windows.\n" +
            "6. Optional, only if I ask about cost or the vendor route looks expensive: get_market_prices for the missing materials and " +
            "for the finished item, and compare buying against gathering or crafting. Never buy anything.\n\n" +
            "Output: (a) what is still missing as a table (material | need | have | get it from), vendor items with the gil total, " +
            "(b) crafting order bottom-up with crafter and level, (c) blockers and anything the game data could not source.\n\n" +
            HostRule + "\n\n" + GuidePromptsProvider.Rules("what-do-i-need-to-craft");

        return new PromptResult
        {
            Description = $"Shopping list for {count} × {item}",
            Messages = [PromptMessage.User(text)],
        };
    }

    [McpPrompt("where_do_i_get",
        Title = "Where do I get…",
        Description = "Every known way to obtain an item (vendors with location, gathering nodes, recipes, currency exchanges, quest and achievement rewards) and the most practical one.")]
    public PromptResult WhereDoIGet(
        [McpParam("Item to obtain, by name or item id.")] string item)
    {
        var text =
            $"Where do I get \"{item}\"?\n\n" +
            "1. search_items to resolve the item id (exact name first; list close alternatives in one line if the name is ambiguous).\n" +
            "2. get_item_sources: go through kinds in the order returned. Give vendors with NPC, zone, map coordinates and gil price; " +
            "gathering with job type, node level, zone and whether the node is timed; recipes with crafter and level; exchanges with " +
            "the currency and amount; Grand Company seals; retainer ventures; quest and achievement rewards.\n" +
            "3. Timed or hidden gathering nodes: get_gathering_info for coordinates and the next window. Crafted: mention " +
            "get_recipe_tree (or the what_do_i_need_to_craft prompt) rather than expanding the recipe here.\n" +
            "4. If kinds is [other], the game data has no source: say plainly that it is probably a drop, treasure, duty loot, venture " +
            "or event reward, and do not invent one. If the item is marketable, get_market_prices is the fallback.\n" +
            "5. (live) find_owned_items to mention if I already have some. If a vendor or node has map coordinates and the Ui tier is on, " +
            "offer set_map_flag; only call it if I agree.\n\n" +
            "Answer in a few lines: the most practical source first, then the alternatives.\n\n" +
            HostRule + "\n\n" + GuidePromptsProvider.Rules("where-do-i-get");

        return new PromptResult
        {
            Description = $"Sources of {item}",
            Messages = [PromptMessage.User(text)],
        };
    }

    [McpPrompt("weather_hunt",
        Title = "Weather hunt",
        Description = "Find the next real-time windows of a weather in a zone (optionally after another weather or during certain Eorzea hours) and pin the next one as an objective.")]
    public PromptResult WeatherHunt(
        [McpParam("Zone name or territory id, e.g. \"Eastern La Noscea\".")] string zone,
        [McpParam("Wanted weather, e.g. \"Rain\".")] string weather,
        [McpParam("Weather required in the window before, e.g. \"Clear Skies\". Optional.")] string? previousWeather = null,
        [McpParam("Eorzea hour range that counts, as \"start-end\", e.g. \"18-6\". Optional.")] string? eorzeaHours = null)
    {
        var after = string.IsNullOrWhiteSpace(previousWeather) ? "" : $" right after {previousWeather.Trim()}";
        var hours = string.IsNullOrWhiteSpace(eorzeaHours) ? "" : $" during Eorzea hours {eorzeaHours.Trim()}";
        var text =
            $"I am waiting for {weather}{after} in {zone}{hours}.\n\n" +
            "1. find_weather_windows with the zone and weather" +
            (after.Length > 0 ? ", previousWeather" : "") +
            (hours.Length > 0 ? ", and the hour range split into eorzeaHourStart and eorzeaHourEnd" : "") +
            ". If the zone name is ambiguous or unknown, use search_zones and ask me which one; if the weather never occurs there, " +
            "report the zone's weather table from the error or get_zone_info and stop.\n" +
            "2. Report the next windows in my local time with how long until each starts and how long it lasts, and chancePercent so I " +
            "know how rare it is. If fewer windows came back than asked for, say nothing more falls within the search horizon.\n" +
            "3. (live) get_location: if I am not in the zone, get_zone_info lists its aetherytes so you can name the closest one. Do " +
            "not teleport unless I ask.\n" +
            "4. (live) If the Ui tier is on, post_objective with a stable id, a title naming the weather and zone, the territoryId and " +
            "steps such as the start time in my local time and what to do when it begins, so the reminder stays on screen. Skip this " +
            "on the standalone host or when Ui is off, and give me the times to note down instead.\n\n" +
            "Keep it short: the next window first.\n\n" +
            HostRule + "\n\n" + GuidePromptsProvider.Rules("weather-hunt");

        return new PromptResult
        {
            Description = $"{weather} in {zone}",
            Messages = [PromptMessage.User(text)],
        };
    }
}
