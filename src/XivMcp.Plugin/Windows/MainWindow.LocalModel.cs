using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using XivMcp.Plugin.Services;

namespace XivMcp.Plugin.Windows;

/// <summary>
/// Settings: the local model companion plugins should use, and whether they may connect themselves over IPC.
/// Detect and Test run on the thread pool (LocalModelProbe); the draw loop only reads finished tasks.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ModelChatTimeout = TimeSpan.FromSeconds(60);

    private bool modelDraftLoaded;
    private string draftModelEndpoint = "";
    private string draftModelName = "";
    private string draftModelKey = "";
    private string? modelSaveError;
    private Task<IReadOnlyList<LocalModelServer>>? detectTask;
    private Task<LocalModelTestResult>? modelTestTask;

    /// <summary>Called after the local model block is saved (raises XivMcp.LocalModelChanged).</summary>
    public Action? LocalModelSaved { get; set; }

    private void ResetModelDraft()
    {
        draftModelEndpoint = config.LocalModelEndpoint;
        draftModelName = config.LocalModelName;
        draftModelKey = config.LocalModelApiKey;
        modelSaveError = null;
        modelDraftLoaded = true;
    }

    private bool ModelDraftDirty =>
        draftModelEndpoint.Trim().TrimEnd('/') != config.LocalModelEndpoint
        || draftModelName.Trim() != config.LocalModelName
        || draftModelKey.Trim() != config.LocalModelApiKey;

    private void DrawLocalModelSettings()
    {
        if (!ImGui.CollapsingHeader("Local model"))
            return;
        if (!modelDraftLoaded)
            ResetModelDraft();

        ImGui.PushTextWrapPos(0);
        ImGui.TextDisabled("XivMcp does not run the model. This tells companion plugins (Almanac, Ghostty /ask) which local model to use.");
        ImGui.PopTextWrapPos();

        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("Endpoint##model", "http://127.0.0.1:11434/v1", ref draftModelEndpoint, 256);
        HelpMarker("OpenAI-compatible base URL. Ollama http://127.0.0.1:11434/v1, LM Studio http://127.0.0.1:1234/v1, " +
                   "llama.cpp server http://127.0.0.1:8080/v1, KoboldCpp http://127.0.0.1:5001/v1. Empty = not configured.");
        if (LocalModelProbe.NormalizeEndpoint(draftModelEndpoint) == null)
            ImGui.TextColored(ImGuiColors.DalamudRed, "Not an http:// or https:// URL.");

        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("Model##model", "e.g. llama3.2:3b", ref draftModelName, 256);
        ImGui.SetNextItemWidth(300);
        ImGui.InputTextWithHint("API key (optional)##model", "only if your server requires one", ref draftModelKey, 512, ImGuiInputTextFlags.Password);
        HelpMarker("Stored in XivMcp.json. Never logged and never given to other plugins; they only learn whether one is set.");

        var detecting = detectTask is { IsCompleted: false };
        ImGui.BeginDisabled(detecting);
        if (ImGui.Button(detecting ? "Detecting…##model" : "Detect##model"))
            detectTask = Task.Run(async () =>
            {
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
                return await new LocalModelProbe(http).DetectAsync().ConfigureAwait(false);
            });
        ImGui.EndDisabled();
        HelpMarker("Looks for Ollama, LM Studio, llama.cpp and KoboldCpp on this machine at their default ports.");

        ImGui.SameLine();
        var testing = modelTestTask is { IsCompleted: false };
        ImGui.BeginDisabled(testing || string.IsNullOrEmpty(LocalModelProbe.NormalizeEndpoint(draftModelEndpoint)) || draftModelName.Trim().Length == 0);
        if (ImGui.Button(testing ? "Testing…##model" : "Test##model"))
        {
            var (endpoint, model, key) = (draftModelEndpoint, draftModelName, draftModelKey);
            modelTestTask = Task.Run(async () =>
            {
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
                return await new LocalModelProbe(http).TestAsync(endpoint, model, key, ModelListTimeout, ModelChatTimeout).ConfigureAwait(false);
            });
        }

        ImGui.EndDisabled();
        HelpMarker("Checks the model is listed, then asks it for a one-word reply. The first reply can take a while if the server has to load the model.");

        DrawDetectResults();
        DrawModelTestResult();

        if (ModelDraftDirty)
        {
            if (ImGui.Button("Save##model"))
                SaveModelDraft();
            ImGui.SameLine();
            if (ImGui.Button("Revert##model"))
                ResetModelDraft();
        }

        if (modelSaveError != null)
            ImGui.TextColored(ImGuiColors.DalamudRed, modelSaveError);

        ImGui.Spacing();
        var allow = config.AllowIpcClientTokens;
        if (ImGui.Checkbox("Let other plugins (Almanac, Ghostty) connect themselves", ref allow))
        {
            config.AllowIpcClientTokens = allow;
            SaveConfig(apply: false);
        }

        HelpMarker("A plugin can ask for its own client token over Dalamud IPC (XivMcp.ConnectClient); it appears under Client tokens " +
                   "marked \"via IPC\" and can be revoked there. Game actions and chat from it still require your in-game approval.");
    }

    private void DrawDetectResults()
    {
        if (detectTask is not { IsCompleted: true } task)
            return;
        if (!task.IsCompletedSuccessfully)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, $"Detect failed: {task.Exception?.GetBaseException().Message}");
            return;
        }

        var servers = task.Result;
        if (servers.Count == 0)
        {
            ImGui.TextDisabled("No local model server answered on the default ports.");
            return;
        }

        var current = $"{draftModelEndpoint.Trim().TrimEnd('/')}  {draftModelName.Trim()}";
        ImGui.SetNextItemWidth(300);
        if (!ImGui.BeginCombo("Found##model", "(choose a server / model)"))
            return;
        foreach (var server in servers)
        {
            if (server.Models.Count == 0)
            {
                if (ImGui.Selectable($"{server.BaseUrl}  (no models)##{server.BaseUrl}"))
                    draftModelEndpoint = server.BaseUrl;
                continue;
            }

            foreach (var model in server.Models)
            {
                var label = $"{server.BaseUrl}  {model}";
                if (ImGui.Selectable($"{label}##{label}", label == current))
                    (draftModelEndpoint, draftModelName) = (server.BaseUrl, model);
            }
        }

        ImGui.EndCombo();
    }

    private void DrawModelTestResult()
    {
        if (modelTestTask is not { IsCompleted: true } task)
            return;
        if (!task.IsCompletedSuccessfully)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, $"Test failed: {task.Exception?.GetBaseException().Message}");
            return;
        }

        var result = task.Result;
        var latency = result.LatencyMs is { } ms ? $" ({ms} ms)" : "";
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(result.Ok ? ImGuiColors.HealerGreen : ImGuiColors.DalamudRed, result.Message + latency);
        ImGui.PopTextWrapPos();
    }

    private void SaveModelDraft()
    {
        var endpoint = LocalModelProbe.NormalizeEndpoint(draftModelEndpoint);
        if (endpoint == null)
        {
            modelSaveError = "Endpoint must be an http:// or https:// URL (or empty).";
            return;
        }

        config.LocalModelEndpoint = endpoint;
        config.LocalModelName = draftModelName.Trim();
        config.LocalModelApiKey = draftModelKey.Trim();
        SaveConfig(apply: false);
        ResetModelDraft();
        try
        {
            LocalModelSaved?.Invoke();
        }
        catch
        {
            // Subscribers are other plugins; a failure there must not break the settings tab.
        }
    }
}
