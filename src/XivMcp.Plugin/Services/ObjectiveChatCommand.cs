using Dalamud.Plugin;
using XivMcp.Plugin.Objectives;

namespace XivMcp.Plugin.Services;

/// <summary>"/xivmcp quests ..." — the player's side of custom objectives. Runs on the framework thread (command handler).</summary>
public sealed class ObjectiveChatCommand(ObjectiveTracker tracker, Configuration config, IDalamudPluginInterface pluginInterface, Action<string> print)
{
    public void Handle(string text)
    {
        if (ObjectiveCommands.Parse(text, out var error) is not { } command)
        {
            print(error!);
            return;
        }

        var store = tracker.Store;
        var now = store.Now;
        var id = command.Argument ?? "";
        switch (command.Verb)
        {
            case "list":
                List();
                break;
            case "done":
                Change(id, o => ObjectiveFactory.Complete(o, true, now), o => $"\"{o.Title}\" complete.");
                break;
            case "next":
                Change(id, o => ObjectiveFactory.Advance(o, now), o => o.Completed ? $"\"{o.Title}\" complete." : $"\"{o.Title}\": {o.CurrentStepText}");
                break;
            case "undo":
                Change(
                    id,
                    o => o.Completed
                        ? ObjectiveFactory.SetCurrentStep(ObjectiveFactory.Complete(o, false, now), Math.Max(0, o.Steps.Count - 1), now)
                        : ObjectiveFactory.SetCurrentStep(o, Math.Max(0, o.StepsDone - 1), now),
                    o => $"\"{o.Title}\": {o.CurrentStepText ?? "reopened"}");
                break;
            case "flag":
                if (Find(id) is { } flagged)
                {
                    try
                    {
                        print($"flag placed: {tracker.PlaceFlag(flagged, openMap: true)}");
                    }
                    catch (InvalidOperationException ex)
                    {
                        print(ex.Message);
                    }
                }

                break;
            case "load":
                Load(id);
                break;
            case "clear-done":
                print($"removed {store.Remove(null, completedOnly: true)} completed objective(s).");
                break;
            case "remove":
                print(store.Remove(id) > 0 ? $"removed \"{id}\"." : $"no objective \"{id}\".");
                break;
            case "show" or "hide":
                config.ShowObjectives = command.Verb == "show";
                pluginInterface.SavePluginConfig(config);
                print(config.ShowObjectives ? "objectives shown." : "objectives hidden.");
                break;
        }
    }

    private void List()
    {
        var views = tracker.EvaluateNow();
        if (views.Count == 0)
        {
            print($"no custom objectives. Agents post them with post_objective; load a pack with \"{Plugin.Command} quests load <file>\".");
            return;
        }

        foreach (var view in views)
        {
            var o = view.Objective;
            var step = o.Completed ? "complete" : o.CurrentStepText is { } current ? $"step {o.CurrentStepIndex + 1}/{o.Steps.Count}: {current}" : "no steps";
            print($"[{o.Id}] {o.Title} - {step}{(o.Completed ? "" : $" ({view.Status.Summary})")}");
        }
    }

    private void Load(string path)
    {
        try
        {
            var result = ObjectiveCommands.LoadPack(tracker.Store, null, path);
            print($"loaded {result.Loaded} objective(s){(result.Title is { } title ? $" from \"{title}\"" : "")}; {result.Total} in total.");
            foreach (var problem in result.Errors.Take(5))
                print("skipped " + problem);
            if (result.Errors.Count > 5)
                print($"... and {result.Errors.Count - 5} more problem(s).");
        }
        catch (ArgumentException ex)
        {
            print(ex.Message);
        }
    }

    private Objective? Find(string id)
    {
        var found = tracker.Store.Get(id);
        if (found is null)
            print($"no objective \"{id}\". \"{Plugin.Command} quests list\" shows the ids.");
        return found;
    }

    private void Change(string id, Func<Objective, Objective> change, Func<Objective, string> message)
    {
        if (Find(id) is null)
            return;
        try
        {
            if (tracker.Store.Update(id, change) is { } updated)
                print(message(updated));
        }
        catch (ArgumentException ex)
        {
            print(ex.Message);
        }
    }
}
