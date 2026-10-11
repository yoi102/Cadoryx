using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using MessagePack;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class SketchFreeformProjectionTests
{
    internal static Shape NativeSource(int kind)
    {
        var g=SketchFreeformGeometryTests.Geometry(kind);var poles=g.Poles.Select((p,i)=>new GpPoint(p.X,p.Y,2+.15*i)).ToArray();
        ParameterRange? range=g.Periodic&&g.FirstParameter==g.Knots[0]&&g.LastParameter==g.Knots[^1]?null:new(g.FirstParameter,g.LastParameter);
        var definition=g.Kind==SketchFreeformKind.Bezier?FreeformCurveDefinition.Bezier(poles,g.Weights,range):
            FreeformCurveDefinition.BSpline(poles,g.Knots,g.Multiplicities,g.Degree,g.Periodic,g.Weights,range);
        return FreeformAuthoring.CreateCurve(definition);
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)]
    public async Task NativeCopiedProjectionPreservesAllValuesAndExactPointsAcrossStorageAndSolidConsumers(int kind)
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            using(var source=NativeSource(kind))using(var stored=OcctGeometryBridge.StoreShape(source,assets))
                await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Native source",part));
            var selection=SketchEllipseProjectionTests.Pick(session.Snapshot,assets,kind<2?CurveGeometryType.BezierCurve:CurveGeometryType.BSplineCurve);
            var plane=new RigidTransform3d(new(1,-3,5),Quaterniond.FromAxisAngle(new Vector3d(1,2,3).Normalized(),.45));
            var evidence=await kernel.ProjectEdgeAsync(session.Snapshot,part,selection,plane,assets);
            await kernel.ConfirmEdgeProjectionAsync(evidence,assets);var geometry=Assert.IsType<SketchFreeformGeometry>(evidence.Curve.Freeform);
            using(var source=OcctGeometryBridge.ReadShape(evidence.Source,assets))using(var map=RepairSnapshot.Create(source))
            using(var edge=map.CopySubshape(map.Select(selection.FullTopologyIndex)))
            {
                var copy=FreeformAuthoring.GetCurveDefinition(edge);var range=copy.ParameterRange!.Value;
                Assert.Equal(copy.Degree,geometry.Degree);Assert.Equal(copy.Periodic,geometry.Periodic);
                Assert.True(geometry.Weights.SequenceEqual(copy.Weights));Assert.True(geometry.Knots.SequenceEqual(copy.Knots));
                Assert.True(geometry.Multiplicities.SequenceEqual(copy.Multiplicities));
                var inverse=plane.Inverse();
                foreach(double t in new[]{0d,.03,.13,.37,.78,.97,1})
                {
                    var value=edge.EvaluateEdgeDerivatives(range.First+t*(range.Last-range.First));
                    var p=inverse.Apply(new(value.Point.X,value.Point.Y,value.Point.Z));
                    var d=inverse.Rotation.Rotate(new(value.FirstDerivative.X,value.FirstDerivative.Y,value.FirstDerivative.Z))*(range.Last-range.First);
                    var actual=kernel.Evaluate(evidence.Curve,t);
                    SketchFreeformGeometryTests.Near(new(p.X,p.Y),actual.Point);SketchFreeformGeometryTests.Near(new(d.X,d.Y),actual.NormalizedDerivative);
                }
            }
            var draft=new SketchDraft(CadSketch.Create(part,"Linked",plane));var id=draft.ProjectNative(evidence);draft.SetConstruction([id],false);
            Assert.Throws<CadValidationException>(()=>draft.EditFreeform(id,geometry with{Weights=geometry.Weights.SetItem(1,geometry.Weights[1]*1.3)}));
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));var sketch=session.Snapshot.Sketches[draft.Value.Id];
            var reference=SketchProfileReference.CreateMixed(sketch,[id]);var recipe=(ExtrudeRecipe)reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,plane));
            var region=kernel.InspectRegion(recipe.Profile.BoundaryCurves.Select(ExactSketchCurve.FromBoundary).ToImmutableArray());
            await session.ExecuteAsync(new AddBodyCommand(recipe,"Consumer",part,sketchSource:reference));var consumer=session.Snapshot.Features.Values.Single(f=>f.Name=="Consumer");
            using(var shape=OcctGeometryBridge.ReadShape(consumer.Result,assets))
            {
                Assert.True(shape.IsValid);
                // Independent rational line integral; native property integration is numerical, despite exact geometry.
                double area=Area(geometry);
                Assert.InRange(Math.Abs(region.Area-area)/area,0,1e-6);
                Assert.InRange(Math.Abs(consumer.Result.VolumeMm3-area*3)/(area*3),0,1e-6);
            }
            var original=session.Snapshot;var storage=new CadDocumentStorage(edgeProjector:kernel);string path=files.PathFor("freeform.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                var restored=loaded.Snapshot.Sketches[sketch.Id];Assert.True(geometry.SameValues(restored.Freeforms.Single().Geometry));
                var output=((ExtrudeRecipe)loaded.Snapshot.Features[consumer.Id].Recipe).Profile.BoundaryCurves[0];
                Assert.True(geometry.SameValues(output.Freeform!));loaded.Snapshot.Validate();
            }
            var detached=new SketchDraft(sketch);detached.DetachProjection(id);detached.EditFreeform(id,geometry with{Reversed=true});
            Assert.Empty(detached.Value.NativeProjections);detached.Undo();detached.Undo();Assert.Same(sketch,detached.Value);
            var independent=new SketchDraft(CadSketch.Create(part,"Independent",plane));independent.ProjectNative(evidence,linked:false);
            Assert.Empty(independent.Value.NativeProjections);Assert.True(geometry.SameValues(independent.Value.Freeforms.Single().Geometry));
            await session.UndoAsync();await session.RedoAsync();Assert.Same(original,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    internal static double Area(SketchFreeformGeometry g)
    {
        double F(double t){var(p,d)=g.Evaluate(t);return(p.X*d.Y-p.Y*d.X)/2;}
        double Simpson(double a,double b)=> (b-a)/6*(F(a)+4*F((a+b)/2)+F(b));
        double Integrate(double a,double b,double whole,int depth)
        {
            double m=(a+b)/2,left=Simpson(a,m),right=Simpson(m,b);
            return depth==0||Math.Abs(left+right-whole)<1e-11?left+right+(left+right-whole)/15:
                Integrate(a,m,left,depth-1)+Integrate(m,b,right,depth-1);
        }
        var start=g.At(0);var end=g.At(1);
        var breaks=g.Knots.Select(k=>(k-g.FirstParameter)/(g.LastParameter-g.FirstParameter)).Where(t=>t>0&&t<1)
            .Select(t=>g.Reversed?1-t:t).Append(0).Append(1).Order().ToArray();double total=0;
        for(int i=1;i<breaks.Length;i++)total+=Integrate(breaks[i-1],breaks[i],Simpson(breaks[i-1],breaks[i]),20);
        return Math.Abs(total+(end.X*start.Y-start.X*end.Y)/2);
    }
    [Theory][InlineData("poles")][InlineData("weights")][InlineData("interval")][InlineData("direction")][InlineData("legacy-sketch")][InlineData("legacy-feature")]
    public async Task NativeFreeformStorageRejectsValidButForgedDefinitionsAndLegacySchemas(string change)
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            using(var source=NativeSource(2))using(var stored=OcctGeometryBridge.StoreShape(source,assets))
                await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Source",part));
            var evidence=await kernel.ProjectEdgeAsync(session.Snapshot,part,SketchEllipseProjectionTests.Pick(session.Snapshot,assets,CurveGeometryType.BSplineCurve),RigidTransform3d.Identity,assets);
            var draft=new SketchDraft(CadSketch.Create(part,"Linked",RigidTransform3d.Identity));var id=draft.ProjectNative(evidence);draft.SetConstruction([id],false);
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));var sketch=session.Snapshot.Sketches[draft.Value.Id];
            var reference=SketchProfileReference.CreateMixed(sketch,[id]);
            await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane)),"Consumer",part,sketchSource:reference));
            var storage=new CadDocumentStorage(edgeProjector:kernel);string path=files.PathFor("forged.cadoryx");await session.SaveAsync(storage,path);int count=assets.Count;
            if(change.StartsWith("legacy-"))FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind==(change=="legacy-sketch"?"sketches":"features")?
                s with{SchemaVersion=change=="legacy-sketch"?13:33}:s).ToImmutableArray()});
            else FormatEvolutionTests.RewriteSection(path,"sketches",bytes=>
            {
                var pack=MessagePackSerializer.Deserialize<PackSketches>(bytes);var s=pack.Sketches.Single();var f=s.Freeforms!.Single();var g=f.Geometry;
                g=change switch
                {
                    "poles"=>g with{Poles=g.Poles.Select((p,i)=>i==2?new[]{p[0],p[1]+2}:p).ToArray()},
                    "weights"=>g with{Weights=g.Weights.Select((w,i)=>i==2?w*1.3:w).ToArray()},
                    "interval"=>g with{FirstParameter=.2},_=>g with{Reversed=true}
                };
                return MessagePackSerializer.Serialize(pack with{Sketches=[s with{Freeforms=[f with{Geometry=g}]}]});
            });
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
}
