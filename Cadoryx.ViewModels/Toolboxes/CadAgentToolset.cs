using System.Text.Json;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;

namespace Cadoryx.ViewModels.Toolboxes;

internal sealed class CadAgentToolset : IAgentToolset
{
    private readonly ICadCommandLineService commands;
    private readonly CadDocumentViewModel? document;
    private readonly SynchronizationContext? uiContext;
    private static readonly IReadOnlyList<AiToolDefinition> Definitions =
    [
        Define("cad_status", "Read the active CAD document name, body count, selection and available commands.",
            "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
        Define("cad_command", "Run one registered Cadoryx command (HELP, STATUS, LIST, FIND, SELECTION, SELECT, UNDO, REDO, FIT, VIEW, DESELECT, TOOL, CANCEL, BOX, CYLINDER). Report its actual result. SELECT rejects ambiguous names.",
            "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}},\"required\":[\"command\"],\"additionalProperties\":false}"),
        Define("cad_inspect", "Inspect sketches, assembly instances and relations, or technical drawings. Returns exact document/state and object IDs for later edits; lists are bounded.",
            "{\"type\":\"object\",\"properties\":{\"area\":{\"type\":\"string\",\"enum\":[\"sketches\",\"assembly\",\"drawings\"]},\"id\":{\"type\":\"string\"}},\"required\":[\"area\"],\"additionalProperties\":false}"),
        Define("cad_edit", "Edit one exact CAD object through document history. Inspect first and pass documentId/stateId; stale state is rejected. Sketch rectangle: partId,name,x,y,width,height in mm on part XY. Fixed instance: exact path. Drawing view: sheetId,path,kind,scale,centerX,centerY. No guessed topology.",
            "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"create_sketch_rectangle\",\"rename_sketch\",\"fix_assembly_instance\",\"set_assembly_constraint_enabled\",\"set_assembly_distance\",\"create_drawing_sheet\",\"rename_drawing_sheet\",\"add_drawing_view\"]},\"documentId\":{\"type\":\"string\"},\"stateId\":{\"type\":\"string\"},\"id\":{\"type\":\"string\"},\"revision\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"partId\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"sheetId\":{\"type\":\"string\"},\"kind\":{\"type\":\"string\",\"enum\":[\"Front\",\"Top\",\"Right\",\"Isometric\"]},\"enabled\":{\"type\":\"boolean\"},\"distanceMm\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"width\":{\"type\":\"number\"},\"height\":{\"type\":\"number\"},\"scale\":{\"type\":\"number\"},\"centerX\":{\"type\":\"number\"},\"centerY\":{\"type\":\"number\"}},\"required\":[\"action\",\"documentId\",\"stateId\"],\"additionalProperties\":false}")
    ];

    public CadAgentToolset(ICadCommandLineService commands, CadDocumentViewModel? document)
    {
        this.commands = commands; this.document = document;
        uiContext = SynchronizationContext.Current;
    }

    public IReadOnlyList<AiToolDefinition> ToolDefinitions => Definitions;
    public IReadOnlyList<AiToolDefinition> SelectTools(string prompt, bool aggressive = false) => Definitions;

    public Task<string> ExecuteAsync(AiToolCall call, CancellationToken cancellationToken)
    {
        if (uiContext is null || ReferenceEquals(SynchronizationContext.Current, uiContext))
            return ExecuteCoreAsync(call, cancellationToken);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        uiContext.Post(async _ =>
        {
            try { completion.TrySetResult(await ExecuteCoreAsync(call, cancellationToken)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception e) { completion.TrySetException(e); }
        }, null);
        return completion.Task;
    }

    private async Task<string> ExecuteCoreAsync(AiToolCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (call.Name == "cad_status")
        {
            return JsonSerializer.Serialize(document is null || document.IsDetached
                ? new { active = false, name = "", bodies = 0, selected = 0,
                    commands = commands.Commands.Select(x => x.Syntax).ToArray() }
                : new { active = true, name = document.Session.Snapshot.Name,
                    bodies = document.Session.Snapshot.Bodies.Count, selected = document.Selection.Items.Length,
                    commands = commands.Commands.Select(x => x.Syntax).ToArray() });
        }
        if (call.Name is "cad_inspect" or "cad_edit")
            return await CadAgentDocumentTools.ExecuteAsync(call, document, cancellationToken);
        if (call.Name != "cad_command") return JsonSerializer.Serialize(new { success = false, message = "Unknown tool." });
        string input;
        try
        {
            using var arguments = JsonDocument.Parse(call.ArgumentsJson);
            if (arguments.RootElement.ValueKind != JsonValueKind.Object ||
                !arguments.RootElement.TryGetProperty("command", out var value) || value.ValueKind != JsonValueKind.String)
                return JsonSerializer.Serialize(new { success = false, message = "Missing command." });
            input = value.GetString() ?? "";
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(new { success = false, message = "Invalid tool arguments." });
        }
        if (input.Length > 4096) return JsonSerializer.Serialize(new { success = false, message = "Command too long." });
        var result = await commands.ExecuteAsync(input,
            document is null || document.IsDetached ? null : new CadCommandContext(document));
        return JsonSerializer.Serialize(new { success = result.Success, message = result.Message });
    }

    private static AiToolDefinition Define(string name, string description, string schema) =>
        new(name, description, JsonDocument.Parse(schema).RootElement.Clone());
}
