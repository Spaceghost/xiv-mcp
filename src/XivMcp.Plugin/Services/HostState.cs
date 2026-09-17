using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Services;

/// <summary>
/// Answers the server's gatekeeping questions from <see cref="Configuration"/> and client state.
/// </summary>
/// <remarks>
/// In-game confirmation of Action/Chat calls needs a server-side hook that the frozen Core
/// contract does not have yet. The intended additive Core interface is
/// <c>XivMcp.Core.IToolCallApprover</c> with exactly the signature of
/// <see cref="ApproveToolCallAsync"/>; the server would await it (off the game thread) before
/// invoking any Action/Chat tool. Until this class implements that interface, Action/Chat are
/// reported as NOT permitted whenever <see cref="Configuration.ConfirmActions"/> is on
/// (fail closed), and the UI says so.
/// </remarks>
public sealed class HostState : IHostState
{
    private const string ApproverInterfaceName = "XivMcp.Core.IToolCallApprover";

    private readonly Configuration config;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly ConfirmationService confirmations;

    public HostState(Configuration config, IClientState clientState, IObjectTable objectTable, IFramework framework, ConfirmationService confirmations)
    {
        this.config = config;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
        this.confirmations = confirmations;
        ConfirmationHookActive = GetType().GetInterfaces().Any(i => i.FullName == ApproverInterfaceName);
    }

    /// <summary>True once the server can await <see cref="ApproveToolCallAsync"/> (see remarks).</summary>
    public bool ConfirmationHookActive { get; }

    /// <summary>Action/Chat are blocked because confirmation is requested but cannot be enforced.</summary>
    public bool ConfirmationFailClosed => config.ConfirmActions && !ConfirmationHookActive;

    public bool IsLoggedIn
    {
        get
        {
            if (!clientState.IsLoggedIn)
                return false;

            // The object table must only be read on the framework thread.
            return !framework.IsInFrameworkUpdateThread || objectTable.LocalPlayer != null;
        }
    }

    public bool IsPermitted(ToolPermission permission)
    {
        if (!config.IsPermitted(permission))
            return false;
        if (permission >= ToolPermission.Action && ConfirmationFailClosed)
            return false;
        return true;
    }

    public bool IsCategoryEnabled(string category) => config.IsCategoryEnabled(category);

    /// <summary>
    /// Awaits the player's in-game decision for an Action/Chat call (true = run it). Returns true
    /// immediately for Read/Ui or when confirmations are off; false on deny, timeout, cancel or unload.
    /// Must not be called on the game thread (it waits for the Draw loop).
    /// </summary>
    public Task<bool> ApproveToolCallAsync(string toolName, ToolPermission permission, string? clientName, string? argumentsJson, CancellationToken cancellationToken)
    {
        if (permission < ToolPermission.Action)
            return Task.FromResult(true);
        if (framework.IsInFrameworkUpdateThread)
            return Task.FromResult(false);
        return confirmations.RequestAsync(toolName, permission, clientName, argumentsJson, cancellationToken);
    }
}
