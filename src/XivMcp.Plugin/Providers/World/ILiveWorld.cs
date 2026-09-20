namespace XivMcp.Plugin.Providers.World;

/// <summary>The game's clock as far as it can be read. Without the game everything comes from the host clock.</summary>
/// <param name="ServerTime">The game server's time when the client knows it, else the host's UTC clock.</param>
/// <param name="FromGame"><paramref name="ServerTime"/> came from the game.</param>
/// <param name="EorzeaSeconds">The client's Eorzea clock, or null to derive it from <paramref name="ServerTime"/>.</param>
/// <param name="Overridden">The client's Eorzea clock is frozen or overridden (some cutscenes).</param>
public readonly record struct LiveClock(DateTimeOffset ServerTime, bool FromGame, long? EorzeaSeconds, bool Overridden);

/// <summary>Where the player is, when there is a player.</summary>
public readonly record struct LiveZone(uint Territory, byte Weather, bool LoggedIn);

/// <summary>
/// The little bit of live state the otherwise static world tools (time, weather forecast) use. The plugin reads it from
/// the client; the standalone host answers "no game" so those tools still work for an explicitly named zone.
/// No Dalamud types: this file compiles into both hosts.
/// </summary>
public interface ILiveWorld
{
    /// <summary>False in the standalone host.</summary>
    bool GameRunning { get; }

    /// <summary>Call on the game thread.</summary>
    LiveClock ReadClock();

    /// <summary>Call on the game thread.</summary>
    LiveZone ReadZone();
}

/// <summary><see cref="ILiveWorld"/> when no game is attached: host clock, no zone.</summary>
public sealed class OfflineWorld : ILiveWorld
{
    public bool GameRunning => false;

    public LiveClock ReadClock() => new(DateTimeOffset.UtcNow, false, null, false);

    public LiveZone ReadZone() => default;
}
