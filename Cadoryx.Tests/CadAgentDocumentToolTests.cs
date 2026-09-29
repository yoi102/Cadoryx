using System.Text.Json;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class CadAgentDocumentToolTests
{
    [Fact]
    public async Task ExactSelectionTargetsOneOfTwoInstancesWithTheSameBodyName()
    {
        var kernel = new OcctGeometryKernel();
        await using var session = new CadDocumentSession(DocumentSnapshot.Create("Shared"),
            new MemoryAssetStore(), kernel, new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10, 10, 10,
            RigidTransform3d.Identity), "Bracket"));
        var body = Assert.Single(session.Snapshot.Bodies.Values);
        var root = new OccurrencePath(session.Snapshot.Id, []);
        var otherSlot = ComponentSlotId.New();
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root, body.PartId,
            otherSlot, "Part 1", RigidTransform3d.Translate(20, 0, 0)));
        var document = new CadDocumentViewModel(session, kernel, new CadMessageLog());
        try
        {
            var tools = new CadAgentToolset(new CadCommandLineService(), document);
            var snapshot = session.Snapshot;
            var chosenPath = root.Append(otherSlot);
            Assert.False(Succeeded(await Call(tools, "cad_command", new { command = "SELECT Bracket" })));
            var request = new { documentId = snapshot.Id.ToString(),
                stateId = snapshot.StateId.ToString(), path = chosenPath.ToString(),
                bodyId = body.Id.ToString(), revision = body.Geometry.Revision.ToString() };
            Assert.True(Succeeded(await Call(tools, "cad_select", request)));
            Assert.Equal(chosenPath, Assert.Single(document.Selection.Items).Path);
            using var inspected = JsonDocument.Parse(await Call(tools, "cad_inspect", new { area = "selection" }));
            Assert.Equal(chosenPath.ToString(), inspected.RootElement.GetProperty("items")[0]
                .GetProperty("path").GetString());

            Assert.False(Succeeded(await Call(tools, "cad_select", new
            {
                documentId = Guid.NewGuid().ToString("D"), request.stateId,
                request.path, request.bodyId, request.revision
            })));
            Assert.False(Succeeded(await Call(tools, "cad_select", new
            {
                request.documentId, request.stateId, request.path,
                bodyId = Guid.NewGuid().ToString("D"), request.revision
            })));
            Assert.False(Succeeded(await Call(tools, "cad_select", new
            {
                request.documentId, request.stateId, request.path, request.bodyId,
                revision = Guid.NewGuid().ToString("D")
            })));
            Assert.Equal(chosenPath, Assert.Single(document.Selection.Items).Path);

            await session.ExecuteAsync(DocumentEdits.SetGrid(snapshot.Settings.Grid with { SpacingMm = 20 }));
            Assert.False(Succeeded(await Call(tools, "cad_select", request)));
            var current = session.Snapshot;
            Assert.True(Succeeded(await Call(tools, "cad_select", new
            {
                documentId = current.Id.ToString(), stateId = current.StateId.ToString(),
                path = chosenPath.ToString()
            })));
            Assert.Empty(document.Selection.Items);
            Assert.Equal(chosenPath, document.Selection.Occurrence);
        }
        finally { document.Detach(); }
    }

    [Fact]
    public async Task AgentInspectionPagesExactStructureAndCurrentSelection()
    {
        var kernel = new OcctGeometryKernel();
        await using var session = new CadDocumentSession(DocumentSnapshot.Create("Structure"),
            new MemoryAssetStore(), kernel, new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10, 12, 14,
            RigidTransform3d.Identity), "Alpha"));
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(5, 6, 7,
            RigidTransform3d.Identity), "Beta"));
        var document = new CadDocumentViewModel(session, kernel, new CadMessageLog());
        try
        {
            var tools = new CadAgentToolset(new CadCommandLineService(), document);
            var inspectAreas = tools.ToolDefinitions.Single(x => x.Name == "cad_inspect")
                .Parameters.GetProperty("properties").GetProperty("area")
                .GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray();
            Assert.Contains("bodies", inspectAreas);
            Assert.Contains("selection", inspectAreas);
            Assert.Contains("materials", inspectAreas);
            var editActions = tools.ToolDefinitions.Single(x => x.Name == "cad_edit")
                .Parameters.GetProperty("properties").GetProperty("action")
                .GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray();
            Assert.Contains("create_box", editActions);
            Assert.Contains("create_cylinder", editActions);
            var snapshot = session.Snapshot;
            using var status = JsonDocument.Parse(await Call(tools, "cad_status", new { }));
            Assert.Equal(snapshot.Id.ToString(), status.RootElement.GetProperty("documentId").GetString());
            Assert.Equal(snapshot.StateId.ToString(), status.RootElement.GetProperty("stateId").GetString());
            Assert.Equal(2, status.RootElement.GetProperty("bodies").GetInt32());
            using var summary = JsonDocument.Parse(await Call(tools, "cad_inspect", new { area = "document" }));
            Assert.Equal("Millimeter", summary.RootElement.GetProperty("items")[0]
                .GetProperty("displayUnit").GetString());

            using var first = JsonDocument.Parse(await Call(tools, "cad_inspect",
                new { area = "bodies", offset = 0, limit = 1 }));
            Assert.Equal(2, first.RootElement.GetProperty("total").GetInt32());
            Assert.True(first.RootElement.GetProperty("hasMore").GetBoolean());
            Assert.Equal("Alpha", first.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
            using var second = JsonDocument.Parse(await Call(tools, "cad_inspect",
                new { area = "bodies", offset = 1, limit = 1 }));
            Assert.False(second.RootElement.GetProperty("hasMore").GetBoolean());
            Assert.Equal("Beta", second.RootElement.GetProperty("items")[0].GetProperty("name").GetString());
            var part = Assert.Single(snapshot.Definitions.Values.OfType<PartDefinition>());
            using var parts = JsonDocument.Parse(await Call(tools, "cad_inspect", new { area = "parts" }));
            Assert.Equal(part.Id.ToString(), parts.RootElement.GetProperty("items")[0]
                .GetProperty("id").GetString());
            using var features = JsonDocument.Parse(await Call(tools, "cad_inspect",
                new { area = "features", partId = part.Id.ToString() }));
            Assert.Equal(2, features.RootElement.GetProperty("total").GetInt32());
            using var layers = JsonDocument.Parse(await Call(tools, "cad_inspect", new { area = "layers" }));
            Assert.Single(layers.RootElement.GetProperty("items").EnumerateArray());
            using var materials = JsonDocument.Parse(await Call(tools, "cad_inspect", new { area = "materials" }));
            Assert.Empty(materials.RootElement.GetProperty("items").EnumerateArray());
            using var occurrences = JsonDocument.Parse(await Call(tools, "cad_inspect",
                new { area = "occurrences" }));
            Assert.Single(occurrences.RootElement.GetProperty("items").EnumerateArray());
            using var oneBody = JsonDocument.Parse(await Call(tools, "cad_inspect", new
            {
                area = "bodies", id = snapshot.Bodies.Values.Single(x => x.Name == "Beta").Id.ToString()
            }));
            Assert.Single(oneBody.RootElement.GetProperty("items").EnumerateArray());

            var body = snapshot.Bodies.Values.Single(x => x.Name == "Beta");
            var path = snapshot.EnumerateOccurrences().Single(x => x.DefinitionId == part.Id).Path;
            document.Selection.Replace([new SelectionTarget(path, body.Id, body.Geometry.Revision)]);
            using var selection = JsonDocument.Parse(await Call(tools, "cad_inspect", new { area = "selection" }));
            Assert.Equal(body.Id.ToString(), selection.RootElement.GetProperty("items")[0]
                .GetProperty("bodyId").GetString());
            Assert.True(selection.RootElement.GetProperty("items")[0].GetProperty("current").GetBoolean());
            Assert.False(Succeeded(await Call(tools, "cad_inspect", new { area = "bodies", limit = 0 })));
            Assert.False(Succeeded(await Call(tools, "cad_inspect", new { area = "bodies", offset = -1 })));
        }
        finally { document.Detach(); }
    }

    [Fact]
    public async Task AgentPrimitiveCreationRequiresExactPartAndFreshStateAndSupportsUndo()
    {
        var kernel = new OcctGeometryKernel();
        await using var session = new CadDocumentSession(DocumentSnapshot.Create("Primitives"),
            new MemoryAssetStore(), kernel, new InlineSessionDispatcher());
        var document = new CadDocumentViewModel(session, kernel, new CadMessageLog());
        try
        {
            var tools = new CadAgentToolset(new CadCommandLineService(), document);
            var before = session.Snapshot;
            using var boxResult = JsonDocument.Parse(await Call(tools, "cad_edit", new
            {
                action = "create_box", documentId = before.Id.ToString(),
                stateId = before.StateId.ToString(), name = "Block",
                width = 10, depth = 20, height = 30, x = 1, y = 2, z = 3
            }));
            Assert.True(boxResult.RootElement.GetProperty("success").GetBoolean());
            var partId = boxResult.RootElement.GetProperty("partId").GetString()!;
            var bodyId = boxResult.RootElement.GetProperty("bodyId").GetString()!;
            Assert.Contains(session.Snapshot.Bodies.Values, body => body.Id.ToString() == bodyId);
            var current = session.Snapshot;
            Assert.False(Succeeded(await Call(tools, "cad_edit", new
            {
                action = "create_cylinder", documentId = current.Id.ToString(),
                stateId = current.StateId.ToString(), name = "No target", radius = 4, height = 8
            })));
            Assert.False(Succeeded(await Call(tools, "cad_edit", new
            {
                action = "create_cylinder", documentId = current.Id.ToString(),
                stateId = before.StateId.ToString(), partId, name = "Stale", radius = 4, height = 8
            })));
            Assert.False(Succeeded(await Call(tools, "cad_edit", new
            {
                action = "create_cylinder", documentId = current.Id.ToString(),
                stateId = current.StateId.ToString(), partId = Guid.NewGuid().ToString("D"),
                name = "Foreign", radius = 4, height = 8
            })));
            Assert.False(Succeeded(await Call(tools, "cad_edit", new
            {
                action = "create_cylinder", documentId = current.Id.ToString(),
                stateId = current.StateId.ToString(), partId, name = "Bad size", radius = -1, height = 8
            })));
            Assert.Equal(current.StateId, session.Snapshot.StateId);
            var cylinder = await Call(tools, "cad_edit", new
            {
                action = "create_cylinder", documentId = current.Id.ToString(),
                stateId = current.StateId.ToString(), partId, name = "Pin", radius = 4, height = 8
            });
            Assert.True(Succeeded(cylinder), cylinder);
            Assert.Equal(2, session.Snapshot.Bodies.Count);
            await session.UndoAsync();
            Assert.Single(session.Snapshot.Bodies);
            await session.UndoAsync();
            Assert.Empty(session.Snapshot.Bodies);
        }
        finally { document.Detach(); }
    }

    [Fact]
    public async Task StructuredToolsRequireExactStateAndPreserveUndoForSketchAndDrawing()
    {
        var (initial, part) = SketchCommandTests.Document();
        var kernel = new OcctGeometryKernel();
        await using var session = new CadDocumentSession(initial, new MemoryAssetStore(), kernel,
            new InlineSessionDispatcher());
        var sketch = SketchTestData.Rectangle(part);
        await session.ExecuteAsync(new UpsertSketchCommand(sketch, new ManagedSketchConstraintSolver()));
        var document = new CadDocumentViewModel(session, kernel, new CadMessageLog());
        try
        {
            var tools = new CadAgentToolset(new CadCommandLineService(), document);
            Assert.Equal(5, tools.ToolDefinitions.Count);
            var before = session.Snapshot;
            var sketchInfo = await Call(tools, "cad_inspect", new { area = "sketches" });
            Assert.Contains(sketch.Id.ToString(), sketchInfo);
            Assert.Contains(before.StateId.ToString(), sketchInfo);

            var edit = new { action = "rename_sketch", documentId = before.Id.ToString(),
                stateId = before.StateId.ToString(), id = sketch.Id.ToString(),
                revision = before.Sketches[sketch.Id].Revision.ToString("D"), name = "Renamed" };
            Assert.True(Succeeded(await Call(tools, "cad_edit", edit)));
            Assert.Equal("Renamed", session.Snapshot.Sketches[sketch.Id].Name);
            Assert.False(Succeeded(await Call(tools, "cad_edit", edit)));
            Assert.Equal("Renamed", session.Snapshot.Sketches[sketch.Id].Name);

            var renamed = session.Snapshot;
            var rectangleResult = await Call(tools, "cad_edit", new
            {
                action = "create_sketch_rectangle", documentId = renamed.Id.ToString(),
                stateId = renamed.StateId.ToString(), partId = part.ToString(), name = "Plate",
                x = 5, y = 6, width = 40, height = 20
            });
            Assert.True(Succeeded(rectangleResult), rectangleResult);
            Assert.Equal(2, session.Snapshot.Sketches.Count);
            renamed = session.Snapshot;
            Assert.True(Succeeded(await Call(tools, "cad_edit", new
            {
                action = "create_drawing_sheet", documentId = renamed.Id.ToString(),
                stateId = renamed.StateId.ToString(), name = "A4"
            })));
            Assert.Single(session.Snapshot.DrawingSheets);
            var drawings = await Call(tools, "cad_inspect", new { area = "drawings" });
            Assert.Contains("A4", drawings);
            await session.UndoAsync();
            Assert.Empty(session.Snapshot.DrawingSheets);
            await session.UndoAsync();
            Assert.Single(session.Snapshot.Sketches);
            await session.UndoAsync();
            Assert.Equal(before, session.Snapshot);
        }
        finally { document.Detach(); }
    }

    [Fact]
    public async Task AgentCanCreateFixedRelationFromExactInspectedPath()
    {
        var (initial, _) = SketchCommandTests.Document();
        var kernel = new OcctGeometryKernel();
        await using var session = new CadDocumentSession(initial, new MemoryAssetStore(), kernel,
            new InlineSessionDispatcher());
        var document = new CadDocumentViewModel(session, kernel, new CadMessageLog());
        try
        {
            var tools = new CadAgentToolset(new CadCommandLineService(), document);
            var path = initial.EnumerateOccurrences().Single().Path;
            var result = await Call(tools, "cad_edit", new
            {
                action = "fix_assembly_instance", documentId = initial.Id.ToString(),
                stateId = initial.StateId.ToString(), path = path.ToString(), name = "Ground"
            });
            Assert.True(Succeeded(result), result);
            Assert.Single(session.Snapshot.AssemblyConstraints);
            Assert.False(Succeeded(await Call(tools, "cad_edit", new
            {
                action = "fix_assembly_instance", documentId = initial.Id.ToString(),
                stateId = session.Snapshot.StateId.ToString(), path = Guid.NewGuid().ToString("D") + "/" +
                    path.Slots[0], name = "Foreign"
            })));
            await session.UndoAsync();
            Assert.Empty(session.Snapshot.AssemblyConstraints);
        }
        finally { document.Detach(); }
    }

    [Fact]
    public async Task AgentDrawingViewUsesExplicitSheetAndInstancePath()
    {
        var kernel = new OcctGeometryKernel();
        await using var session = new CadDocumentSession(DocumentSnapshot.Create("Drawing"),
            new MemoryAssetStore(), kernel, new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10, 20, 30, RigidTransform3d.Identity), "Box"));
        var document = new CadDocumentViewModel(session, kernel, new CadMessageLog());
        try
        {
            var tools = new CadAgentToolset(new CadCommandLineService(), document);
            var before = session.Snapshot;
            Assert.True(Succeeded(await Call(tools, "cad_edit", new
            {
                action = "create_drawing_sheet", documentId = before.Id.ToString(),
                stateId = before.StateId.ToString(), name = "Sheet"
            })));
            var current = session.Snapshot;
            var sheet = Assert.Single(current.DrawingSheets.Values);
            var path = current.EnumerateOccurrences().Single(x => x.DefinitionId ==
                current.Bodies.Values.Single().PartId).Path;
            var result = await Call(tools, "cad_edit", new
            {
                action = "add_drawing_view", documentId = current.Id.ToString(),
                stateId = current.StateId.ToString(), sheetId = sheet.Id.ToString("D"),
                path = path.ToString(), kind = "Front", name = "Front", scale = 1,
                centerX = 100, centerY = 90
            });
            Assert.True(Succeeded(result), result);
            Assert.NotEmpty(Assert.Single(session.Snapshot.DrawingSheets[sheet.Id].Views).Strokes);
            await session.UndoAsync();
            Assert.Empty(session.Snapshot.DrawingSheets[sheet.Id].Views);
        }
        finally { document.Detach(); }
    }

    [Fact]
    public async Task AgentDrawingItemsPageBeyondSummaryAndRequireExactSheet()
    {
        var kernel = new OcctGeometryKernel();
        await using var session = new CadDocumentSession(DocumentSnapshot.Create("Drawing items"),
            new MemoryAssetStore(), kernel, new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10, 20, 30,
            RigidTransform3d.Identity), "Box"));
        var snapshot = session.Snapshot;
        var body = Assert.Single(snapshot.Bodies.Values);
        var path = snapshot.EnumerateOccurrences().Single(x => x.DefinitionId == body.PartId).Path;
        var viewId = Guid.NewGuid();
        var source = new DrawingSource(path, body.Id, body.Producer,
            body.Geometry.Revision, body.Geometry.AssetId, RigidTransform3d.Identity);
        var view = new TechnicalDrawingView(viewId, "Front", DrawingViewKind.Front, null,
            new Point2d(100, 90), 1, [source], [], "Projection pending");
        var datum = new AssemblyDatumReference(path, body.PartId, body.Id, body.Producer,
            body.Geometry.Revision, body.Geometry.AssetId, new string('A', 64), 1,
            AssemblyDatumGeometry.PlaneFace, new Vector3d(0, 0, 0),
            new Vector3d(0, 0, 1), 0);
        var dimensions = Enumerable.Range(0, 51).Select(index =>
            new TechnicalDrawingDimension(Guid.NewGuid(), viewId, DrawingMeasureKind.Length,
                datum, datum, new Point2d(index, 40), index + 1, StaleReason: "Needs refresh")
            { UpperTolerance = .2, LowerTolerance = .1 }).ToArray();
        var sheet = TechnicalDrawingSheet.A4Landscape("Sheet") with
        { Views = [view], Dimensions = [.. dimensions] };
        await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        var document = new CadDocumentViewModel(session, kernel, new CadMessageLog());
        try
        {
            var tools = new CadAgentToolset(new CadCommandLineService(), document);
            var inspectSchema = tools.ToolDefinitions.Single(x => x.Name == "cad_inspect")
                .Parameters.GetProperty("properties");
            var areas = inspectSchema.GetProperty("area").GetProperty("enum")
                .EnumerateArray().Select(x => x.GetString()).ToArray();
            Assert.Contains("drawing_views", areas);
            Assert.Contains("drawing_dimensions", areas);
            Assert.True(inspectSchema.TryGetProperty("sheetId", out _));

            using var summary = JsonDocument.Parse(await Call(tools, "cad_inspect",
                new { area = "drawings", id = sheet.Id.ToString("D") }));
            Assert.Equal(51, summary.RootElement.GetProperty("items")[0]
                .GetProperty("dimensionTotal").GetInt32());
            Assert.Equal(50, summary.RootElement.GetProperty("items")[0]
                .GetProperty("dimensions").GetArrayLength());
            using var views = JsonDocument.Parse(await Call(tools, "cad_inspect",
                new { area = "drawing_views", sheetId = sheet.Id.ToString("D"), id = viewId.ToString("D") }));
            Assert.Equal(1, views.RootElement.GetProperty("total").GetInt32());
            var viewItem = views.RootElement.GetProperty("items")[0];
            Assert.True(viewItem.TryGetProperty("staleReason", out _));
            Assert.Equal(path.ToString(), viewItem.GetProperty("sources")[0]
                .GetProperty("path").GetString());
            Assert.False(viewItem.TryGetProperty("strokes", out _));

            using var page = JsonDocument.Parse(await Call(tools, "cad_inspect", new
            { area = "drawing_dimensions", sheetId = sheet.Id.ToString("D"), offset = 50, limit = 1 }));
            Assert.Equal(51, page.RootElement.GetProperty("total").GetInt32());
            Assert.False(page.RootElement.GetProperty("hasMore").GetBoolean());
            Assert.Equal(session.Snapshot.StateId.ToString(),
                page.RootElement.GetProperty("stateId").GetString());
            var item = page.RootElement.GetProperty("items")[0];
            Assert.True(item.TryGetProperty("staleReason", out _));
            Assert.Equal(.2, item.GetProperty("upperTolerance").GetDouble());
            Assert.Equal(body.Id.ToString(), item.GetProperty("first")
                .GetProperty("bodyId").GetString());
            using var exact = JsonDocument.Parse(await Call(tools, "cad_inspect", new
            { area = "drawing_dimensions", sheetId = sheet.Id.ToString("D"),
                id = item.GetProperty("id").GetString() }));
            Assert.Equal(1, exact.RootElement.GetProperty("total").GetInt32());
            Assert.False(Succeeded(await Call(tools, "cad_inspect",
                new { area = "drawing_dimensions" })));
            Assert.False(Succeeded(await Call(tools, "cad_inspect", new
            { area = "drawing_views", sheetId = Guid.NewGuid().ToString("D") })));
        }
        finally { document.Detach(); }
    }

    [Fact]
    public async Task AssemblyEditsUseIdentityAndRejectForeignOrStaleState()
    {
        var (initial, _) = SketchCommandTests.Document();
        var kernel = new OcctGeometryKernel();
        await using var session = new CadDocumentSession(initial, new MemoryAssetStore(), kernel,
            new InlineSessionDispatcher());
        var relation = AssemblyConstraintId.New();
        await session.ExecuteAsync(AssemblyConstraintCommands.AddFixed(relation, "Ground",
            session.Snapshot.EnumerateOccurrences().Single().Path));
        var document = new CadDocumentViewModel(session, kernel, new CadMessageLog());
        try
        {
            var tools = new CadAgentToolset(new CadCommandLineService(), document);
            var before = session.Snapshot;
            var inspected = await Call(tools, "cad_inspect", new { area = "assembly" });
            Assert.Contains(relation.ToString(), inspected);
            Assert.Contains("Satisfied", inspected);
            var edit = new { action = "set_assembly_constraint_enabled",
                documentId = before.Id.ToString(), stateId = before.StateId.ToString(),
                id = relation.ToString(), enabled = false };
            Assert.True(Succeeded(await Call(tools, "cad_edit", edit)));
            Assert.False(session.Snapshot.AssemblyConstraints[relation].IsEnabled);
            Assert.False(Succeeded(await Call(tools, "cad_edit", edit)));
            Assert.False(Succeeded(await Call(tools, "cad_edit", new
            {
                action = edit.action, documentId = Guid.NewGuid().ToString("D"),
                stateId = session.Snapshot.StateId.ToString(), id = relation.ToString(), enabled = true
            })));
            await session.UndoAsync();
            Assert.True(session.Snapshot.AssemblyConstraints[relation].IsEnabled);
        }
        finally { document.Detach(); }
    }

    private static Task<string> Call(CadAgentToolset tools, string name, object args) =>
        tools.ExecuteAsync(new AiToolCall(Guid.NewGuid().ToString("N"), name,
            JsonSerializer.Serialize(args)), default);

    private static bool Succeeded(string json) => JsonDocument.Parse(json).RootElement
        .GetProperty("success").GetBoolean();
}
