using Dalamud.Plugin.Services;
using XivMcp.Core;
using XivMcp.Plugin.Providers.Character;
using XivMcp.Plugin.Util;
using CsFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using CsWeatherManager = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager;
using LTerritoryType = Lumina.Excel.Sheets.TerritoryType;
using LWeather = Lumina.Excel.Sheets.Weather;
using LWeatherRate = Lumina.Excel.Sheets.WeatherRate;

namespace XivMcp.Plugin.Providers.World;

[McpProvider("world")]
public sealed class TimeWeatherProvider
{
    private readonly IClientState clientState;
    private readonly IDataManager data;

    public TimeWeatherProvider(IClientState clientState, IDataManager data)
    {
        this.clientState = clientState;
        this.data = data;
    }

    public sealed record EorzeaTimeDto(
        string Time,
        int Hour,
        int Minute,
        int Bell,
        bool IsDaytime,
        int Year,
        int Month,
        string MonthName,
        int Day,
        string MoonPhase,
        bool IsOverridden,
        double SecondsUntilNextBell,
        double SecondsUntilNextWeatherChange);

    public sealed record ResetDto(string Name, string Description, DateTimeOffset NextUtc, long SecondsUntil);

    public sealed record TimeDto(
        EorzeaTimeDto Eorzea,
        DateTimeOffset ServerTimeUtc,
        bool ServerTimeFromGame,
        DateTimeOffset LocalTime,
        string LocalTimeZone,
        List<ResetDto> Resets);

    public sealed record WeatherChanceDto(uint Id, string? Name, int ChancePercent);

    public sealed record WeatherWindowDto(
        uint WeatherId,
        string? Name,
        DateTimeOffset StartUtc,
        DateTimeOffset EndUtc,
        string EorzeaStart,
        long SecondsUntilStart,
        bool IsCurrent);

    public sealed record ForecastDto(
        uint TerritoryId,
        string? TerritoryName,
        uint WeatherRateId,
        IdNameDto? CurrentWeatherInGame,
        List<WeatherChanceDto> PossibleWeather,
        List<WeatherWindowDto> Forecast,
        string Note);

    [McpTool("get_time",
        Title = "Get Eorzea and real time",
        RequiresLogin = false,
        Description = "Current Eorzea time and the real-world reset schedule. Returns eorzea {time \"HH:MM\", hour, minute, bell, isDaytime " +
                      "(06:00-18:00), year, month, monthName (e.g. \"3rd Astral Moon\"), day (sun 1-32), moonPhase, isOverridden (the client " +
                      "clock is frozen/overridden, e.g. in some cutscenes), secondsUntilNextBell, secondsUntilNextWeatherChange}, serverTimeUtc " +
                      "(from the game server clock when available), localTime with timezone, and resets: dailyReset (15:00 UTC: roulettes, " +
                      "tribal quests), weeklyReset (Tuesday 08:00 UTC: raids lockouts, Wondrous Tails, custom deliveries), grandCompanyReset " +
                      "(20:00 UTC: GC supply/provisioning missions), leveAllowances (every 12h at 00:00/12:00 UTC), each with nextUtc and " +
                      "secondsUntil. Use for timed nodes, weather windows and 'when does X reset'.")]
    public unsafe TimeDto GetTime()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var serverTime = utcNow;
        var fromGame = false;
        long eorzeaSeconds;
        var overridden = false;

        var framework = CsFramework.Instance();
        if (framework != null)
        {
            var server = CsFramework.GetServerTime();
            if (server > 1_500_000_000)
            {
                serverTime = DateTimeOffset.FromUnixTimeSeconds(server);
                fromGame = true;
            }
        }

