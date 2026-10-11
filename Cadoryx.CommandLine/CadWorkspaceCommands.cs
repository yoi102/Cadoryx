namespace Cadoryx.CommandLine;

public interface ICadWorkspaceCommandContext
{
    Task<CadCommandResult> ExecuteWorkspaceAsync(string requestJson, CancellationToken cancellationToken = default);
}

public sealed partial class CadCommandLineService
{
    private void RegisterWorkspaceCommands() => Register("WORKSPACE", "WS",
        "WORKSPACE LIST | CONTRACT | {action:new/open/activate,name?/path?/target?}",
        "List sessions or create, open and activate an exact document. Available without an active document; terminal captures workspace identity.",
        (_, _) => Done(false, "Workspace authoring is unavailable in this context."));
}
