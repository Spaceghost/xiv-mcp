using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;

namespace XivMcp.Plugin.Windows;

public sealed partial class MainWindow
{
    private void DrawAgentsTab()
    {
        var posts = board.Snapshot();
        if (posts.Count == 0)
        {
            ImGui.TextDisabled("No agent has posted a status yet.");
            ImGui.TextWrapped("Agents call the post_status tool to show their progress here (and in the Umbra widget).");
            return;
        }

        if (ImGui.Button("Clear all"))
            board.Clear(null);
        ImGui.SameLine();
        ImGui.TextDisabled(config.AgentBoardExpiryMinutes > 0 ? $"Entries idle for {config.AgentBoardExpiryMinutes} min are removed." : "Entries are kept until cleared.");

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY;
        if (!ImGui.BeginTable("##agents", 5, flags, new Vector2(-1, -1)))
            return;

        ImGui.TableSetupColumn("Agent", ImGuiTableColumnFlags.WidthFixed, 130);
        ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Updated", ImGuiTableColumnFlags.WidthFixed, 60);
        ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 24);
        ImGui.TableHeadersRow();

        var now = DateTimeOffset.UtcNow;
        foreach (var post in posts)
        {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(post.Agent);
            if (post.ClientName != null && ImGui.IsItemHovered())
                Tooltip($"via {post.ClientName}");

            ImGui.TableNextColumn();
            ImGui.TextColored(StateColor(post.State), post.StateName);

            ImGui.TableNextColumn();
            ImGui.TextWrapped(post.Status);
            if (post.Detail != null && ImGui.IsItemHovered())
                Tooltip(post.Detail);
            if (post.Progress is { } progress)
                ImGui.ProgressBar((float)progress, new Vector2(-1, ImGui.GetTextLineHeight()), $"{progress:P0}");
            if (post.Detail != null)
                ImGui.TextDisabled(post.Detail.Length > 120 ? post.Detail[..120] + "…" : post.Detail);

            ImGui.TableNextColumn();
            ImGui.TextDisabled(FormatAge(now - post.UpdatedAt));
            if (ImGui.IsItemHovered())
                Tooltip($"started {FormatAge(now - post.CreatedAt)} ago");

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"x##clear-{post.Agent}"))
                board.Clear(post.Agent);
        }

        ImGui.EndTable();
    }
}
