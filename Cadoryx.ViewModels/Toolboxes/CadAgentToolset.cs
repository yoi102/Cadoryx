using System.Text.Json;
using Cadoryx.Agent;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;

namespace Cadoryx.ViewModels.Toolboxes;

internal sealed class CadAgentToolset : IAgentToolset
{
    private readonly ICadCommandLineService commands;
    private readonly CadDocumentViewModel? document;
    private readonly CadWorkspaceAuthoring? workspace;
    private readonly SynchronizationContext? uiContext;
    private static readonly IReadOnlyList<AiToolDefinition> Definitions =
    [
        Define("cad_status", "Read the active CAD document identity, state, size, selection and available commands.",
            "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
        Define("cad_workspace", "No document needed. list gives exact expected for new/open/activate; target=sessionId/documentId/stateId. Own transitions keep conversation; user switching cancels. Read contract.", CadAgentWorkspaceTools.Schema),
        Define("cad_command", "Run cad_status.commands with exact document/state. HISTORY needs cad_history cursor. Pre-commit cancellation rolls back; completed commits succeed.",
            "{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\"},\"documentId\":{\"type\":\"string\",\"format\":\"uuid\"},\"stateId\":{\"type\":\"string\",\"format\":\"uuid\"}},\"required\":[\"command\"],\"additionalProperties\":false}"),
        Define("cad_inspect", "Read IDs/authors, mm/rad. id=object ID, never documentId; model_contract id=type. drawings gives sheet IDs; drawing_views/dimensions needs sheetId; timeline partId. recipes: edits; sketch_geometry: entities/rank.",
            "{\"type\":\"object\",\"properties\":{\"area\":{\"type\":\"string\",\"enum\":[\"document\",\"parts\",\"bodies\",\"features\",\"layers\",\"materials\",\"selection\",\"sketches\",\"occurrences\",\"assembly\",\"drawings\",\"drawing_views\",\"drawing_dimensions\",\"parameters\",\"parameter_bindings\",\"parameter_targets\",\"datums\",\"datum_associations\",\"timeline\",\"recipes\",\"sketch_geometry\",\"model_contract\",\"assembly_patterns\"]},\"id\":{\"type\":\"string\"},\"partId\":{\"type\":\"string\"},\"sheetId\":{\"type\":\"string\"},\"offset\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50}},\"required\":[\"area\"],\"additionalProperties\":false}"),
        Define("cad_parameter", "Atomic mm/rad/scalar. Read state/parameter_targets; copy full IDs. set_expression/reference: omit id for new, keep for edit. References cannot drive; bind rejects existing driver. unbind needs binding id; delete guards users. Returns IDs/state.", CadAgentParameterTools.Schema),
        Define("cad_history", "Exact documentId/stateId and inspected cursor (null at end); stale cursor rejects. Inspect timeline; suppression needs result revision. Reorder keeps IDs/dependencies/operands at history end. Rollback is session-only. Preview: changed=false, previewChanged/cursor.", CadAgentHistoryTools.Schema),
        Define("cad_datum", "Create/edit planes, axes and points, part-local mm/rad. Read contract for full schemas; copy datums edit input. New omits id/revision; edits keep owner/type. inspect_sources returns native map slots. Exact revisions/full path; no caches. Delete guards dependents; detach retains values; no-op adds no history.", CadAgentDatumTools.Schema),
        Define("cad_select", "Select one inspected occurrence/body. Exact documentId/stateId/full path; body also needs bodyId/revision. Reject stale or ambiguous identity.",
            "{\"type\":\"object\",\"properties\":{\"documentId\":{\"type\":\"string\"},\"stateId\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"bodyId\":{\"type\":\"string\"},\"revision\":{\"type\":\"string\"}},\"required\":[\"documentId\",\"stateId\",\"path\"],\"additionalProperties\":false}"),
        Define("cad_model", "Create/edit production recipes. Read model_contract by type, recipes for edits, sketch_geometry for entities. Exact state/revisions; part-local mm/rad. inspect_contours pages native chains; cad_datum inspect_sources gives seeds. Keep type/operands. set_assembly needs full path; new omits id. Explicit reselect/detach; returns created IDs.",CadAgentModelTools.Schema),
        Define("cad_file", "Write save/export/report/pdf/template_export. Read contract; exact state/absolute path, explicit overwrite, template id. Receipt: bytes/hash/source/diagnostics. Non-save needs history end; PDF rejects stale references; no undo.", CadAgentFileTools.Schema),
        Define("cad_drawing", "contract: only action/type, no IDs. inspect returns edits; inspect_sources native selections. documentId/stateId=document/snapshot; sheetId=sheet; id=typed object. Explicit reselect, no caches. PDF: cad_file.",CadAgentDrawingTools.Schema),
        Define("cad_edit", "Exact document/state, mm. Contact/G2: read sketch_geometry contract, use id/revision. Offset: sketchId/revision, seeds (loop=1), distanceMm, join (default Miter); returns new IDs. No guessed topology.",
            "{\"type\":\"object\",\"properties\":{\"action\":{\"type\":\"string\",\"enum\":[\"create_box\",\"create_cylinder\",\"create_sketch_rectangle\",\"rename_sketch\",\"set_sketch_contact\",\"delete_sketch_contact\",\"set_sketch_curvature\",\"delete_sketch_curvature\",\"set_sketch_finite_contact\",\"delete_sketch_finite_contact\",\"offset_sketch_loop\",\"offset_sketch_regions\",\"reselect_region_sources\",\"fix_assembly_instance\",\"set_assembly_constraint_enabled\",\"set_assembly_distance\",\"create_drawing_sheet\",\"rename_drawing_sheet\",\"add_drawing_view\"]},\"documentId\":{\"type\":\"string\"},\"stateId\":{\"type\":\"string\"},\"id\":{\"type\":\"string\"},\"revision\":{\"type\":\"string\"},\"sketchId\":{\"type\":\"string\"},\"sources\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"string\",\"format\":\"uuid\"},\"minItems\":1,\"maxItems\":4096},\"minItems\":1,\"maxItems\":64},\"seeds\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"minItems\":1,\"maxItems\":256},\"join\":{\"type\":\"string\",\"enum\":[\"Miter\",\"Round\",\"Bevel\"]},\"constraintId\":{\"type\":\"string\"},\"curveA\":{\"type\":\"string\"},\"curveB\":{\"type\":\"string\"},\"parameterA\":{\"type\":\"number\"},\"parameterB\":{\"type\":\"number\"},\"parameterMode\":{\"type\":\"string\",\"enum\":[\"Fixed\",\"SolveA\",\"SolveB\",\"SolveBoth\"]},\"seedA\":{\"type\":\"number\"},\"lowerA\":{\"type\":\"number\"},\"upperA\":{\"type\":\"number\"},\"seedB\":{\"type\":\"number\"},\"lowerB\":{\"type\":\"number\"},\"upperB\":{\"type\":\"number\"},\"direction\":{\"type\":\"string\",\"enum\":[\"SameDirection\",\"OppositeDirection\"]},\"continuity\":{\"type\":\"string\",\"enum\":[\"G1\",\"G2\"]},\"branch\":{\"type\":\"string\",\"enum\":[\"LineLeft\",\"LineRight\",\"External\",\"AContainsB\",\"BContainsA\"]},\"name\":{\"type\":\"string\"},\"partId\":{\"type\":\"string\"},\"layerId\":{\"type\":\"string\"},\"materialId\":{\"type\":\"string\"},\"path\":{\"type\":\"string\"},\"sheetId\":{\"type\":\"string\"},\"kind\":{\"type\":\"string\",\"enum\":[\"Front\",\"Top\",\"Right\",\"Isometric\"]},\"enabled\":{\"type\":\"boolean\"},\"distanceMm\":{\"type\":\"number\"},\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"},\"width\":{\"type\":\"number\"},\"depth\":{\"type\":\"number\"},\"radius\":{\"type\":\"number\"},\"height\":{\"type\":\"number\"},\"scale\":{\"type\":\"number\"},\"centerX\":{\"type\":\"number\"},\"centerY\":{\"type\":\"number\"}},\"required\":[\"action\",\"documentId\",\"stateId\"],\"additionalProperties\":false}")
    ];

