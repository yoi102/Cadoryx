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
            Assert.Equal(4, tools.ToolDefinitions.Count);
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
