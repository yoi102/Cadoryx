using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels.Toolboxes;
using Cadoryx.ViewModels.Services.Platform;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public async Task TerminalPrintsInputResultsErrorsAndCommittedDocumentActions()
    {
        var log = new CadMessageLog();
        var terminal = new CommandLineToolboxViewModel(new CadCommandLineService(), new TestIcons(), log);
        terminal.CommandText = "HELP BOX";
        await terminal.ExecuteCommand.ExecuteAsync(null);
        Assert.Contains(terminal.Entries, x => x.Kind == "Input" && x.Text.Contains("HELP BOX"));
        Assert.Contains(terminal.Entries, x => x.Kind == "Output" && x.Text.Contains("BOX name"));
        terminal.CommandText = "UNKNOWN";
        await terminal.ExecuteCommand.ExecuteAsync(null);
        Assert.Contains(terminal.Entries, x => x.Kind == "Error" && x.Text.Contains("Unknown command"));
        log.Add("Editing failed", CadMessageLevel.Error, "Document");
        Assert.Contains(terminal.Entries, x => x.Kind == "Error" && x.Text == "Editing failed");

        await using var session = new CadDocumentSession(DocumentSnapshot.Create("Transcript"),
            new MemoryAssetStore(), new OcctGeometryKernel(), new InlineSessionDispatcher());
        var activities = new List<CadDocumentCommandActivity>();
        session.CommandCommitted += (_, activity) => activities.Add(activity);
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10, 20, 30, RigidTransform3d.Identity), "Box"));
        await Assert.ThrowsAnyAsync<Exception>(() => session.ExecuteAsync(new AddBodyCommand(
            new BoxRecipe(1, 1, 1, RigidTransform3d.Identity), "Rejected", DefinitionId.New())));
        Assert.Single(activities);
        await session.UndoAsync();
        await session.RedoAsync();
        Assert.Equal(["Executed", "Undo", "Redo"], activities.Select(x => x.Verb));
        Assert.All(activities, x => Assert.Equal("Transcript", x.DocumentName));
        Assert.All(activities, x => Assert.Equal(activities[0].Name, x.Name));
    }

    private sealed class TestIcons : IToolboxIconProvider
    {
        public object ModelTree => "";
        public object Properties => "";
        public object Modeling => "";
        public object Messages => "";
    }

    [Fact]
    public async Task CommandLineRejectsUnknownAndInvalidInputWithoutChangingDocument()
    {
        var service = new CadCommandLineService(); var document = new Context();
        Assert.False((await service.ExecuteAsync("NOT_A_COMMAND", document)).Success);
        Assert.False((await service.ExecuteAsync("VIEW DIAGONAL", document)).Success);
        Assert.False((await service.ExecuteAsync("UNDO EXTRA", document)).Success);
        Assert.False((await service.ExecuteAsync("STATUS", null)).Success);
        Assert.False((await service.ExecuteAsync("FIND", document)).Success);
        Assert.False((await service.ExecuteAsync("SELECT", document)).Success);
        Assert.False((await service.ExecuteAsync("TOOL SPHERE", document)).Success);
        Assert.False((await service.ExecuteAsync("HELP \"", null)).Success);
        Assert.Equal(0, document.EditCount);
        Assert.Contains("VIEW", (await service.ExecuteAsync("HELP", null)).Message);
    }

    [Fact]
    public async Task CommandLineRoutesDocumentActionsAndHonorsUndoState()
    {
        var service = new CadCommandLineService(); var document = new Context();
        Assert.Contains("body", (await service.ExecuteAsync("LIST", document)).Message);
        Assert.False((await service.ExecuteAsync("UNDO", document)).Success);
        document.CanUndo = true;
        Assert.True((await service.ExecuteAsync("u", document)).Success);
        Assert.True((await service.ExecuteAsync("VIEW TOP", document)).Success);
        Assert.True((await service.ExecuteAsync("FIT", document)).Success);
        Assert.True((await service.ExecuteAsync("DESELECT", document)).Success);
        Assert.Equal(1, document.EditCount);
        Assert.Equal("TOP", document.View);
        Assert.Equal(1, document.FitCount);
        Assert.Equal(1, document.ClearCount);
        Assert.Contains("STATUS", service.Complete("st"));
        Assert.Contains("bracket", (await service.ExecuteAsync("FIND bracket", document)).Message);
        Assert.Contains("Nothing selected", (await service.ExecuteAsync("SELECTION", document)).Message);
        Assert.False((await service.ExecuteAsync("SELECT duplicate", document)).Success);
        Assert.True((await service.ExecuteAsync("SELECT bracket", document)).Success);
        Assert.True((await service.ExecuteAsync("tool box", document)).Success);
        Assert.Equal("BOX", document.InteractiveTool);
        Assert.True((await service.ExecuteAsync("CANCEL", document)).Success);
        Assert.False((await service.ExecuteAsync("CANCEL", document)).Success);
        Assert.True((await service.ExecuteAsync("BOX \"Frame 1\" 10 20 30 1 2 3", document)).Success);
        Assert.Equal("Frame 1", document.CreatedName);
        Assert.False((await service.ExecuteAsync("CYLINDER C -1 10", document)).Success);
    }

    [Fact]
    public async Task ExpandedCommandsRouteQueriesGridDisplayAndVisibility()
    {
        var service = new CadCommandLineService(); var document = new Context();
        foreach (var name in new[] { "PARTS", "FEATURES", "LAYERS", "MATERIALS", "SKETCHES", "DRAWINGS", "OCCURRENCES" })
        {
            Assert.True((await service.ExecuteAsync(name, document)).Success);
            Assert.False((await service.ExecuteAsync(name + " extra", document)).Success);
            Assert.Contains(name, service.Commands.Select(x => x.Name));
        }
        Assert.Contains("Definitions:", (await service.ExecuteAsync("STATS", document)).Message);
        Assert.Contains("spacing: 10 mm", (await service.ExecuteAsync("GRID", document)).Message);
        Assert.True((await service.ExecuteAsync("GRID OFF", document)).Success);
        Assert.False(document.Grid.Visible);
        Assert.True((await service.ExecuteAsync("GRID SPACING 2.5", document)).Success);
        Assert.Equal(2.5, document.Grid.SpacingMm);
        Assert.True((await service.ExecuteAsync("GRID SNAP ON", document)).Success);
        Assert.True(document.Grid.Snap);
        foreach (var invalid in new[] { "GRID SPACING 0", "GRID SPACING 1001", "GRID SNAP MAYBE", "GRID ON EXTRA" })
            Assert.False((await service.ExecuteAsync(invalid, document)).Success);
        Assert.Equal("SHADED", (await service.ExecuteAsync("DISPLAY", document)).Message.Split(' ')[1]);
        Assert.True((await service.ExecuteAsync("DISPLAY WIREFRAME", document)).Success);
        Assert.Equal("WIREFRAME", document.DisplayMode);
        Assert.False((await service.ExecuteAsync("DISPLAY SOLID", document)).Success);
        Assert.True((await service.ExecuteAsync("ISOLATE", document)).Success);
        Assert.True((await service.ExecuteAsync("HIDE", document)).Success);
        Assert.True((await service.ExecuteAsync("SHOWALL", document)).Success);
        Assert.False((await service.ExecuteAsync("SHOWALL", document)).Success);
        Assert.True((await service.ExecuteAsync("FOCUS", document)).Success);
        Assert.False((await service.ExecuteAsync("FOCUS extra", document)).Success);
    }

    private sealed class Context : ICadCommandContext
    {
        public string DocumentName => "test";
        public int BodyCount => 1;
        public int SelectionCount => 1;
        public bool CanUndo { get; set; }
        public bool CanRedo => false;
        public int EditCount { get; private set; }
        public int FitCount { get; private set; }
        public int ClearCount { get; private set; }
        public CadGridState Grid { get; private set; } = new(true, 10, false);
        public string DisplayMode { get; private set; } = "SHADED";
        private bool filtered;
        public string? View { get; private set; }
        public string? CreatedName { get; private set; }
        public string? InteractiveTool { get; private set; }
        public IReadOnlyList<string> ListBodies(int limit) => ["body"];
        public IReadOnlyList<string> FindBodies(string query, int limit) => ["assembly / bracket"];
        public IReadOnlyList<string> ListSelection(int limit) => [];
        public IReadOnlyList<string> ListParts(int limit) => ["Part"];
        public IReadOnlyList<string> ListFeatures(int limit) => ["Feature"];
        public IReadOnlyList<string> ListLayers(int limit) => ["Default"];
        public IReadOnlyList<string> ListMaterials(int limit) => ["Steel"];
        public IReadOnlyList<string> ListSketches(int limit) => ["Sketch"];
        public IReadOnlyList<string> ListDrawings(int limit) => ["Sheet"];
        public IReadOnlyList<string> ListOccurrences(int limit) => ["Part instance"];
        public string ScaleSummary => "Definitions: 1 | instances: 1 | features: 1 | assets: 1 (1024 bytes)";
        public Task SetGridAsync(bool? visible = null, double? spacingMm = null, bool? snap = null)
        { Grid = Grid with { Visible = visible ?? Grid.Visible, SpacingMm = spacingMm ?? Grid.SpacingMm, Snap = snap ?? Grid.Snap }; return Task.CompletedTask; }
        public void SetDisplayMode(string mode) => DisplayMode = mode.ToUpperInvariant();
        public CadCommandResult IsolateSelection() { filtered = true; return new(true, "Selection isolated."); }
        public CadCommandResult HideSelection() { filtered = true; return new(true, "Selection hidden."); }
        public CadCommandResult ShowAll() { if (!filtered) return new(false, "Nothing hidden."); filtered = false; return new(true, "All restored."); }
        public CadCommandResult FocusSelection() => new(true, "Selection focused.");
        public CadCommandResult SelectBody(string exactName) => exactName == "bracket"
            ? new(true, "Selected: bracket") : new(false, "Cannot select: name is ambiguous.");
        public Task UndoAsync() { EditCount++; return Task.CompletedTask; }
        public Task RedoAsync() => Task.CompletedTask;
        public void Fit() => FitCount++;
        public void SetView(string direction) => View = direction;
        public void ClearSelection() => ClearCount++;
        public void StartInteractiveTool(string kind) => InteractiveTool = kind.ToUpperInvariant();
        public bool CancelInteractiveTool()
        {
            if (InteractiveTool is null) return false;
            InteractiveTool = null;
            return true;
        }
        public Task CreateBoxAsync(string name, double width, double depth, double height, double x, double y, double z)
        { CreatedName = name; EditCount++; return Task.CompletedTask; }
        public Task CreateCylinderAsync(string name, double radius, double height, double x, double y, double z)
        { CreatedName = name; EditCount++; return Task.CompletedTask; }
    }
}
