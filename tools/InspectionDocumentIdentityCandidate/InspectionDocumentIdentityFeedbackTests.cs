using System.Text.Json;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class InspectionDocumentIdentityFeedbackTests
{
    private static readonly string[] Areas = ["parts", "bodies", "features", "layers", "materials", "selection", "sketches", "occurrences", "assembly", "parameters", "parameter_bindings", "parameter_targets", "datums", "datum_associations", "timeline", "recipes", "sketch_geometry", "assembly_patterns"];
    private static readonly string[] PartAreas = ["bodies", "features", "sketches", "recipes", "sketch_geometry", "parameter_targets", "datums", "datum_associations"];
    public static IEnumerable<object[]> AllAreas() => Areas.Select(a => new object[] { a });
    public static IEnumerable<object[]> AllPartAreas() => PartAreas.Select(a => new object[] { a });
    public static IEnumerable<object[]> ScopedAreas() => PartAreas.Append("timeline").Select(a => new object[] { a });

    [Theory, MemberData(nameof(AllAreas))]
    public async Task DocumentFilterReportsItsActualObjectRoleAndReadOnlySuggestion(string area)
    {
        await using var f = new Fixture();
        using var result = await f.Query(new { area, id = f.Session.Snapshot.Id.ToString(), partId = area == "timeline" ? f.Part.ToString() : null });
        var r = result.RootElement;
        Assert.False(r.GetProperty("success").GetBoolean());
        Assert.Equal("inspection_document_id_filter", r.GetProperty("code").GetString());
        Assert.Equal("id", r.GetProperty("field").GetString());
        Assert.Contains("not documentId", r.GetProperty("message").GetString());
        var read = r.GetProperty("reinspection");
        Assert.Equal("cad_inspect", read.GetProperty("toolName").GetString());
        Assert.Equal(area, read.GetProperty("arguments").GetProperty("area").GetString());
        Assert.False(read.GetProperty("arguments").TryGetProperty("id", out _));
        Assert.False(r.TryGetProperty("items", out _));
        if (area == "timeline") Assert.Equal(f.Part.ToString(), read.GetProperty("arguments").GetProperty("partId").GetString());
    }

    [Theory, MemberData(nameof(AllAreas))]
    public async Task CallerExplicitlyReadsSuggestedQueryAndSeesOriginalState(string area)
    {
        await using var f = new Fixture();
        using var rejected = await f.Query(new { area, id = f.Session.Snapshot.Id.ToString(), partId = area == "timeline" ? f.Part.ToString() : null });
        var suggestion = rejected.RootElement.GetProperty("reinspection").GetProperty("arguments");
        using var accepted = await f.QueryJson(suggestion.GetRawText());
        Assert.True(accepted.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(f.Session.Snapshot.Id.ToString(), accepted.RootElement.GetProperty("documentId").GetString());
        Assert.Equal(f.Session.Snapshot.StateId.ToString(), accepted.RootElement.GetProperty("stateId").GetString());
        Assert.True(accepted.RootElement.GetProperty("total").GetInt32() > 0 || area == "assembly");
    }

    [Theory, MemberData(nameof(AllAreas))]
    public async Task UnknownObjectStillReturnsItsHonestEmptyPage(string area)
    {
        await using var f = new Fixture();
        using var result = await f.Query(new { area, id = Guid.NewGuid().ToString(), partId = area == "timeline" ? f.Part.ToString() : null });
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("total").GetInt32());
        if (area == "assembly") Assert.Equal(0, result.RootElement.GetProperty("instanceTotal").GetInt32());
    }

    [Theory, MemberData(nameof(AllAreas))]
    public async Task RealTypedObjectMayShareTheDocumentGuid(string area)
    {
        await using var f = new Fixture(area);
        using var result = await f.Query(new { area, id = f.Session.Snapshot.Id.ToString(), partId = area == "timeline" ? f.Part.ToString() : null });
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        var count = result.RootElement.GetProperty(area == "assembly" ? "instanceTotal" : "total").GetInt32();
        Assert.True(count > 0);
    }

    [Theory, MemberData(nameof(AllPartAreas))]
    public async Task DocumentPartFilterOffersOnlyPartDiscovery(string area)
    {
        await using var f = new Fixture();
        using var result = await f.Query(new { area, partId = f.Session.Snapshot.Id.ToString() });
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("inspection_document_part_filter", result.RootElement.GetProperty("code").GetString());
        var args = result.RootElement.GetProperty("reinspection").GetProperty("arguments");
        Assert.Equal(area == "parameter_targets" ? "parameter_targets" : "parts", args.GetProperty("area").GetString());
        Assert.False(args.TryGetProperty("id", out _)); Assert.False(args.TryGetProperty("partId", out _));
    }

    [Theory, MemberData(nameof(AllPartAreas))]
    public async Task RealPartMayShareDocumentGuidAndStillFilterOwnedObjects(string area)
    {
        await using var f = new Fixture("parts");
        using var result = await f.Query(new { area, partId = f.Part.ToString() });
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.True(result.RootElement.GetProperty("total").GetInt32() > 0);
    }

    [Theory, MemberData(nameof(AllAreas))]
    public async Task ValidTypedDocumentGuidStillAcceptsALaterEmptyPage(string area)
    {
        await using var f = new Fixture(area);
        using var result = await f.Query(new { area, id = f.Session.Snapshot.Id.ToString(), offset = 100, limit = 1, partId = area == "timeline" ? f.Part.ToString() : null });
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        var r = result.RootElement;
        var items = r.GetProperty("items");
        Assert.Empty((area == "assembly" ? items.GetProperty("instances") : items).EnumerateArray());
    }

    [Theory, MemberData(nameof(ScopedAreas))]
    public async Task RealObjectGuidOutsideTheExplicitPartStillReturnsAnEmptyPage(string area)
    {
        await using var f = new Fixture(area);
        using var result = await f.Query(new { area, id = f.Session.Snapshot.Id.ToString(), partId = f.OtherPart.ToString() });
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task RealBodySharingDocumentGuidDoesNotRequireThatBodyToBeSelected()
    {
        await using var f = new Fixture("selection", selected: false);
        using var result = await f.Query(new { area = "selection", id = f.Session.Snapshot.Id.ToString() });
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task RealTargetOwnerSharingDocumentGuidMayHaveNoWritableNumericTargets()
    {
        await using var f = new Fixture("parameter_targets", imported: true);
        using var result = await f.Query(new { area = "parameter_targets", id = f.Session.Snapshot.Id.ToString() });
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task ActualAssemblyTargetOwnerCanShareDocumentGuid()
    {
        await using var f = new Fixture("assembly-owner");
        using var result = await f.Query(new { area = "parameter_targets", partId = f.Session.Snapshot.Id.ToString() });
        Assert.True(result.RootElement.GetProperty("success").GetBoolean());
        Assert.True(result.RootElement.GetProperty("total").GetInt32() > 0);
        foreach (var row in result.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(f.Session.Snapshot.Id.ToString(), row.GetProperty("ownerDefinitionId").GetString());
            Assert.Equal("AssemblyPattern", row.GetProperty("target").GetProperty("kind").GetString());
        }
    }

    [Theory, InlineData("detach"), InlineData("close"), InlineData("closing-requested"), InlineData("unknown-field"), InlineData("duplicate-id"), InlineData("invalid-id"), InlineData("page")]
    public async Task OriginalEntryAndParsingGuardsPrecedeFeedback(string fault)
    {
        await using var f = new Fixture(); var doc = f.Session.Snapshot.Id.ToString();
        if (fault == "detach") f.Document.Detach();
        if (fault == "close") await f.Session.DisposeAsync();
        if (fault == "closing-requested") f.Document.IsClosingRequested = true;
        var json = fault switch
        {
            "unknown-field" => JsonSerializer.Serialize(new { area = "bodies", id = doc, unknown = true }),
            "duplicate-id" => "{\"area\":\"bodies\",\"id\":\"" + doc + "\",\"id\":\"" + doc + "\"}",
            "invalid-id" => "{\"area\":\"bodies\",\"id\":\"invalid\"}",
            "page" => JsonSerializer.Serialize(new { area = "bodies", id = doc, limit = 51 }),
            _ => JsonSerializer.Serialize(new { area = "bodies", id = doc })
        };
        using var result = await f.QueryJson(json);
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.False(result.RootElement.TryGetProperty("reinspection", out _));
    }

    [Fact]
    public async Task PreCancelledReadDoesNotParseOrReturnFeedback()
    {
        await using var f = new Fixture(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Tools.ExecuteAsync(new("cancel", "cad_inspect", "invalid-json"), cancel.Token));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal MemoryAssetStore Assets { get; } = new();
        private NoGeometryKernel Kernel { get; } = new();
        internal CadDocumentSession Session { get; }
        internal CadDocumentViewModel Document { get; }
        internal CadAgentToolset Tools { get; }
        internal DefinitionId Part { get; }
        internal DefinitionId OtherPart { get; } = DefinitionId.New();

        internal Fixture(string? collision = null, bool selected = true, bool imported = false)
        {
            var snapshot = DocumentSnapshot.Create("Inspection role data");
            if (collision == "assembly-owner")
            {
                var root = new DefinitionId(snapshot.Id.Value);
                snapshot = snapshot with { RootAssemblyId = root, Definitions = snapshot.Definitions.Remove(snapshot.RootAssemblyId).Add(root, new AssemblyDefinition(root, "Root", [])) };
            }
            Guid Id(params string[] names) => names.Contains(collision, StringComparer.Ordinal) ? snapshot.Id.Value : Guid.NewGuid();
            Part = new(Id("parts")); var bodyId = new BodyId(Id("bodies", "selection"));
            var featureId = new FeatureId(Id("features", "recipes", "timeline", "parameter_targets"));
            var sketchId = new SketchId(Id("sketches", "sketch_geometry"));
            var slot = new ComponentSlotId(Id("occurrences", "assembly")); var copy = ComponentSlotId.New();
            var layer = new LayerId(Id("layers")); var material = new MaterialId(Id("materials"));
            using var bytes = Assets.Stage("Managed inspection metadata; never native BRep"u8);
            var geometry = new GeometryAssetRef(bytes.Id, GeometryRevisionId.New(), BodyKind.Solid, new(Vector3d.Zero, new(2, 3, 4)), 24);
            var body = new CadBody(bodyId, Part, "Box", geometry, featureId, layer, new(), MaterialId: material);
            GeometryRecipe recipe = imported ? new ImportedRecipe(geometry) : new BoxRecipe(2, 3, 4, RigidTransform3d.Identity);
            var feature = new FeatureDefinition(featureId, Part, "Box author", recipe, [], bodyId, geometry);
            var parameter = new CadParameter(Id("parameters"), "WidthDriver", ParameterDimension.Length, "2 mm");
            var binding = new ParameterBinding(Id("parameter_bindings"), parameter.Id, ParameterTarget.Feature(featureId, "X"));
            var datum = new CadDatumPlane(Id("datums", "datum_associations"), Part, "XY datum", DatumPlaneKind.Offset, DatumPlaneSource.Base(DocumentWorkPlaneKind.XY));
            var sketch = CadSketch.Create(Part, "Sketch", RigidTransform3d.Identity) with { Id = sketchId };
            var pattern = new AssemblyRepetition(Id("assembly_patterns"), snapshot.RootAssemblyId, slot, "Array", RepetitionKind.Linear, RigidTransform3d.Identity, 2, 10, [copy]);
            snapshot = snapshot with
            {
                Bodies = snapshot.Bodies.Add(bodyId, body), Features = snapshot.Features.Add(featureId, feature),
                Layers = snapshot.Layers.Add(layer, new(layer, "Inspection layer")), Materials = snapshot.Materials.Add(material, new(material, "Material", .00000785)),
                Definitions = snapshot.Definitions.Add(Part, new PartDefinition(Part, "Part", [bodyId], [featureId])).Add(OtherPart, new PartDefinition(OtherPart, "Other part", [], [])).SetItem(snapshot.RootAssemblyId,
                    new AssemblyDefinition(snapshot.RootAssemblyId, "Root", [new(slot, Part, "Seed", RigidTransform3d.Identity), new(copy, Part, "Copy", RigidTransform3d.Translate(10, 0, 0))])),
                Parameters = new([parameter], imported ? [] : [binding]), Sketches = snapshot.Sketches.Add(sketchId, sketch),
                Datums = new(snapshot.Datums.Planes.Add(datum.Id, datum), snapshot.Datums.SketchBindings.Add(sketchId, new(sketchId, datum.Id, datum.Revision))),
                AssemblyPatterns = snapshot.AssemblyPatterns.Add(pattern.Id, pattern)
            };
            snapshot.Validate(); Session = new(snapshot, Assets, Kernel, new InlineSessionDispatcher());
            Document = new(Session, Kernel, new CadMessageLog()); Tools = new(new CadCommandLineService(), Document);
            if (selected) Document.Selection.Replace([new(new(snapshot.Id, [slot]), bodyId, geometry.Revision)]);
            Assert.Equal(12, Tools.ToolDefinitions.Count);
        }

        internal Task<JsonDocument> Query(object request) => QueryJson(JsonSerializer.Serialize(request, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }));
        internal async Task<JsonDocument> QueryJson(string json)
        {
            var before = Session.Snapshot; var display = Session.DisplaySnapshot; var generation = Session.Generation;
            var history = Session.HistoryUsage; var undo = Session.CanUndo; var redo = Session.CanRedo; var dirty = Session.IsDirty;
            int count = Assets.Count; long size = Assets.SizeBytes; var selection = Document.Selection.Items;
            var result = JsonDocument.Parse(await Tools.ExecuteAsync(new("query", "cad_inspect", json), default));
            Assert.Same(before, Session.Snapshot); Assert.Same(display, Session.DisplaySnapshot); Assert.Equal(generation, Session.Generation);
            Assert.Equal(history, Session.HistoryUsage); Assert.Equal(undo, Session.CanUndo); Assert.Equal(redo, Session.CanRedo); Assert.Equal(dirty, Session.IsDirty);
            Assert.Equal(count, Assets.Count); Assert.Equal(size, Assets.SizeBytes); Assert.Equal(selection, Document.Selection.Items);
            Assert.Equal(0, Kernel.Calls); return result;
        }
        public async ValueTask DisposeAsync() { Document.Detach(); await Session.DisposeAsync(); Assert.Equal(0, Assets.Count); Assert.Equal(0, Kernel.Calls); }
    }

    private sealed class NoGeometryKernel : IGeometryKernel
    {
        internal int Calls { get; private set; }
        public string Version => "No geometry in inspection feedback";
        public bool Supports(GeometryRecipe recipe) => false;
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe, IAssetStore assets, CancellationToken cancellationToken = default) { Calls++; throw new InvalidOperationException("Unexpected geometry evaluation"); }
        public Task<LoadedDocument> ImportAsync(string path, IAssetStore assets, CancellationToken cancellationToken = default) { Calls++; throw new NotSupportedException(); }
        public Task ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CancellationToken cancellationToken = default) { Calls++; throw new NotSupportedException(); }
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot, IAssetStore assets, string path, CadExportOptions options, CancellationToken cancellationToken = default) { Calls++; throw new NotSupportedException(); }
    }
}