    public CadAgentToolset(ICadCommandLineService commands, CadDocumentViewModel? document, CadWorkspaceAuthoring? workspace = null)
    {
        this.commands = commands; this.document = document;
        this.workspace = workspace;
        uiContext = SynchronizationContext.Current;
    }

    public IReadOnlyList<AiToolDefinition> ToolDefinitions => Definitions;
    public IReadOnlyList<AiToolDefinition> SelectTools(string prompt, bool aggressive = false) => Definitions
        .OrderBy(tool => Priority(tool.Name, prompt))
        .ThenBy(tool => tool.Parameters.GetRawText().Length + tool.Description.Length).ToArray();

    private static int Priority(string name, string prompt)
    {
        if (prompt.Contains(name, StringComparison.OrdinalIgnoreCase)) return 0;
        string[] hints = name switch
        {
            "cad_workspace" => ["新建文档", "打开文件", "切换文档", "激活文档", "workspace", "open document", "new document", "activate document"],
            "cad_file" => ["保存", "导出", "报告", "save", "export", "report"],
            "cad_drawing" => ["工程图", "图纸", "剖切", "局部视图", "尺寸", "公差", "孔表", "drawing", "dimension", "tolerance", "hatching", "balloon"],
            "cad_datum" => ["基准", "datum"], "cad_parameter" => ["参数", "表达式", "parameter", "expression"],
            "cad_history" => ["回滚", "抑制", "重排", "rollback", "suppress", "reorder"],
            "cad_edit" => ["草图", "接触", "相切", "曲率", "偏移", "sketch", "contact", "curvature", "offset", "G1", "G2"],
            "cad_model" => ["拉伸", "旋转", "扫掠", "放样", "抽壳", "拔模", "阵列", "镜像", "孔", "extrude", "revolve", "sweep", "loft", "shell", "draft", "pattern", "mirror", "hole"],
            _ => []
        };
        if (hints.Any(hint => prompt.Contains(hint, StringComparison.OrdinalIgnoreCase))) return 0;
        return name is "cad_status" or "cad_inspect" ? 1 : 2;
    }

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
        var document = workspace is null ? this.document : workspace.ActiveDocument;
        if (call.Name == "cad_workspace") return await CadAgentWorkspaceTools.ExecuteAsync(call, workspace, cancellationToken);
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
        if (call.Name == "cad_parameter")
            return await CadAgentParameterTools.ExecuteAsync(call, document, cancellationToken);
        if (call.Name == "cad_history")
            return await CadAgentHistoryTools.ExecuteAsync(call, document, cancellationToken);
        if (call.Name == "cad_datum")
            return await CadAgentDatumTools.ExecuteAsync(call, document, cancellationToken);
        if (call.Name == "cad_model")
            return await CadAgentModelTools.ExecuteAsync(call, document, cancellationToken);
        if (call.Name == "cad_file")
            return await CadAgentFileTools.ExecuteAsync(call, document, cancellationToken);
        if (call.Name == "cad_drawing")
            return await CadAgentDrawingTools.ExecuteAsync(call, document, cancellationToken);
        if (call.Name != "cad_command") return JsonSerializer.Serialize(new { success = false, message = "Unknown tool." });
        string input;
        CadCommandContext? context = null;
        try
        {
            using var arguments = JsonDocument.Parse(call.ArgumentsJson);
            if (arguments.RootElement.ValueKind != JsonValueKind.Object ||
                !arguments.RootElement.TryGetProperty("command", out var value) || value.ValueKind != JsonValueKind.String)
                return JsonSerializer.Serialize(new { success = false, message = "Missing command." });
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in arguments.RootElement.EnumerateObject())
                if (property.Name is not ("command" or "documentId" or "stateId") || !names.Add(property.Name))
                    throw new ArgumentException("Unknown or duplicate command argument.");
            input = value.GetString() ?? "";
            if (document is not null && !document.IsDetached)
            {
                Guid ExactGuid(string name)
                {
                    if (!arguments.RootElement.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String ||
                        !Guid.TryParse(item.GetString(), out var id) || id == Guid.Empty)
                        throw new ArgumentException("An active document requires exact documentId and stateId. Inspect again before running commands.");
                    return id;
                }
                context = new CadCommandContext(document, new(new DocumentId(ExactGuid("documentId")), new DocumentStateId(ExactGuid("stateId"))));
            }
            else if (names.Contains("documentId") || names.Contains("stateId"))
                throw new ArgumentException("The requested document is no longer active.");
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return JsonSerializer.Serialize(new { success = false, message = error is JsonException ? "Invalid tool arguments." : error.Message });
        }
        if (input.Length > 4096) return JsonSerializer.Serialize(new { success = false, message = "Command too long." });
        try
        {
            var first = CadCommandLineService.Tokenize(input).FirstOrDefault();
            if (first?.Equals("HISTORY", StringComparison.OrdinalIgnoreCase) == true)
                return JsonSerializer.Serialize(new { success = false, message = "Use cad_history with the exact inspected cursor for history commands." });
            if (first?.Equals("WORKSPACE", StringComparison.OrdinalIgnoreCase) == true || first?.Equals("WS", StringComparison.OrdinalIgnoreCase) == true)
                return JsonSerializer.Serialize(new { success = false, message = "Use cad_workspace with the exact inspected workspace identity." });
        }
        catch (FormatException error) { return JsonSerializer.Serialize(new { success = false, message = error.Message }); }
        var result = await commands.ExecuteAsync(input, context, cancellationToken);
        if (result.IsCancelled) throw new OperationCanceledException(cancellationToken);
        return JsonSerializer.Serialize(new { success = result.Success, message = result.Message,
            documentId = context is null ? null : document!.Session.Snapshot.Id.ToString(),
            stateId = context is null ? null : document!.Session.Snapshot.StateId.ToString() });
    }

    private static AiToolDefinition Define(string name, string description, string schema) =>
        // The transport emits compact JSON. Store that representation so context budgeting
        // does not charge formatting whitespace and silently drop an otherwise fitting tool.
        new(name, description, JsonSerializer.SerializeToElement(JsonDocument.Parse(schema).RootElement));
}
