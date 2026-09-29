using System.Text.Json;
using Cadoryx.AI.Contracts;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Sketching;

namespace Cadoryx.ViewModels.Toolboxes;

/// <summary>Small, identity-bound surface for structured CAD agent calls.</summary>
internal static class CadAgentDocumentTools
{
    public static async Task<string> ExecuteAsync(AiToolCall call, CadDocumentViewModel? document,
        CancellationToken cancellationToken)
    {
        try
        {
            using var parsed = JsonDocument.Parse(call.ArgumentsJson);
            var args = parsed.RootElement;
            if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an object.");
            if (document is null || document.IsDetached) throw new InvalidOperationException("No active document.");
            return call.Name == "cad_inspect" ? Inspect(args, document.Session.Snapshot) :
                await EditAsync(args, document, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or
            CadValidationException or KeyNotFoundException or SketchSolveException or StaleDocumentException)
        {
            return JsonSerializer.Serialize(new { success = false, message = e.Message });
        }
    }

    private static string Inspect(JsonElement args, DocumentSnapshot snapshot)
    {
        var area = RequiredString(args, "area");
        var id = OptionalGuid(args, "id");
        object items = area switch
        {
            "sketches" => snapshot.Sketches.Values.Where(x => id is null || x.Id.Value == id)
                .OrderBy(x => x.Name).ThenBy(x => x.Id.Value).Take(50)
                .Select(x => new { id = x.Id.ToString(), x.Name, partId = x.PartId.ToString(),
                    revision = x.Revision.ToString("D"), points = x.Points.Length, lines = x.Lines.Length,
                    circles = x.Circles.Length, arcs = x.Arcs.Length, beziers = x.Beziers.Length,
                    splines = x.Splines.Length, constraints = x.Constraints.Length }).ToArray(),
            "assembly" => new
            {
                instances = snapshot.EnumerateOccurrences().Where(x => id is null || x.Path.Slots.Contains(new ComponentSlotId(id.Value)))
                    .Take(50).Select(x => new { path = x.Path.ToString(), x.Name,
                        definitionId = x.DefinitionId.ToString() }).ToArray(),
                constraints = snapshot.AssemblyConstraints.Values.Where(x => id is null || x.Id.Value == id)
                    .OrderBy(x => x.Name).ThenBy(x => x.Id.Value).Take(50)
                    .Select(x => new { id = x.Id.ToString(), x.Name, kind = x.Kind.ToString(),
                        x.IsEnabled, primaryPath = x.PrimaryPath.ToString(),
                        secondaryPath = x.SecondaryPath?.ToString(), x.TargetDistanceMm,
                        evaluation = x.Evaluate(snapshot).Status.ToString() }).ToArray()
            },
            "drawings" => snapshot.DrawingSheets.Values.Where(x => id is null || x.Id == id)
                .OrderBy(x => x.Name).ThenBy(x => x.Id).Take(50)
                .Select(x => new { id = x.Id.ToString("D"), x.Name, x.WidthMm, x.HeightMm,
                    unit = x.Unit.ToString(), views = x.Views.Take(128).Select(v => new
                    { id = v.Id.ToString("D"), v.Name, kind = v.Kind.ToString(), v.Scale,
                        center = new { v.CenterMm.X, v.CenterMm.Y }, v.StaleReason,
                        sourceCount = v.Sources.Length }).ToArray(),
                    dimensions = x.Dimensions.Take(50).Select(d => new
                    { id = d.Id.ToString("D"), viewId = d.ViewId.ToString("D"),
                        kind = d.Kind.ToString(), d.Value, d.StaleReason }).ToArray() }).ToArray(),
            _ => throw new ArgumentException("Unknown inspection area.")
        };
        return JsonSerializer.Serialize(new { success = true, documentId = snapshot.Id.ToString(),
            stateId = snapshot.StateId.ToString(), total = area switch
            { "sketches" => snapshot.Sketches.Count, "assembly" => snapshot.AssemblyConstraints.Count,
                _ => snapshot.DrawingSheets.Count }, items });
    }

    private static async Task<string> EditAsync(JsonElement args, CadDocumentViewModel document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (document.IsReadOnly || document.IsClosingRequested)
            throw new InvalidOperationException("The active document cannot be edited.");
        var snapshot = document.Session.Snapshot;
        if (RequiredGuid(args, "documentId") != snapshot.Id.Value ||
            RequiredGuid(args, "stateId") != snapshot.StateId.Value)
            throw new InvalidOperationException("Document or state changed. Inspect again before editing.");
        var action = RequiredString(args, "action");
        ICadDocumentCommand command;
        Guid? createdId = null;
        switch (action)
        {
            case "create_sketch_rectangle":
            {
                var part = new DefinitionId(RequiredGuid(args, "partId"));
                if (!snapshot.Definitions.TryGetValue(part, out var definition) || definition is not PartDefinition)
                    throw new InvalidOperationException("Exact part ID is missing.");
                var x = RequiredNumber(args, "x");
                var y = RequiredNumber(args, "y");
                var width = RequiredNumber(args, "width");
                var height = RequiredNumber(args, "height");
                if (width is <= 0 or > 1_000_000 || height is <= 0 or > 1_000_000 ||
                    Math.Abs(x) > 1_000_000 || Math.Abs(y) > 1_000_000)
                    throw new ArgumentException("Rectangle coordinates or dimensions are outside the supported range.");
                var sketch = CadSketch.Create(part, RequiredString(args, "name"), RigidTransform3d.Identity);
                var draft = new SketchDraft(sketch);
                draft.AddRectangle(new Point2d(x, y), new Point2d(x + width, y + height));
                createdId = sketch.Id.Value;
                command = new UpsertSketchCommand(draft.Value, new ManagedSketchConstraintSolver());
                break;
            }
            case "rename_sketch":
            {
                var id = new SketchId(RequiredGuid(args, "id"));
                var revision = RequiredGuid(args, "revision");
                if (!snapshot.Sketches.TryGetValue(id, out var sketch) || sketch.Revision != revision)
                    throw new InvalidOperationException("Sketch identity or revision changed. Inspect again.");
                command = new UpsertSketchCommand(sketch with { Name = RequiredString(args, "name") },
                    new ManagedSketchConstraintSolver());
                break;
            }
            case "fix_assembly_instance":
            {
                var path = ExactPath(args, "path", snapshot);
                var id = AssemblyConstraintId.New();
                createdId = id.Value;
                command = AssemblyConstraintCommands.AddFixed(id, RequiredString(args, "name"), path);
                break;
            }
            case "set_assembly_constraint_enabled":
            {
                var id = new AssemblyConstraintId(RequiredGuid(args, "id"));
                if (!snapshot.AssemblyConstraints.ContainsKey(id)) throw new KeyNotFoundException("Assembly relation is missing.");
                command = AssemblyConstraintCommands.SetEnabled(id, RequiredBoolean(args, "enabled"));
                break;
            }
            case "set_assembly_distance":
            {
                var id = new AssemblyConstraintId(RequiredGuid(args, "id"));
                if (!snapshot.AssemblyConstraints.ContainsKey(id)) throw new KeyNotFoundException("Assembly relation is missing.");
                command = AssemblyConstraintCommands.SetDistance(id, RequiredNumber(args, "distanceMm"));
                break;
            }
            case "create_drawing_sheet":
            {
                var sheet = TechnicalDrawingSheet.A4Landscape(RequiredString(args, "name"));
                createdId = sheet.Id;
                command = TechnicalDrawingCommands.AddSheet(sheet);
                break;
            }
            case "rename_drawing_sheet":
            {
                var id = RequiredGuid(args, "id");
                if (!snapshot.DrawingSheets.TryGetValue(id, out var sheet))
                    throw new KeyNotFoundException("Drawing sheet is missing.");
                command = TechnicalDrawingCommands.EditSheet(id, RequiredString(args, "name"),
                    sheet.WidthMm, sheet.HeightMm, sheet.Unit, sheet.Title, sheet.Author);
                break;
            }
            case "add_drawing_view":
            {
                var sheetId = RequiredGuid(args, "sheetId");
                if (!snapshot.DrawingSheets.ContainsKey(sheetId))
                    throw new KeyNotFoundException("Drawing sheet is missing.");
                var path = ExactPath(args, "path", snapshot);
                var kindName = RequiredString(args, "kind");
                if (!Enum.TryParse<DrawingViewKind>(kindName, false, out var kind) ||
                    kind == DrawingViewKind.Section)
                    throw new ArgumentException("Drawing view kind must be Front, Top, Right or Isometric.");
                var viewId = Guid.NewGuid();
                createdId = viewId;
                command = TechnicalDrawingCommands.AddView(sheetId, viewId, RequiredString(args, "name"),
                    kind, new Point2d(RequiredNumber(args, "centerX"), RequiredNumber(args, "centerY")),
                    RequiredNumber(args, "scale"), path);
                break;
            }
            default: throw new ArgumentException("Unknown edit action.");
        }
        await document.Session.ExecuteAsync(command, cancellationToken);
        var after = document.Session.Snapshot;
        var resultId = createdId?.ToString("D") ??
            (args.TryGetProperty("id", out var inputId) && inputId.ValueKind == JsonValueKind.String
                ? inputId.GetString() : null);
        return JsonSerializer.Serialize(new { success = true, action, id = resultId,
            documentId = after.Id.ToString(), stateId = after.StateId.ToString() });
    }

    private static string RequiredString(JsonElement args, string key, int maxLength = 256) =>
        args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text && text.Length <= maxLength && !string.IsNullOrWhiteSpace(text)
            ? text : throw new ArgumentException($"Missing or invalid {key}.");
    private static Guid RequiredGuid(JsonElement args, string key) =>
        Guid.TryParse(RequiredString(args, key), out var value) && value != Guid.Empty
            ? value : throw new ArgumentException($"Invalid {key}.");
    private static Guid? OptionalGuid(JsonElement args, string key) =>
        args.TryGetProperty(key, out _) ? RequiredGuid(args, key) : null;
    private static bool RequiredBoolean(JsonElement args, string key) =>
        args.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw new ArgumentException($"Missing or invalid {key}.");
    private static double RequiredNumber(JsonElement args, string key) =>
        args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number : throw new ArgumentException($"Missing or invalid {key}.");

    private static OccurrencePath ExactPath(JsonElement args, string key, DocumentSnapshot snapshot)
    {
        var tokens = RequiredString(args, key, 5000).Split('/');
        if (tokens.Length is < 2 or > 129 || !Guid.TryParse(tokens[0], out var documentId) ||
            documentId != snapshot.Id.Value)
            throw new ArgumentException("Instance path belongs to another document or is malformed.");
        var slots = new List<ComponentSlotId>();
        foreach (var token in tokens.Skip(1))
        {
            if (!Guid.TryParse(token, out var id) || id == Guid.Empty)
                throw new ArgumentException("Invalid instance slot ID.");
            slots.Add(new ComponentSlotId(id));
        }
        var path = new OccurrencePath(snapshot.Id, slots);
        if (!snapshot.EnumerateOccurrences().Any(x => x.Path.Equals(path)))
            throw new InvalidOperationException("Exact instance path is missing. Inspect again.");
        return path;
    }
}
