using Lumina.Data;
using Lumina.Excel;
using LuminaGameData = Lumina.GameData;

namespace XivMcp.Plugin.Providers.GameData;

/// <summary>
/// Where the static game data comes from: Dalamud's <c>IDataManager</c> inside the game, or Lumina opened directly
/// over the installed sqpack in the standalone host. Lumina types only, so every provider written against it
/// compiles into both hosts (the standalone project links these source files; see docs/STANDALONE.md).
/// </summary>
public interface IGameDataSource
{
    ExcelModule Excel { get; }

    LuminaGameData GameData { get; }

    /// <summary>Language the text columns are read in.</summary>
    Language Language { get; }

    /// <summary>Shorthand for <c>Excel.GetSheet&lt;T&gt;(Language)</c>.</summary>
    ExcelSheet<T> GetExcelSheet<T>()
        where T : struct, IExcelRow<T> => Excel.GetSheet<T>(Language);
}
