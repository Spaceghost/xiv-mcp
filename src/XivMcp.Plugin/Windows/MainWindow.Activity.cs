using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using XivMcp.Core;

namespace XivMcp.Plugin.Windows;

public sealed partial class MainWindow
{
    private const int ActivityMax = 300;

    private IReadOnlyList<ActivityEntry> activityCache = [];
    private long activityCacheVersion = -1;
    private DateTime activityCacheAt;
    private string activityFilter = "";
    private bool activityFailuresOnly;
    private bool activityHideNoise = true;

    private static readonly HashSet<string> NoiseMethods = new(StringComparer.Ordinal)
    {
        "ping", "notifications/initialized", "tools/list", "resources/list", "resources/templates/list", "prompts/list", "logging/setLevel",
    };

    private void DrawActivityTab()
    {
        var version = host.ActivityVersion;
        var now = DateTime.UtcNow;
        if (version != activityCacheVersion && now - activityCacheAt > TimeSpan.FromMilliseconds(250))
        {
            activityCache = host.GetActivity(ActivityMax);
            activityCacheVersion = version;
            activityCacheAt = now;
        }

        ImGui.SetNextItemWidth(220);
        ImGui.InputText("Filter##activity", ref activityFilter, 128);
        ImGui.SameLine();
        ImGui.Checkbox("Failures only", ref activityFailuresOnly);
        ImGui.SameLine();
        ImGui.Checkbox("Hide list/ping", ref activityHideNoise);

        IEnumerable<ActivityEntry> rows = activityCache;
        if (activityFailuresOnly)
            rows = rows.Where(e => !e.Success);
        if (activityHideNoise)
            rows = rows.Where(e => !e.Success || !NoiseMethods.Contains(e.Method));
        if (!string.IsNullOrWhiteSpace(activityFilter))
        {
            var f = activityFilter.Trim();
            rows = rows.Where(e =>
                e.Method.Contains(f, StringComparison.OrdinalIgnoreCase)
                || (e.Target?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || (e.ClientName?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false)
                || (e.Error?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = rows.ToList();
        if (activityCache.Count == 0)
        {
            ImGui.TextDisabled("No requests handled yet.");
            return;
        }

        const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
        if (!ImGui.BeginTable("##activity", 5, flags, new Vector2(-1, -1)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, 64);
        ImGui.TableSetupColumn("Client", ImGuiTableColumnFlags.WidthFixed, 100);
        ImGui.TableSetupColumn("Request", ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn("ms", ImGuiTableColumnFlags.WidthFixed, 48);
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableHeadersRow();

        foreach (var e in list)
        {
            ImGui.TableNextRow();
            if (!e.Success)
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(ImGuiColors.ErrorBackground));

            ImGui.TableNextColumn();
            ImGui.TextDisabled(e.Timestamp.ToLocalTime().ToString("HH:mm:ss"));

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(e.ClientName ?? "—");
            if (e.SessionId != null && ImGui.IsItemHovered())
                Tooltip($"session {e.SessionId}");

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(e.Target is { Length: > 0 } target ? $"{e.Method}  {target}" : e.Method);

            ImGui.TableNextColumn();
            ImGui.TextDisabled($"{e.DurationMs:0}");

            ImGui.TableNextColumn();
            if (e.Success)
                ImGui.TextColored(ImGuiColors.HealerGreen, "ok");
            else
                ImGui.TextColored(ImGuiColors.DalamudRed, e.Error ?? "failed");
        }

        ImGui.EndTable();
    }
}
