using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Plugin.Objectives;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Windows;

/// <summary>
/// Custom objectives drawn like Duty List entries, pinned just below the game's Duty List (see docs/OBJECTIVES.md for
/// why this is an overlay rather than nodes injected into _ToDoList). Click an entry to place the map flag and open the
/// map; right-click for progress actions. All state and rules live in <see cref="ObjectiveTracker"/>.
/// </summary>
public sealed class ObjectiveOverlay : Window, IDisposable
{
    /// <summary>Side-quest marker from ui/icon/071000 (verify in game; a missing icon draws nothing).</summary>
    public const uint QuestIconId = 71021;

    private static readonly Vector4 DefaultTitle = new(0.93f, 0.88f, 0.77f, 1f);
    private static readonly Vector4 DefaultText = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 DefaultEdge = new(0.16f, 0.11f, 0.05f, 1f);
    private static readonly Vector4 ReadyColor = new(0.55f, 0.92f, 0.45f, 1f);
    private static readonly Vector4 WaitColor = new(0.78f, 0.74f, 0.66f, 1f);
    private static readonly Vector4 ProblemColor = new(1f, 0.45f, 0.4f, 1f);
    private static readonly Vector4 DoneColor = new(0.6f, 0.6f, 0.6f, 1f);

    private readonly ObjectiveTracker tracker;
    private readonly Configuration config;
    private readonly IGameGui gameGui;
    private readonly ITextureProvider textures;
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Action<string> print;
    private readonly IFontHandle font;
    private DutyListLayout layout;

    public ObjectiveOverlay(
        ObjectiveTracker tracker, Configuration config, IGameGui gameGui, ITextureProvider textures, IDalamudPluginInterface pluginInterface,
        Action<string> print)
        : base("Custom objectives##XivMcpObjectives")
    {
        this.tracker = tracker;
        this.config = config;
        this.gameGui = gameGui;
        this.textures = textures;
        this.pluginInterface = pluginInterface;
        this.print = print;
        font = pluginInterface.UiBuilder.FontAtlas.NewGameFontHandle(new GameFontStyle(GameFontFamilyAndSize.Axis14));
        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;
    }

    private IEnumerable<ObjectiveView> Visible =>
        tracker.Latest.Where(v => !v.Objective.Completed || config.ShowCompletedObjectives);

    public override bool DrawConditions()
    {
        if (!config.ShowObjectives || gameGui.GameUiHidden || !Visible.Any())
            return false;
        layout = DutyListAnchor.Read(gameGui);
        return !config.ObjectivesFollowDutyList || layout.Visible;
    }

