using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using XivMcp.Core;

namespace XivMcp.Plugin.Windows;

/// <summary>
/// The What's new tab: the changelog, straight from changelog.json (embedded in XivMcp.Core, and the
/// same file CHANGELOG.md is rendered from), so a player sees what changed without leaving the game.
/// </summary>
public sealed partial class MainWindow
{
    public const string WhatsNewTab = "What's new";

    private static Changelog? changelog;
    private static string? changelogError;

    /// <summary>NEW and FIX are in a release; BETA is merged but not yet verified in game; SOON is still being built.</summary>
    private static Vector4 StatusColour(string status) => status switch
    {
        "new" => ImGuiColors.HealerGreen,
        "fix" => ImGuiColors.DalamudOrange,
        "beta" => ImGuiColors.TankBlue,
        _ => ImGuiColors.DalamudGrey,
    };

    private void DrawWhatsNewTab()
    {
        if (changelog is null && changelogError is null)
        {
            try
            {
                changelog = Changelog.Bundled();
            }
            catch (Exception ex)
            {
                changelogError = ex.Message;
            }
        }

        if (changelog is null)
        {
            ImGui.TextDisabled($"The changelog could not be read: {changelogError}");
            return;
        }

        if (changelog.Intro.Length > 0)
        {
            Wrapped(changelog.Intro, ImGuiColors.DalamudGrey);
            ImGui.Separator();
        }

        for (var i = 0; i < changelog.Releases.Count; i++)
        {
            var release = changelog.Releases[i];
            var flags = i == 0 ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
            if (!ImGui.CollapsingHeader($"{release.Heading}###changelog{i}", flags))
                continue;

            Wrapped(release.Blurb, ImGuiColors.DalamudGrey);
            foreach (var item in release.Items)
                Wrapped($"{Changelog.Label(item.Status)}   {item.Text}", StatusColour(item.Status));

            ImGui.Spacing();
        }
    }

    private static void Wrapped(string text, Vector4 colour)
    {
        if (text.Length == 0)
            return;
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }
}