        eorzeaSeconds = GameMath.ToEorzeaSeconds(serverTime);
        if (framework != null)
        {
            var clientTime = framework->ClientTime;
            if (clientTime.IsEorzeaTimeOverridden && clientTime.EorzeaTimeOverride > 0)
            {
                eorzeaSeconds = clientTime.EorzeaTimeOverride;
                overridden = true;
            }
            else if (clientTime.EorzeaTime > 0)
            {
                eorzeaSeconds = clientTime.EorzeaTime;
            }
        }

        var date = GameMath.ToEorzeaDate(eorzeaSeconds);
        var realUnix = serverTime.ToUnixTimeMilliseconds() / 1000.0;
        var secondsIntoBell = realUnix % GameMath.SecondsPerBell;
        var secondsIntoWindow = realUnix % GameMath.SecondsPerWeatherWindow;
        var eorzea = new EorzeaTimeDto(
            $"{date.Hour:00}:{date.Minute:00}",
            date.Hour,
            date.Minute,
            date.Hour,
            date.Hour is >= 6 and < 18,
            date.Year,
            date.Month,
            GameMath.EorzeaMonthName(date.Month),
            date.Day,
            GameMath.MoonPhaseName(date.Day),
            overridden,
            GameMath.Round(GameMath.SecondsPerBell - secondsIntoBell, 1),
            GameMath.Round(GameMath.SecondsPerWeatherWindow - secondsIntoWindow, 1));

        ResetDto Reset(string name, string description, DateTimeOffset next) =>
            new(name, description, next, (long)Math.Ceiling((next - serverTime).TotalSeconds));

        var leveNext = GameMath.NextDailyUtc(serverTime, 0);
        var leveNoon = GameMath.NextDailyUtc(serverTime, 12);
        var resets = new List<ResetDto>
        {
            Reset("dailyReset", "Daily duty roulettes, tribal/allied society quests, daily hunts, Mini Cactpot", GameMath.NextDailyUtc(serverTime, 15)),
            Reset("weeklyReset", "Weekly raid loot lockouts, tomestone cap, Wondrous Tails, custom deliveries, Doman enclave, weekly hunts", GameMath.NextWeeklyUtc(serverTime, DayOfWeek.Tuesday, 8)),
            Reset("grandCompanyReset", "Grand Company supply and provisioning missions", GameMath.NextDailyUtc(serverTime, 20)),
            Reset("leveAllowances", "Levequest allowances (+3 every 12 hours)", leveNext < leveNoon ? leveNext : leveNoon),
        };