    public override void PreDraw()
    {
        var baseFlags = ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing
                        | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoCollapse;
        if (config.ObjectivesFollowDutyList && layout.Visible)
        {
            Flags = baseFlags | ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoMove
                    | ImGuiWindowFlags.NoSavedSettings;
            Position = ImGuiHelpers.MainViewport.Pos + new Vector2(layout.TopLeft.X, layout.ContentBottom + (6 * layout.Scale));
            PositionCondition = ImGuiCond.Always;
            var width = Math.Max(160f, layout.Width);
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(width, 0), MaximumSize = new Vector2(width, 4000) };
            BgAlpha = 0;
        }
        else
        {
            Flags = baseFlags | ImGuiWindowFlags.NoTitleBar;
            PositionCondition = ImGuiCond.FirstUseEver;
            Position = new Vector2(300, 300);
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(220, 0), MaximumSize = new Vector2(600, 4000) };
            BgAlpha = 0.35f;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(4, 2));
    }

    public override void PostDraw() => ImGui.PopStyleVar();

    public override void Draw()
    {
        var scale = config.ObjectivesFollowDutyList && layout.Visible ? layout.Scale : 1f;
        using (font.Push())
        {
            ImGui.SetWindowFontScale(scale);
            ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX());
            foreach (var view in Visible.ToArray())
                DrawEntry(view, scale);
            ImGui.PopTextWrapPos();
            ImGui.SetWindowFontScale(1f);
        }
    }

    private void DrawEntry(ObjectiveView view, float scale)
    {
        var o = view.Objective;
        var s = view.Status;
        ImGui.PushID(o.Id);
        var start = ImGui.GetCursorScreenPos();
        var iconSize = new Vector2(ImGui.GetTextLineHeight());

        var icon = textures.GetFromGameIcon(new GameIconLookup(QuestIconId)).GetWrapOrEmpty();
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.4f, o.Completed))
            ImGui.Image(icon.Handle, iconSize);
        ImGui.SameLine(0, 4 * scale);
        var indent = ImGui.GetCursorPosX();

        var titleColor = o.Completed ? DoneColor : layout.TitleColor ?? DefaultTitle;
        var textColor = o.Completed ? DoneColor : layout.TextColor ?? DefaultText;
        EdgeText(o.Title, titleColor, layout.TitleEdge ?? DefaultEdge);

        ImGui.SetCursorPosX(indent);
        var step = o.Completed ? "Complete" : o.CurrentStepText ?? o.Spot ?? "";
        if (step.Length > 0)
        {
            var prefix = o.Steps.Count > 1 && !o.Completed ? $"{o.CurrentStepIndex + 1}/{o.Steps.Count}  " : "";
            EdgeText(prefix + step, textColor, layout.TextEdge ?? DefaultEdge);
        }

        if (o.Note is { } note && !o.Completed)
        {
            ImGui.SetCursorPosX(indent);
            EdgeText(note, WaitColor, layout.TextEdge ?? DefaultEdge);
        }

        if (!o.Completed)
        {
            ImGui.SetCursorPosX(indent);
            var color = s.Problem is not null ? ProblemColor : s.Ready ? ReadyColor : WaitColor;
            EdgeText(s.Summary, color, layout.TextEdge ?? DefaultEdge);
        }

        // The whole entry is one click target, like a Duty List row.
        var end = ImGui.GetCursorScreenPos();
        var size = new Vector2(Math.Max(ImGui.GetContentRegionAvail().X, 1), Math.Max(end.Y - start.Y, 1));
        ImGui.SetCursorScreenPos(start);
        ImGui.InvisibleButton("entry", size, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        if (ImGui.IsItemHovered())
        {
            ImGui.GetWindowDrawList().AddRectFilled(start, start + size, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.06f)), 3);
            DrawTooltip(view);
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                OnClick(o);
            else if (ImGui.IsMouseReleased(ImGuiMouseButton.Right))
                ImGui.OpenPopup("actions");
        }
        DrawActions(o);

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + (4 * scale)));
        ImGui.Dummy(Vector2.Zero);
        ImGui.PopID();
    }

    private void OnClick(Objective o)
    {
        if (!o.HasSpot)
        {
            print($"\"{o.Title}\" has no map location.");
            return;
        }

        try
        {
            tracker.PlaceFlag(o, openMap: true);
        }
        catch (Exception ex)
        {
            print($"Could not place the flag for \"{o.Title}\": {ex.Message}");
        }
    }

    private void DrawActions(Objective o)
    {
        if (!ImGui.BeginPopup("actions"))
            return;
        ImGui.SetWindowFontScale(1f);
        ImGui.TextDisabled(o.Title);
        ImGui.Separator();
        var now = tracker.Store.Now;
        if (!o.Completed && o.Steps.Count > 0 && ImGui.MenuItem("Step done"))
            tracker.Store.Update(o.Id, x => ObjectiveFactory.Advance(x, now));
        if (!o.Completed && o.StepsDone > 0 && ImGui.MenuItem("Back one step"))
            tracker.Store.Update(o.Id, x => ObjectiveFactory.SetCurrentStep(x, Math.Max(0, x.StepsDone - 1), now));
        if (!o.Completed && ImGui.MenuItem("Complete"))
            tracker.Store.Update(o.Id, x => ObjectiveFactory.Complete(x, true, now));
        if (o.Completed && ImGui.MenuItem("Reopen"))
            tracker.Store.Update(o.Id, x => ObjectiveFactory.SetCurrentStep(ObjectiveFactory.Complete(x, false, now), 0, now));
        if (o.HasSpot && ImGui.MenuItem("Place flag (keep map closed)"))
            OnFlagOnly(o);
        ImGui.Separator();
        if (ImGui.MenuItem("Remove"))
            tracker.Store.Remove(o.Id);
        ImGui.EndPopup();
    }

    private void OnFlagOnly(Objective o)
    {
        try
        {
            tracker.PlaceFlag(o, openMap: false);
        }
        catch (Exception ex)
        {
            print($"Could not place the flag for \"{o.Title}\": {ex.Message}");
        }
    }

    private static void DrawTooltip(ObjectiveView view)
    {
        var o = view.Objective;
        var s = view.Status;
        using var tooltip = ImRaii.Tooltip();
        ImGui.SetWindowFontScale(1f);
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
        ImGui.TextUnformatted(o.Title);
        if (o.Description is { } description)
            ImGui.TextDisabled(description);
        ImGui.Separator();
        for (var i = 0; i < o.Steps.Count; i++)
            ImGui.TextUnformatted($"{(o.Steps[i].Done ? "[x]" : i == o.CurrentStepIndex ? "[>]" : "[ ]")} {o.Steps[i].Text}");
        if (o.Steps.Count > 0)
            ImGui.Separator();
        var where = string.Join(", ", new[] { o.Zone, o.HasSpot ? $"({o.MapX:0.0}, {o.MapY:0.0})" : null, o.Spot }.Where(x => x is not null));
        if (where.Length > 0)
            ImGui.TextUnformatted($"Where: {where}{(s.DistanceYalms is { } d ? $" - {d:0} yalms away" : "")}");
        if (o.Conditions.EorzeaTime is { } time)
            ImGui.TextUnformatted($"Eorzea time: {time}{(s.TimeOk == true ? " (now)" : "")}");
        if (o.Conditions.WeatherConstraint.Count > 0)
            ImGui.TextUnformatted($"Weather: {string.Join(", ", o.Conditions.WeatherConstraint)}{(s.CurrentWeather is { } w ? $" (now {w})" : "")}");
        if (s.NextWindowStart is { } next && next > DateTimeOffset.UtcNow)
            ImGui.TextUnformatted($"Next window: {next.ToLocalTime():HH:mm}{(s.NextWindowEnd is { } end ? $" to {end.ToLocalTime():HH:mm}" : "")} local time");
        if (o.Source is { } source)
            ImGui.TextDisabled($"From {source}. Id: {o.Id}");
        ImGui.TextDisabled(o.HasSpot ? "Click: place flag and open map. Right-click: progress." : "Right-click: progress.");
        ImGui.PopTextWrapPos();
    }

    /// <summary>Text with a one-pixel outline, like the game's edged UI text.</summary>
    private static void EdgeText(string text, Vector4 color, Vector4 edge)
    {
        var pos = ImGui.GetCursorScreenPos();
        var wrap = ImGui.GetContentRegionAvail().X;
        var size = ImGui.CalcTextSize(text, false, wrap);
        var draw = ImGui.GetWindowDrawList();
        var fontPtr = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        var edgeColor = ImGui.GetColorU32(edge);
        foreach (var offset in new[] { new Vector2(-1, 0), new Vector2(1, 0), new Vector2(0, -1), new Vector2(0, 1) })
            draw.AddText(fontPtr, fontSize, pos + offset, edgeColor, text, wrap);
        draw.AddText(fontPtr, fontSize, pos, ImGui.GetColorU32(color), text, wrap);
        ImGui.Dummy(size);
    }

    public void Dispose() => font.Dispose();
}
