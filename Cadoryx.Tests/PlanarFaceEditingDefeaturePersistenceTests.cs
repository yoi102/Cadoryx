using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed partial class PlanarFaceEditingTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public async Task DefeatureSurvivingExactEdgeAndFaceContinueThroughFilletAndSavedHistory(bool rotated)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create(rotated:rotated,bores:1);
        var originEdge=f.SelectMany(shape=>
        {
            var box=shape.GetBoundingBox();return Math.Abs(box.Minimum.X)<1e-5&&Math.Abs(box.Maximum.X)<1e-5&&
                Math.Abs(box.Minimum.Z)<1e-5&&box.SizeY>11;
        },kind:ShapeKind.Edge).Single();
        var farFace=f.SelectMany(shape=>
        {
            var box=shape.GetBoundingBox();return Math.Abs(box.Minimum.X-10)<1e-5&&Math.Abs(box.Maximum.X-10)<1e-5;
        }).Single();
        var removed=f.SelectMany(shape=>shape.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder);
        await f.Apply(new DefeatureRecipe(f.Source.Result,removed));var healed=f.Edited;
        var edgeTrace=await f.Kernel.TraceExactAsync(f.Session.Snapshot,originEdge,healed.Id,f.Assets);
        Assert.Equal(HistoryResolutionStatus.Resolved,edgeTrace.Status);Assert.NotNull(edgeTrace.Target);
        ExactTopologySelection selected;
        using(var shape=OcctGeometryBridge.ReadShape(healed.Result,f.Assets))using(var map=RepairSnapshot.Create(shape))
            selected=new(f.Session.Snapshot.Id,healed.Id,healed.Result.Revision,healed.Result.AssetId,map.Fingerprint,
                edgeTrace.Target.FullTopologyIndex,HistoryShapeKind.Edge,OcctGeometryKernel.HistoryAdapterVersion);
        await f.Kernel.ConfirmAsync(f.Session.Snapshot,selected,f.Assets);
        var before=f.Session.Snapshot;
        await f.Session.ExecuteAsync(new ExactLocalFeatureCommand(selected,Cadoryx.Db.LocalFeatureOperation.Fillet,.1));
        var following=f.Session.Snapshot.Features.Values.Single(feature=>feature.Recipe is HistoryFilletRecipe);
        Assert.False(following.IsStale);Assert.Equal(healed.Id,following.Inputs.Single());
        var faceTrace=await f.Kernel.TraceExactAsync(f.Session.Snapshot,farFace,following.Id,f.Assets);
        Assert.Equal(HistoryResolutionStatus.Resolved,faceTrace.Status);Assert.Equal(2,faceTrace.PathLength);
        foreach(var face in removed)
        {
            var deleted=await f.Kernel.TraceExactAsync(f.Session.Snapshot,face,following.Id,f.Assets);
            Assert.Equal(HistoryResolutionStatus.Deleted,deleted.Status);Assert.Null(deleted.Target);
        }
        var path=files.PathFor("defeature-history.cadoryx");var storage=new CadDocumentStorage(faceEditResolver:f.Kernel);
        await f.Session.SaveAsync(storage,path);int assets=f.Assets.Count;
        using(var loaded=await storage.LoadAsync(path,f.Assets))
        {
            loaded.Snapshot.Validate();var stored=loaded.Snapshot.Features[healed.Id].TopologyHistory!;
            Assert.Equal(healed.TopologyHistory!.AdapterVersion,stored.AdapterVersion);
            Assert.Equal(healed.TopologyHistory.Entries.ToArray(),stored.Entries.ToArray());
            Assert.Equal(faceTrace,await f.Kernel.TraceExactAsync(loaded.Snapshot,farFace,following.Id,f.Assets));
        }
        Assert.Equal(assets,f.Assets.Count);
        var after=f.Session.Snapshot;await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
        await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task DefeatureNativeHistorySupportsDrawingContinuationPdfAndStep(bool rotated)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create(rotated:rotated,bores:1);
        var face=f.SelectMany(shape=>
        {var box=shape.GetBoundingBox();return Math.Abs(box.Minimum.X-10)<1e-5&&Math.Abs(box.Maximum.X-10)<1e-5;}).Single();
        var occurrence=f.Session.Snapshot.EnumerateOccurrences().Single();
        var attachment=await f.Kernel.ResolveDrawingTopologyAsync(f.Session.Snapshot,occurrence.Path,f.Source.OutputBodyId,
            face.FullTopologyIndex,face.Fingerprint,f.Assets);
        await f.Apply(new DefeatureRecipe(f.Source.Result,f.SelectMany(shape=>shape.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder)));
        var healed=f.Edited;var current=f.Session.Snapshot.EnumerateOccurrences().Single();
        var target=new DrawingSource(current.Path,healed.OutputBodyId,healed.Id,healed.Result.Revision,healed.Result.AssetId,current.WorldTransform);
        var continued=await f.Kernel.ResolveDrawingHistoryAsync(f.Session.Snapshot,attachment,target,f.Assets);
        Assert.Equal(HistoryResolutionStatus.Resolved,continued.History.Status);Assert.NotNull(continued.Current);
        Assert.Equal(DrawingAttachmentGeometry.PlaneFace,continued.Current.Geometry);
        var sheet=TechnicalDrawingSheet.A4Landscape("Defeature drawing");
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(70,90),1));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Section",DrawingViewKind.Section,new(140,90),1,
            sectionNormal:Vector3d.UnitZ,sectionOffsetMm:5));
        Assert.All(f.Session.Snapshot.DrawingSheets[sheet.Id].Views,view=>
        {Assert.Null(view.StaleReason);Assert.NotEmpty(view.Strokes);Assert.Equal(healed.Result.AssetId,view.Sources.Single().Asset);});
        var storage=new CadDocumentStorage(faceEditResolver:f.Kernel);var path=files.PathFor("defeature-drawing.cadoryx");
        await f.Session.SaveAsync(storage,path);int count=f.Assets.Count;
        using(var loaded=await storage.LoadAsync(path,f.Assets))
        {
            loaded.Snapshot.Validate();TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("defeature.pdf"));
            Assert.True(new FileInfo(files.PathFor("defeature.pdf")).Length>1000);
            Assert.Equal(continued,await f.Kernel.ResolveDrawingHistoryAsync(loaded.Snapshot,attachment,target,f.Assets));
        }
        var step=files.PathFor("defeature.step");await f.Kernel.ExportAsync(f.Session.Snapshot,f.Assets,step);
        using(var loaded=await f.Kernel.ImportAsync(step,f.Assets))
        {loaded.Snapshot.Validate();Assert.Equal(1800,loaded.Snapshot.Bodies.Values.Sum(body=>body.Geometry.VolumeMm3),4);}
        Assert.Equal(count,f.Assets.Count);
    }

    [Theory][InlineData("adapter")][InlineData("target")][InlineData("coverage")]
    public async Task DefeatureForgedFlattenedMapCannotAuthorizeHistoryContinuation(string mutation)
    {
        await using var f=await Fixture.Create(bores:1);
        var face=f.SelectMany(shape=>
        {var box=shape.GetBoundingBox();return Math.Abs(box.Minimum.X-10)<1e-5&&Math.Abs(box.Maximum.X-10)<1e-5;}).Single();
        await f.Apply(new DefeatureRecipe(f.Source.Result,f.SelectMany(shape=>shape.GetFaceSurfaceSnapshot().SurfaceType==SurfaceGeometryType.Cylinder)));
        var feature=f.Edited;var history=feature.TopologyHistory!;
        var faces=history.Entries.Where(row=>row.ResultKind==HistoryShapeKind.Face&&row.ResultIndex is not null).ToArray();
        var chosen=faces.First(row=>row.SourceIndex==face.FullTopologyIndex);
        var other=faces.First(row=>row.ResultIndex!=chosen.ResultIndex);
        history=mutation switch
        {
            "adapter"=>history with{AdapterVersion=OcctGeometryKernel.HistoryAdapterVersion},
            "coverage"=>history with{Entries=history.Entries.Where(row=>row.SourceIndex!=chosen.SourceIndex).ToImmutableArray()},
            _=>history with{Entries=history.Entries.Select(row=>row==chosen?row with{ResultIndex=other.ResultIndex}:row).ToImmutableArray()}
        };
        var corrupt=f.Session.Snapshot with{Features=f.Session.Snapshot.Features.SetItem(feature.Id,feature with{TopologyHistory=history})};
        int count=f.Assets.Count;var before=f.Session.Snapshot;
        var result=await f.Kernel.TraceExactAsync(corrupt,face,feature.Id,f.Assets);
        Assert.Equal(HistoryResolutionStatus.Unsupported,result.Status);Assert.Null(result.Target);
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
    }
}
