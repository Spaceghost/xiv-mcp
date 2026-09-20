using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Data;
using Lumina.Excel;
using LuminaGameData = Lumina.GameData;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary><see cref="IGameDataSource"/> over Dalamud's data manager (the game's own Lumina instance, client language).</summary>
public sealed class DalamudGameDataSource(IDataManager data) : IGameDataSource
{
    public ExcelModule Excel => data.Excel;

    public LuminaGameData GameData => data.GameData;

    public Language Language => data.Language.ToLumina();
}

internal sealed partial class GameDataIndex
{
    /// <summary>The shared index for Dalamud's data manager; the same instance <see cref="For(IGameDataSource)"/> returns for it.</summary>
    public static GameDataIndex For(IDataManager data) => For(new DalamudGameDataSource(data));
}
