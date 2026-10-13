using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed partial class PlanarFaceEditingTests
{
    [Theory][InlineData(1,false)][InlineData(2,false)][InlineData(1,true)][InlineData(2,true)]
    public async Task DefeatureHasCompleteVerifiedSourceHistoryAndExplicitDeletedFaces(int bores,bool rotated)
    {
        await using var f=await Fixture.Create(rotated:rotated,bores:bores);
        var faces=f.SelectMany(shape=>shape.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder);
        using var sourceShape=OcctGeometryBridge.ReadShape(f.Source.Result,f.Assets);
        using var source=RepairSnapshot.Create(sourceShape);
        var before=f.Session.Snapshot;int initialAssets=f.Assets.Count;
        var recipe=new DefeatureRecipe(f.Source.Result,faces);
        using(var preview=await f.Kernel.EvaluateAsync(recipe,f.Assets))
        {
            Assert.NotNull(preview.TopologyHistory);
            Assert.Equal(OcctGeometryKernel.DefeatureHistoryAdapterVersion,preview.TopologyHistory.AdapterVersion);
            Assert.Equal(1,preview.TopologyHistory.SchemaVersion);
            Assert.Equal(source.Fingerprint,preview.TopologyHistory.SourceFingerprint);
            Assert.DoesNotContain(preview.Diagnostics,row=>row.Code=="HISTORY.UNSUPPORTED");
            Assert.Contains(preview.Diagnostics,row=>row.Code=="STYLE.UNSUPPORTED");
        }
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(initialAssets,f.Assets.Count);
        await f.Apply(recipe);var feature=f.Edited;var history=feature.TopologyHistory!;
        history.ValidateFor(feature);
        Assert.Equal(source.Topology.Where(slot=>slot.Kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex)
            .Select(slot=>slot.Selection.Index).Order(),history.Entries.Select(row=>row.SourceIndex).Distinct().Order());
        foreach(var face in faces)
        {
            var rows=history.Entries.Where(row=>row.SourceIndex==face.FullTopologyIndex).ToArray();
            Assert.NotEmpty(rows);Assert.All(rows,row=>Assert.Equal(TopologyEvolution.Deleted,row.Evolution));
            var traced=await f.Kernel.TraceExactAsync(f.Session.Snapshot,face,feature.Id,f.Assets);
            Assert.Equal(HistoryResolutionStatus.Deleted,traced.Status);Assert.Null(traced.Target);
        }
        var after=f.Session.Snapshot;await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
        await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }

    [Fact]public async Task DefeatureHistoryAdapterCannotAuthorizeAnotherOperationOrComposedSchema()
    {
        await using var f=await Fixture.Create(bores:1);
        await f.Apply(new DefeatureRecipe(f.Source.Result,f.SelectMany(shape=>shape.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder)));
        var feature=f.Edited;Assert.NotNull(feature.TopologyHistory);
        Assert.Throws<CadValidationException>(()=>(feature.TopologyHistory with{SchemaVersion=3}).ValidateFor(feature));
        var removed=((DefeatureRecipe)feature.Recipe).Faces[0];
        var tampered=f.Session.Snapshot with{Features=f.Session.Snapshot.Features.SetItem(feature.Id,
            feature with{TopologyHistory=feature.TopologyHistory with{AdapterVersion=OcctGeometryKernel.HistoryAdapterVersion}})};
        var trace=await f.Kernel.TraceExactAsync(tampered,removed,feature.Id,f.Assets);
        Assert.Equal(HistoryResolutionStatus.Unsupported,trace.Status);Assert.Null(trace.Target);
    }
}
