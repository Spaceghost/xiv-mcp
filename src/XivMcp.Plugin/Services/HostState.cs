using Dalamud.Plugin.Services;
using XivMcp.Core;

namespace XivMcp.Plugin.Services;

/// <summary>
/// Answers the server's gatekeeping questions from <see cref="Configuration"/> and client state. Action and Chat follow
/// their tier toggles; in-game confirmation of those calls is <see cref="ConfirmationService"/>, which the server awaits
/// as its <see cref="IToolCallApprover"/>.
/// </summary>
public sealed class HostState : IHostState
{
    private readonly Configuration config;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;

    public HostState(Configuration config, IClientState clientState, IObjectTable objectTable, IFramework framework)
    {
        this.config = config;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
    }

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

    public bool IsPermitted(ToolPermission permission) => config.IsPermitted(permission);

    public bool IsCategoryEnabled(string category) => config.IsCategoryEnabled(category);
}
