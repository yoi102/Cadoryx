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
    private static readonly JsonSerializerOptions InspectJsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<string> ExecuteAsync(AiToolCall call, CadDocumentViewModel? document,
        CancellationToken cancellationToken)
    {
        try
        {
            using var parsed = JsonDocument.Parse(call.ArgumentsJson);
            var args = parsed.RootElement;
            if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an object.");
            if (document is null || document.IsDetached) throw new InvalidOperationException("No active document.");
            return call.Name switch
            {
                "cad_inspect" => Inspect(args, document),
                "cad_select" => Select(args, document),
                _ => await EditAsync(args, document, cancellationToken)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or
            CadValidationException or KeyNotFoundException or SketchSolveException or StaleDocumentException)
        {
            return JsonSerializer.Serialize(new { success = false, message = e.Message });
        }
    }

    private static string Select(JsonElement args, CadDocumentViewModel document)
    {
        var snapshot = document.Session.Snapshot;
        if (RequiredGuid(args, "documentId") != snapshot.Id.Value ||
            RequiredGuid(args, "stateId") != snapshot.StateId.Value)
            throw new InvalidOperationException("Document or state changed. Inspect again before selecting.");
        var path = ExactPath(args, "path", snapshot);
        var occurrence = snapshot.EnumerateOccurrences().Single(x => x.Path.Equals(path));
        var bodyId = OptionalGuid(args, "bodyId");
        if (bodyId is null)
        {
            if (args.TryGetProperty("revision", out _))
                throw new ArgumentException("A geometry revision requires a bodyId.");
            document.Selection.SelectOccurrence(path);
            return JsonSerializer.Serialize(new { success = true, documentId = snapshot.Id.ToString(),
                stateId = snapshot.StateId.ToString(), path = path.ToString(), bodyId = (string?)null });
        }

        var selected = new BodyId(bodyId.Value);
        var revision = new GeometryRevisionId(RequiredGuid(args, "revision"));
        if (!snapshot.Bodies.TryGetValue(selected, out var body) ||
            body.PartId != occurrence.DefinitionId || body.Geometry.Revision != revision)
            throw new InvalidOperationException("Body, instance path or geometry revision changed. Inspect again.");
        document.Selection.Replace([new SelectionTarget(path, selected, revision)]);
        return JsonSerializer.Serialize(new { success = true, documentId = snapshot.Id.ToString(),
            stateId = snapshot.StateId.ToString(), path = path.ToString(),
            bodyId = selected.ToString(), revision = revision.ToString() });
    }

    private static string Inspect(JsonElement args, CadDocumentViewModel document)
    {
        var snapshot = document.Session.Snapshot;
        var area = RequiredString(args, "area");
        var id = OptionalGuid(args, "id");
        var partId = OptionalGuid(args, "partId");
        var offset = OptionalInt(args, "offset", 0, 0, 100000);
        var limit = OptionalInt(args, "limit", 50, 1, 50);
        object result = area switch
        {
            "document" => Page(snapshot, area, new[] { new
            {
                rootAssemblyId = snapshot.RootAssemblyId.ToString(),
                displayUnit = snapshot.Settings.DisplayUnit.ToString(),
                grid = new { snapshot.Settings.Grid.Visible, snapshot.Settings.Grid.SpacingMm,
                    snapshot.Settings.Grid.Snap },
                workPlane = snapshot.Settings.WorkPlane.Kind.ToString(),
                counts = new { parts = snapshot.Definitions.Values.OfType<PartDefinition>().Count(),
                    bodies = snapshot.Bodies.Count, features = snapshot.Features.Count,
                    sketches = snapshot.Sketches.Count, layers = snapshot.Layers.Count,
                    materials = snapshot.Materials.Count, drawings = snapshot.DrawingSheets.Count,
                    assemblyConstraints = snapshot.AssemblyConstraints.Count }
            } }, offset, limit),
            "parts" => Page(snapshot, area, snapshot.Definitions.Values.OfType<PartDefinition>()
                .Where(x => id is null || x.Id.Value == id)
                .OrderBy(x => x.Name).ThenBy(x => x.Id.Value)
                .Select(x => new { id = x.Id.ToString(), x.Name, bodies = x.Bodies.Length,
                    features = x.Features.Length, external = snapshot.ExternalParts.ContainsKey(x.Id) }), offset, limit),
            "bodies" => Page(snapshot, area, snapshot.Bodies.Values
                .Where(x => (id is null || x.Id.Value == id) && (partId is null || x.PartId.Value == partId))
                .OrderBy(x => x.Name).ThenBy(x => x.Id.Value)
                .Select(x => new { id = x.Id.ToString(), x.Name, partId = x.PartId.ToString(),
                    featureId = x.Producer?.ToString(), kind = x.Geometry.Kind.ToString(),
                    revision = x.Geometry.Revision.ToString(), x.Geometry.VolumeMm3,
                    bounds = x.Geometry.Bounds, layerId = x.LayerId.ToString(),
                    materialId = x.MaterialId?.ToString(), x.IsVisible }), offset, limit),
            "features" => Page(snapshot, area, snapshot.Features.Values
                .Where(x => (id is null || x.Id.Value == id) && (partId is null || x.PartId.Value == partId))
                .OrderBy(x => x.Name).ThenBy(x => x.Id.Value)
                .Select(x => new { id = x.Id.ToString(), x.Name, partId = x.PartId.ToString(),
                    recipe = x.Recipe.GetType().Name, outputBodyId = x.OutputBodyId.ToString(),
                    inputs = x.Inputs.Select(input => input.ToString()).ToArray(),
                    x.IsStale, x.IsSuppressed }), offset, limit),
            "layers" => Page(snapshot, area, snapshot.Layers.Values
                .Where(x => id is null || x.Id.Value == id)
                .OrderBy(x => x.Name).ThenBy(x => x.Id.Value)
                .Select(x => new { id = x.Id.ToString(), x.Name, x.Argb, x.IsVisible, x.IsLocked }),
                offset, limit),
            "materials" => Page(snapshot, area, snapshot.Materials.Values
                .Where(x => id is null || x.Id.Value == id)
                .OrderBy(x => x.Name).ThenBy(x => x.Id.Value)
                .Select(x => new { id = x.Id.ToString(), x.Name, x.DensityKgPerMm3 }), offset, limit),
            "selection" => Page(snapshot, area, document.Selection.Items
                .Where(x => id is null || x.BodyId.Value == id)
                .Select(x => new { path = x.Path.ToString(), bodyId = x.BodyId.ToString(),
                    revision = x.GeometryRevision.ToString(),
                    name = snapshot.Bodies.TryGetValue(x.BodyId, out var body) ? body.Name : null,
                    current = snapshot.Bodies.TryGetValue(x.BodyId, out var current) &&
                        current.Geometry.Revision == x.GeometryRevision })
                .Cast<object>().Concat(document.Selection.Items.IsEmpty && document.Selection.Occurrence is { } path
                    ? new object[] { new { path = path.ToString(), bodyId = (string?)null,
                        revision = (string?)null, name = (string?)null,
                        current = snapshot.EnumerateOccurrences().Any(x => x.Path.Equals(path)) } }
                    : []), offset, limit),
            "sketches" => Page(snapshot, area, snapshot.Sketches.Values
                .Where(x => (id is null || x.Id.Value == id) &&
                    (partId is null || x.PartId.Value == partId))
                .OrderBy(x => x.Name).ThenBy(x => x.Id.Value)
                .Select(x => new { id = x.Id.ToString(), x.Name, partId = x.PartId.ToString(),
                    revision = x.Revision.ToString("D"), points = x.Points.Length, lines = x.Lines.Length,
                    circles = x.Circles.Length, arcs = x.Arcs.Length, beziers = x.Beziers.Length,
                    splines = x.Splines.Length, constraints = x.Constraints.Length }), offset, limit),
            "occurrences" => Page(snapshot, area, snapshot.EnumerateOccurrences()
                .Where(x => id is null || x.Path.Slots.Contains(new ComponentSlotId(id.Value)))
                .Select(x => new { path = x.Path.ToString(), x.Name,
                    definitionId = x.DefinitionId.ToString(), x.IsVisible,
                    translation = x.WorldTransform.Translation }), offset, limit),
            "assembly" => InspectAssembly(snapshot, id, offset, limit),
            "drawings" => Page(snapshot, area, snapshot.DrawingSheets.Values
                .Where(x => id is null || x.Id == id)
                .OrderBy(x => x.Name).ThenBy(x => x.Id)
                .Select(x => new { id = x.Id.ToString("D"), x.Name, x.WidthMm, x.HeightMm,
                    unit = x.Unit.ToString(), viewTotal = x.Views.Length,
                    dimensionTotal = x.Dimensions.Length,
                    views = x.Views.Take(128).Select(v => new
                    { id = v.Id.ToString("D"), v.Name, kind = v.Kind.ToString(), v.Scale,
                        center = new { v.CenterMm.X, v.CenterMm.Y }, v.StaleReason,
                        sourceCount = v.Sources.Length }).ToArray(),
                    dimensions = x.Dimensions.Take(50).Select(d => new
                    { id = d.Id.ToString("D"), viewId = d.ViewId.ToString("D"),
                        kind = d.Kind.ToString(), d.Value, d.StaleReason }).ToArray() }), offset, limit),
            "drawing_views" or "drawing_dimensions" => InspectDrawingItems(snapshot, args, area,
                id, offset, limit),
            _ => throw new ArgumentException("Unknown inspection area.")
        };
        return JsonSerializer.Serialize(result, InspectJsonOptions);
    }

    private static object InspectDrawingItems(DocumentSnapshot snapshot, JsonElement args, string area,
        Guid? id, int offset, int limit)
    {
        var sheetId = RequiredGuid(args, "sheetId");
        if (!snapshot.DrawingSheets.TryGetValue(sheetId, out var sheet))
            throw new InvalidOperationException("Exact drawing sheet ID is missing. Inspect drawings again.");
        if (area == "drawing_views")
            return Page(snapshot, area, sheet.Views
                .Where(view => id is null || view.Id == id)
                .OrderBy(view => view.Name).ThenBy(view => view.Id)
                .Select(view => new
                {
                    sheetId = sheetId.ToString("D"), id = view.Id.ToString("D"), view.Name,
                    kind = view.Kind.ToString(), parentView = view.ParentView?.ToString("D"),
                    view.Scale, center = new { view.CenterMm.X, view.CenterMm.Y },
                    view.StaleReason, sourceCount = view.Sources.Length,
                    sources = view.Sources.Select(source => new
                    {
                        path = source.Path.ToString(), bodyId = source.Body.ToString(),
                        featureId = source.Feature?.ToString(), revision = source.Revision.ToString(),
                        asset = source.Asset.Sha256
                    }).ToArray()
                }), offset, limit);

        return Page(snapshot, area, sheet.Dimensions
            .Where(dimension => id is null || dimension.Id == id)
            .OrderBy(dimension => dimension.Id)
            .Select(dimension => new
            {
                sheetId = sheetId.ToString("D"), id = dimension.Id.ToString("D"),
                viewId = dimension.ViewId.ToString("D"), kind = dimension.Kind.ToString(),
                dimension.Value, dimension.UpperTolerance, dimension.LowerTolerance,
                dimension.StaleReason, first = DatumIdentity(dimension.First),
                second = dimension.Second is { } second ? DatumIdentity(second) : null
            }), offset, limit);
    }

    private static object DatumIdentity(AssemblyDatumReference datum) => new
    {
        path = datum.Path.ToString(), definitionId = datum.DefinitionId.ToString(),
        bodyId = datum.BodyId.ToString(), featureId = datum.FeatureId?.ToString(),
        revision = datum.Revision.ToString(), asset = datum.Asset.Sha256,
        datum.Fingerprint, datum.FullTopologyIndex, geometry = datum.Geometry.ToString()
    };

    private static object Page<T>(DocumentSnapshot snapshot, string area, IEnumerable<T> source,
        int offset, int limit)
    {
        var total = source.Count();
        var items = source.Skip(offset).Take(limit).ToArray();
        return new { success = true, documentId = snapshot.Id.ToString(),
            stateId = snapshot.StateId.ToString(), area, total, offset, limit,
            hasMore = offset + items.Length < total, items };
    }

    private static object InspectAssembly(DocumentSnapshot snapshot, Guid? id, int offset, int limit)
    {
        var instances = snapshot.EnumerateOccurrences()
            .Where(x => id is null || x.Path.Slots.Contains(new ComponentSlotId(id.Value)))
            .Select(x => new { path = x.Path.ToString(), x.Name,
                definitionId = x.DefinitionId.ToString() });
        var constraints = snapshot.AssemblyConstraints.Values
            .Where(x => id is null || x.Id.Value == id)
            .OrderBy(x => x.Name).ThenBy(x => x.Id.Value)
            .Select(x => new { id = x.Id.ToString(), x.Name, kind = x.Kind.ToString(),
                x.IsEnabled, primaryPath = x.PrimaryPath.ToString(),
                secondaryPath = x.SecondaryPath?.ToString(), x.TargetDistanceMm,
                evaluation = x.Evaluate(snapshot).Status.ToString() });
        var instanceTotal = instances.Count();
        var constraintTotal = constraints.Count();
        return new { success = true, documentId = snapshot.Id.ToString(),
            stateId = snapshot.StateId.ToString(), area = "assembly", offset, limit,
            total = constraintTotal, instanceTotal,
            hasMoreInstances = offset + limit < instanceTotal,
            hasMoreConstraints = offset + limit < constraintTotal,
            items = new { instances = instances.Skip(offset).Take(limit).ToArray(),
                constraints = constraints.Skip(offset).Take(limit).ToArray() } };
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
            case "create_box":
            case "create_cylinder":
            {
                var parts = snapshot.Definitions.Values.OfType<PartDefinition>().ToArray();
                var requestedPart = OptionalGuid(args, "partId");
                if (parts.Length > 0 && requestedPart is null)
                    throw new ArgumentException("Specify an exact partId before creating geometry.");
                DefinitionId? part = null;
                if (requestedPart is { } partGuid)
                {
                    part = new DefinitionId(partGuid);
                    if (!snapshot.Definitions.TryGetValue(part.Value, out var definition) ||
                        definition is not PartDefinition)
                        throw new InvalidOperationException("Exact part ID is missing.");
                }
                var layerGuid = OptionalGuid(args, "layerId");
                var materialGuid = OptionalGuid(args, "materialId");
                var layer = layerGuid is { } lg ? new LayerId(lg) : (LayerId?)null;
                var material = materialGuid is { } mg ? new MaterialId(mg) : (MaterialId?)null;
                var x = OptionalNumber(args, "x", 0);
                var y = OptionalNumber(args, "y", 0);
                var z = OptionalNumber(args, "z", 0);
                if (Math.Max(Math.Abs(x), Math.Max(Math.Abs(y), Math.Abs(z))) > 1_000_000)
                    throw new ArgumentException("Placement is outside the supported range.");
                var height = PositiveDimension(args, "height");
                GeometryRecipe recipe = action == "create_box"
                    ? new BoxRecipe(PositiveDimension(args, "width"),
                        PositiveDimension(args, "depth"), height, RigidTransform3d.Translate(x, y, z))
                    : new CylinderRecipe(PositiveDimension(args, "radius"), height,
                        RigidTransform3d.Translate(x, y, z));
                command = new AddBodyCommand(recipe, RequiredString(args, "name"), part, layer, material);
                break;
            }
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
        if (action is "create_box" or "create_cylinder")
        {
            var bodyId = after.Bodies.Keys.Except(snapshot.Bodies.Keys).Single();
            var body = after.Bodies[bodyId];
            return JsonSerializer.Serialize(new { success = true, action, id = bodyId.ToString(),
                bodyId = bodyId.ToString(), featureId = body.Producer?.ToString(),
                partId = body.PartId.ToString(), documentId = after.Id.ToString(),
                stateId = after.StateId.ToString() });
        }
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
    private static int OptionalInt(JsonElement args, string key, int fallback, int minimum, int maximum)
    {
        if (!args.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) ||
            number < minimum || number > maximum)
            throw new ArgumentException($"Invalid {key}; expected {minimum}–{maximum}.");
        return number;
    }
    private static double OptionalNumber(JsonElement args, string key, double fallback) =>
        args.TryGetProperty(key, out _) ? RequiredNumber(args, key) : fallback;
    private static double PositiveDimension(JsonElement args, string key)
    {
        var value = RequiredNumber(args, key);
        if (value is <= 0 or > 1_000_000)
            throw new ArgumentException($"Invalid {key}; expected a positive millimeter value up to 1,000,000.");
        return value;
    }
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