        var local = DateTimeOffset.Now;
        return new TimeDto(eorzea, serverTime, fromGame, local, TimeZoneInfo.Local.Id, resets);
    }

    [McpTool("get_weather_forecast",
        Title = "Get weather forecast",
        GameThread = false,
        RequiresLogin = false,
        Description = "Weather forecast for a zone computed with the game's own deterministic weather algorithm (weather changes every 8 Eorzea " +
                      "hours = 23m20s real time, at ET 00:00, 08:00 and 16:00). Returns territory, possibleWeather with chancePercent from the " +
                      "zone's WeatherRate, and forecast: count consecutive windows starting with the current one, each {weatherId, name, startUtc, " +
                      "endUtc, eorzeaStart, secondsUntilStart (negative for the current window), isCurrent}. territoryId defaults to the current " +
                      "zone (requires login); pass a TerritoryType id (see game data tools) to forecast any zone without being logged in. Instanced " +
                      "duties usually have fixed/scripted weather and are rejected. currentWeatherInGame is included when forecasting the zone " +
                      "the player is in.")]
    public async Task<ForecastDto> GetWeatherForecast(
        [McpParam("TerritoryType row id. Omit for the player's current zone.", Minimum = 1)] uint? territoryId = null,
        [McpParam("Number of 8-bell weather windows to return, starting with the current one (1-24). Default 6.", Minimum = 1, Maximum = 24)] int count = 6,
        ToolContext? ctx = null)
    {
        count = Math.Clamp(count, 1, 24);
        if (ctx == null)
        {
            throw new McpToolException("Tool context unavailable.");
        }

        var (currentTerritory, currentWeather, loggedIn) =
            await ctx.Game.InvokeAsync(ReadCurrent, ctx.CancellationToken).ConfigureAwait(false);

        var id = territoryId ?? 0;
        if (id == 0)
        {
            if (!loggedIn || currentTerritory == 0)
            {
                throw new McpToolException("Not logged in: pass territoryId to forecast a specific zone.");
            }

            id = currentTerritory;
        }

        if (!data.GetExcelSheet<LTerritoryType>().TryGetRow(id, out var territory))
        {
            throw new McpToolException($"Territory {id} not found.");
        }

        var rateId = territory.WeatherRate.RowId;
        if (rateId == 0 || !data.GetExcelSheet<LWeatherRate>().TryGetRow(rateId, out var rate))
        {
            throw new McpToolException($"Territory {id} has no weather table (weather there is fixed or scripted).");
        }

        var rates = new byte[8];
        var weatherIds = new uint[8];
        var total = 0;
        for (var i = 0; i < 8; i++)
        {
            rates[i] = rate.Rate[i];
            weatherIds[i] = rate.Weather[i].RowId;
            total += rates[i];
        }

        if (total == 0)
        {
            throw new McpToolException($"Territory {id} has no weather table (weather there is fixed or scripted).");
        }

        var weatherSheet = data.GetExcelSheet<LWeather>();
        string? WeatherName(uint weatherId)
        {
            var name = weatherSheet.GetRowOrDefault(weatherId)?.Name.ExtractText();
            return string.IsNullOrEmpty(name) ? null : name;
        }

        var possible = new List<WeatherChanceDto>();
        for (var i = 0; i < 8; i++)
        {
            if (rates[i] == 0 || weatherIds[i] == 0)
            {
                continue;
            }

            var existing = possible.FindIndex(p => p.Id == weatherIds[i]);
            if (existing >= 0)
            {
                possible[existing] = possible[existing] with { ChancePercent = possible[existing].ChancePercent + rates[i] };
            }
            else
            {
                possible.Add(new WeatherChanceDto(weatherIds[i], WeatherName(weatherIds[i]), rates[i]));
            }
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var start = GameMath.WeatherWindowStart(now);
        var forecast = new List<WeatherWindowDto>(count);
        for (var i = 0; i < count; i++)
        {
            var windowStart = start + ((long)i * GameMath.SecondsPerWeatherWindow);
            var index = GameMath.PickWeatherIndex(rates, GameMath.WeatherTarget(windowStart));
            var weatherId = index >= 0 ? weatherIds[index] : 0;
            var eorzeaHour = GameMath.ToEorzeaDate(GameMath.ToEorzeaSeconds(windowStart)).Hour;
            forecast.Add(new WeatherWindowDto(
                weatherId,
                WeatherName(weatherId),
                DateTimeOffset.FromUnixTimeSeconds(windowStart),
                DateTimeOffset.FromUnixTimeSeconds(windowStart + GameMath.SecondsPerWeatherWindow),
                $"{eorzeaHour / 8 * 8:00}:00",
                windowStart - now,
                i == 0));
        }

        IdNameDto? inGame = loggedIn && id == currentTerritory && currentWeather != 0
            ? new IdNameDto(currentWeather, WeatherName(currentWeather))
            : null;

        return new ForecastDto(
            id,
            territory.PlaceName.ValueNullable?.Name.ExtractText(),
            rateId,
            inGame,
            possible,
            forecast,
            "Computed from the host clock. Weather can differ from the forecast during scripted events, quests or in zones with individual weather.");
    }

    private unsafe (uint Territory, byte Weather, bool LoggedIn) ReadCurrent()
    {
        var weatherManager = CsWeatherManager.Instance();
        var weather = weatherManager != null ? weatherManager->GetCurrentWeather() : (byte)0;
        return (clientState.TerritoryType, weather, clientState.IsLoggedIn);
    }
}
