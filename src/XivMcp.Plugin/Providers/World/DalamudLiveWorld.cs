using Dalamud.Plugin.Services;
using CsFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using CsWeatherManager = FFXIVClientStructs.FFXIV.Client.Game.WeatherManager;

namespace XivMcp.Plugin.Providers.World;

/// <summary><see cref="ILiveWorld"/> read from the running client. Every member must be called on the framework thread.</summary>
public sealed class DalamudLiveWorld(IClientState clientState) : ILiveWorld
{
    public bool GameRunning => true;

    public unsafe LiveClock ReadClock()
    {
        var serverTime = DateTimeOffset.UtcNow;
        var fromGame = false;
        long? eorzeaSeconds = null;
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

        return new LiveClock(serverTime, fromGame, eorzeaSeconds, overridden);
    }

    public unsafe LiveZone ReadZone()
    {
        var weatherManager = CsWeatherManager.Instance();
        var weather = weatherManager != null ? weatherManager->GetCurrentWeather() : (byte)0;
        return new LiveZone(clientState.TerritoryType, weather, clientState.IsLoggedIn);
    }
}
