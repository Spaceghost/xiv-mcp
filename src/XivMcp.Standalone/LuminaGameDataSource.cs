using Lumina;
using Lumina.Data;
using Lumina.Excel;
using XivMcp.Plugin.Providers.GameData;

namespace XivMcp.Standalone;

/// <summary><see cref="IGameDataSource"/> over Lumina opened directly on the installed game's sqpack directory.</summary>
internal sealed class LuminaGameDataSource : IGameDataSource, IDisposable
{
    public LuminaGameDataSource(string sqpackPath, Language language)
    {
        GameData = new GameData(sqpackPath, new LuminaOptions
        {
            DefaultExcelLanguage = language,
            PanicOnSheetChecksumMismatch = false,
            LoadMultithreaded = false,
        });
        Language = language;
    }

    public ExcelModule Excel => GameData.Excel;

    public GameData GameData { get; }

    public Language Language { get; }

    public string? GameVersion => GameData.Repositories.TryGetValue("ffxiv", out var repo) ? repo.Version : null;

    public void Dispose() => GameData.Dispose();

    public static Language ParseLanguage(string? text) => (text ?? "en").Trim().ToLowerInvariant() switch
    {
        "ja" or "jp" or "japanese" => Language.Japanese,
        "de" or "german" => Language.German,
        "fr" or "french" => Language.French,
        _ => Language.English,
    };
}
