using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using XivMcp.Core;

namespace XivMcp.Plugin.Windows;

public sealed partial class MainWindow
{
    private IReadOnlyList<(string Name, string? Title, string Description, ToolPermission Permission, string Category)> toolsCache = [];
    private DateTime toolsCacheAt;

    private void DrawToolsTab()
    {
        var now = DateTime.UtcNow;
        if (now - toolsCacheAt > TimeSpan.FromSeconds(2))
        {
            toolsCache = host.ListTools();
            toolsCacheAt = now;
        }

        var hostState = host.HostState;
        ImGui.TextUnformatted("Tiers:");
        foreach (var tier in Enum.GetValues<ToolPermission>())
        {
            ImGui.SameLine();
            var on = hostState.IsPermitted(tier);
            ImGui.TextColored(on ? TierColor(tier) : ImGuiColors.DalamudGrey3, $"{tier} {(on ? "on" : "off")}");
        }

        if (config.ConfirmActions && (config.AllowAction || config.AllowChat))
            ImGui.TextDisabled("Action/Chat calls ask for your approval first (Settings → Permissions).");

        ImGui.TextDisabled($"{toolsCache.Count} tools registered. Toggle tiers and categories in Settings.");
        ImGui.Separator();

        var categories = host.Categories
            .Union(toolsCache.Select(t => t.Category), StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (var category in categories)
        {
            var tools = toolsCache
                .Where(t => string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Permission)
                .ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToArray();
            var categoryOn = config.IsCategoryEnabled(category);
            var failures = host.Providers.Where(p => !p.Loaded && string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase)).ToArray();

            var header = $"{category}  ({tools.Length}){(categoryOn ? "" : "  [disabled]")}{(failures.Length > 0 ? "  [provider error]" : "")}###cat-{category}";
            if (!ImGui.CollapsingHeader(header))
                continue;

            ImGui.PushID(category);
            if (ImGui.Checkbox("Category enabled", ref categoryOn))
            {
                config.SetCategoryEnabled(category, categoryOn);
                SaveConfig();
            }

            foreach (var failure in failures)
                ImGui.TextColored(ImGuiColors.DalamudRed, $"{failure.Type.Name}: {failure.Error}");

            if (tools.Length > 0 && ImGui.BeginTable("##tools", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Tool", ImGuiTableColumnFlags.WidthFixed, 190);
                ImGui.TableSetupColumn("Tier", ImGuiTableColumnFlags.WidthFixed, 56);
                ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);

                foreach (var tool in tools)
                {
                    var available = categoryOn && hostState.IsPermitted(tool.Permission);
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    if (available)
                        ImGui.TextUnformatted(tool.Name);
                    else
                        ImGui.TextDisabled(tool.Name);

                    ImGui.TableNextColumn();
                    ImGui.TextColored(TierColor(tool.Permission), tool.Permission.ToString());

                    ImGui.TableNextColumn();
                    var summary = tool.Title ?? FirstSentence(tool.Description);
                    ImGui.TextDisabled(summary);
                    if (ImGui.IsItemHovered() && tool.Description.Length > 0)
                        Tooltip(tool.Description);
                }

                ImGui.EndTable();
            }

            ImGui.PopID();
        }
    }

    private static string FirstSentence(string text)
    {
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        var sentence = end > 0 ? text[..(end + 1)] : text;
        return sentence.Length > 110 ? sentence[..110] + "…" : sentence;
    }
}
