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
        Define("cad_status", "Read the active CAD document identity, state, size, selection and available commands.",
            "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
        Define("cad_command", "Run one registered Cadoryx command. Read cad_status.commands or use HELP for current syntax; report the actual result. Selection and document edits obey the active document's validation.",
            "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"}},\"required\":[\"command\"],\"additionalProperties\":false}"),
        Define("cad_inspect", "Read document objects or drawings. For drawing_views and drawing_dimensions, supply the exact sheetId from drawings; optional id filters one view or dimension. Use offset/limit to page results and check the returned documentId/stateId before edits.",
            "{\"type\":\"object\",\"properties\":{\"area\":{\"type\":\"string\",\"enum\":[\"document\",\"parts\",\"bodies\",\"features\",\"layers\",\"materials\",\"selection\",\"sketches\",\"occurrences\",\"assembly\",\"drawings\",\"drawing_views\",\"drawing_dimensions\"]},\"id\":{\"type\":\"string\"},\"partId\":{\"type\":\"string\"},\"sheetId\":{\"type\":\"string\",\"description\":\"Required for drawing_views and drawing_dimensions; exact GUID from drawings.\"},\"offset\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50}},\"required\":[\"area\"],\"additionalProperties\":false}"),
        Define("cad_select", "Select one exact occurrence or body instance from cad_inspect results. Pass the current documentId/stateId and full path; body selection also requires bodyId and geometry revision. Ambiguous names and stale references are rejected.",
            "{\"type\":\"object\",\"properties\":{\"documentId\":{\"type\":\"string\"},\"stateId\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"bodyId\":{\"type\":\"string\"},\"revision\":{\"type\":\"string\"}},\"required\":[\"documentId\",\"stateId\",\"path\"],\"additionalProperties\":false}"),
        Define("cad_edit", "Edit one exact CAD object through document history. Inspect first and pass documentId/stateId; stale state is rejected. Primitive creation uses exact part/layer/material IDs and mm. Sketch rectangle uses part XY; assembly and drawing views require exact paths. No guessed topology.",
            "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"create_box\",\"create_cylinder\",\"create_sketch_rectangle\",\"rename_sketch\",\"fix_assembly_instance\",\"set_assembly_constraint_enabled\",\"set_assembly_distance\",\"create_drawing_sheet\",\"rename_drawing_sheet\",\"add_drawing_view\"]},\"documentId\":{\"type\":\"string\"},\"stateId\":{\"type\":\"string\"},\"id\":{\"type\":\"string\"},\"revision\":{\"type\":\"string\"},\"name\":{\"type\":\"string\"},\"partId\":{\"type\":\"string\"},\"layerId\":{\"type\":\"string\"},\"materialId\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"sheetId\":{\"type\":\"string\"},\"kind\":{\"type\":\"string\",\"enum\":[\"Front\",\"Top\",\"Right\",\"Isometric\"]},\"enabled\":{\"type\":\"boolean\"},\"distanceMm\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"width\":{\"type\":\"number\"},\"depth\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"height\":{\"type\":\"number\"},\"scale\":{\"type\":\"number\"},\"centerX\":{\"type\":\"number\"},\"centerY\":{\"type\":\"number\"}},\"required\":[\"action\",\"documentId\",\"stateId\"],\"additionalProperties\":false}")
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
            var snapshot = document is null || document.IsDetached ? null : document.Session.Snapshot;
            return JsonSerializer.Serialize(document is null || document.IsDetached
                ? new { active = false, name = "", documentId = "", stateId = "",
                    bodies = 0, parts = 0, features = 0, sketches = 0, drawings = 0, selected = 0,
                    commands = commands.Commands.Select(x => x.Syntax).ToArray() }
                : new { active = true, name = snapshot!.Name, documentId = snapshot.Id.ToString(),
                    stateId = snapshot.StateId.ToString(), bodies = snapshot.Bodies.Count,
                    parts = snapshot.Definitions.Values.OfType<Cadoryx.Db.PartDefinition>().Count(),
                    features = snapshot.Features.Count, sketches = snapshot.Sketches.Count,
                    drawings = snapshot.DrawingSheets.Count, selected = new CadCommandContext(document).SelectionCount,
                    commands = commands.Commands.Select(x => x.Syntax).ToArray() });
        }
        if (call.Name is "cad_inspect" or "cad_select" or "cad_edit")
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
