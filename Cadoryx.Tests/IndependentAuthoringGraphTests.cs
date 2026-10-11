using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed partial class IndependentAuthoringGraphTests
{
    private static async Task<MakePartIndependentCommand> Clone(CadDocumentSession session,DefinitionId part)
    {
        var root=new OccurrencePath(session.Snapshot.Id,[]);var slot=ComponentSlotId.New();
        await session.ExecuteAsync(AssemblyOccurrenceCommands.Insert(root,part,slot,"Second",RigidTransform3d.Translate(50,0,0)));
        var command=AssemblyOccurrenceCommands.MakePartIndependent(root.Append(slot));await session.ExecuteAsync(command);return command;
    }
    private static ExactTopologySelection Face(CadDocumentSession session,FeatureDefinition feature,MemoryAssetStore assets,bool top)
    {
        using var shape=OcctGeometryBridge.ReadShape(feature.Result,assets);using var map=RepairSnapshot.Create(shape);
        var item=map.Topology.Single(t=>
        {
            if(t.Kind!=ShapeKind.Face)return false;using var face=map.CopySubshape(t.Selection);var b=face.GetBoundingBox();
            return top?Math.Abs(b.Minimum.Z-15)<1e-5&&Math.Abs(b.Maximum.Z-15)<1e-5:
                Math.Abs(b.Minimum.X)<1e-5&&Math.Abs(b.Maximum.X)<1e-5;
        });
        return new(session.Snapshot.Id,feature.Id,feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,
            item.Selection.Index,HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);
    }

    [Theory]
    [InlineData("shell")][InlineData("draft")][InlineData("hole")][InlineData("array")]
    [InlineData("revolve")][InlineData("datum-limit")][InlineData("face-limit")]
    public async Task IndependentNewRecipesRetainExactAssetsAndOwnEverySource(string kind)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Independent authoring"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Base"));
            var box=session.Snapshot.Features.Values.Single();var part=box.PartId;
            var top=Face(session,box,assets,true);var exact=await kernel.ResolveDatumGeometryAsync(session.Snapshot,top,assets);
            var plane=new CadDatumPlane(Guid.NewGuid(),part,"Exact top",DatumPlaneKind.Offset,DatumPlaneSource.Face(exact,RigidTransform3d.Identity));
            var neutral=new CadDatumPlane(Guid.NewGuid(),part,"Neutral",DatumPlaneKind.Offset,DatumPlaneSource.Base(DocumentWorkPlaneKind.XY));
            var p=new CadDatumPoint(Guid.NewGuid(),part,"P",DatumPointKind.Independent,Vector3d.Zero);
            var q=new CadDatumPoint(Guid.NewGuid(),part,"Q",DatumPointKind.Offset,Vector3d.UnitZ,p.Id);
            var axis=new CadDatumAxis(Guid.NewGuid(),part,"Axis",DatumAxisKind.TwoPoints,p.Id,q.Id);
            await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with
                {Planes=ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(plane.Id,plane).Add(neutral.Id,neutral),
                    Points=ImmutableDictionary<Guid,CadDatumPoint>.Empty.Add(p.Id,p).Add(q.Id,q),
                    Axes=ImmutableDictionary<Guid,CadDatumAxis>.Empty.Add(axis.Id,axis)}));
            plane=session.Snapshot.Datums.Planes[plane.Id];neutral=session.Snapshot.Datums.Planes[neutral.Id];axis=session.Snapshot.Datums.Axes[axis.Id];
            ICadDocumentCommand create=kind switch
            {
                "shell"=>new AddFaceFeatureCommand(box.OutputBodyId,new ShellRecipe(box.Result,[top],1),"Feature"),
                "draft"=>new AddFaceFeatureCommand(box.OutputBodyId,new DraftRecipe(box.Result,
                    [new(Guid.NewGuid(),Face(session,box,assets,false),.05,neutral.Frame,Vector3d.UnitZ,Datum:neutral.Id,DatumRevision:neutral.Revision)]),"Feature"),
                "hole"=>new AddHoleCommand(box.OutputBodyId,new HoleRecipe(box.Result,plane.Frame,new(5,6),2,Datum:plane.Id,DatumRevision:plane.Revision),"Feature"),
                "array"=>new AddRepetitionCommand(box.OutputBodyId,new RepetitionRecipe(box.Result,RepetitionKind.Linear,plane.Frame,2,20,
                    [Guid.NewGuid(),Guid.NewGuid()],Datum:plane.Id,DatumRevision:plane.Revision),"Feature"),
                "revolve"=>new AddBodyCommand(new RevolveRecipe(SketchProfile.FromCircle(new(4,0),1),Math.PI,RigidTransform3d.Identity)
                    {Axis=new(axis.Frame.Translation,axis.Frame.Rotation.Rotate(Vector3d.UnitZ),axis.Id,axis.Revision)},"Feature",part),
                _=>new AddExtrusionCommand(new(SketchProfile.FromCircle(new(5,6),1),0,RigidTransform3d.Identity,
                    kind=="face-limit"?new(Termination:ExtrusionTermination.PlanarFace,LimitFace:top,LimitGeometry:box.Result):
                        new(Termination:ExtrusionTermination.Plane,LimitPlane:plane.Frame,Datum:plane.Id,DatumRevision:plane.Revision)),"Feature",part)
            };
            await session.ExecuteAsync(create);var original=session.Snapshot.Features.Values.Single(f=>f.Name=="Feature");
            var command=await Clone(session,part);var id=command.ResultPartId!.Value;
            var copied=session.Snapshot.Features.Values.Single(f=>f.PartId==id&&f.Name=="Feature");
            var copiedBox=session.Snapshot.Features.Values.Single(f=>f.PartId==id&&f.Name=="Base");
            Assert.Equal(original.Result,copied.Result);Assert.Equal(original.Result.AssetId,copied.Result.AssetId);
            Assert.NotEqual(original.Id,copied.Id);Assert.Equal(original.Inputs.Length,copied.Inputs.Length);
            Assert.All(copied.Inputs,input=>Assert.Equal(id,session.Snapshot.Features[input].PartId));
            var copiedPlane=session.Snapshot.Datums.Planes.Values.Single(d=>d.PartId==id&&d.Name=="Exact top");
            Assert.Equal(copiedBox.Id,copiedPlane.Primary.Exact!.Selection.FeatureId);Assert.Equal(exact.Source,copiedPlane.Primary.Exact.Source);
            var copiedAxis=session.Snapshot.Datums.Axes.Values.Single(d=>d.PartId==id);
            Assert.Equal(id,session.Snapshot.Datums.Points[copiedAxis.First!.Value].PartId);
            Assert.Equal(id,session.Snapshot.Datums.Points[copiedAxis.Second!.Value].PartId);
            session.Snapshot.Validate();
            using(var evaluated=await kernel.EvaluateAsync(copied.Recipe,assets))Assert.Equal(original.Result.VolumeMm3,evaluated.Geometry.VolumeMm3,5);
            var storage=new CadDocumentStorage(datumResolver:kernel);var path=files.PathFor(kind+".cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets)){loaded.Snapshot.Validate();Assert.Equal(copied.Result,loaded.Snapshot.Features[copied.Id].Result);}
            var before=session.Snapshot;await session.UndoAsync();await session.RedoAsync();Assert.Same(before,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task GuidedSourcesUseClonedSketchesAndCanRecomputeAfterIsolation(bool loft)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var (doc,part)=SketchCommandTests.Document();
        await using(var session=new CadDocumentSession(doc,assets,kernel,new InlineSessionDispatcher()))
        {
            var solver=new ManagedSketchConstraintSolver();
            var sectionDraft=new SketchDraft(CadSketch.Create(part,"Section",RigidTransform3d.Identity));sectionDraft.AddCircle(new(0,0),1);
            await session.ExecuteAsync(new UpsertSketchCommand(sectionDraft.Value,solver));var section=session.Snapshot.Sketches[sectionDraft.Value.Id];
            var reference=SketchProfileReference.CreateCircle(section,section.Circles.Single().Id);GeometryRecipe recipe;
            if(loft)
            {
                var secondDraft=new SketchDraft(CadSketch.Create(part,"Second",RigidTransform3d.Translate(0,0,5)));secondDraft.AddCircle(new(0,0),2);
                await session.ExecuteAsync(new UpsertSketchCommand(secondDraft.Value,solver));var second=session.Snapshot.Sketches[secondDraft.Value.Id];
                recipe=new LoftRecipe([section.Plane,second.Plane],[new(Guid.NewGuid(),null,[SketchProfile.FromCircle(new(0,0),1),SketchProfile.FromCircle(new(0,0),2)])
                    {Sources=[reference,SketchProfileReference.CreateCircle(second,second.Circles.Single().Id)]}],Ruled:true);
            }
            else
            {
                var pathDraft=new SketchDraft(CadSketch.Create(part,"Path",new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0))));
                pathDraft.AddLine(new(0,0),new(0,10));await session.ExecuteAsync(new UpsertSketchCommand(pathDraft.Value,solver));var path=session.Snapshot.Sketches[pathDraft.Value.Id];
                var pathRef=new SketchPathReference(path.Id,path.Revision,[new(path.Lines.Single().Id)]);
                recipe=new SweepRecipe(new(SketchProfile.FromCircle(new(0,0),1),section.Plane){Source=reference},pathRef.Resolve(session.Snapshot,part));
            }
            await session.ExecuteAsync(new AddBodyCommand(recipe,"Guided",part));var original=session.Snapshot.Features.Values.Single();
            var command=await Clone(session,part);var cloneId=command.ResultPartId!.Value;var feature=session.Snapshot.Features.Values.Single(f=>f.PartId==cloneId);
            Assert.All(GuidedAuthoringSources.Sketches(feature),s=>Assert.Equal(cloneId,session.Snapshot.Sketches[s].PartId));
            var clonedSection=session.Snapshot.Sketches.Values.Single(s=>s.PartId==cloneId&&s.Name=="Section");
            var edit=new SketchDraft(clonedSection);edit.Change(s=>s with{Circles=s.Circles.Select(c=>c with{Radius=1.5}).ToImmutableArray(),
                Constraints=s.Constraints.Select(c=>c is RadiusConstraint r?r with{Radius=1.5}:c).ToImmutableArray()});
            await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver));Assert.Equal(original.Result,session.Snapshot.Features[original.Id].Result);
            Assert.NotEqual(feature.Result.AssetId,session.Snapshot.Features[feature.Id].Result.AssetId);session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task DocumentGlobalDriversAreCopiedAndExplicitDetachAllowsIndependentDatumRecompute()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var(doc,part)=SketchCommandTests.Document();
        await using(var session=new CadDocumentSession(doc,assets,kernel,new InlineSessionDispatcher()))
        {
            var plane=new CadDatumPlane(Guid.NewGuid(),part,"Plane",DatumPlaneKind.Offset,DatumPlaneSource.Base(DocumentWorkPlaneKind.XY),Value:2);
            await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=DatumBook.Empty.Planes.Add(plane.Id,plane)}));
            var draft=new SketchDraft(CadSketch.Create(part,"Rectangle",session.Snapshot.Datums.Planes[plane.Id].Frame));draft.AddRectangle(new(0,0),new(4,3));
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver(),datum:plane.Id));
            var sketch=session.Snapshot.Sketches[draft.Value.Id];var profile=SketchProfileReference.Create(sketch,sketch.Lines.Select(l=>l.Id));
            await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),2,sketch.Plane)),"Extrude",part,sketchSource:profile));
            var original=session.Snapshot.Features.Values.Single();var driver=new CadParameter(Guid.NewGuid(),"clearance",ParameterDimension.Length,"2 mm");
            await session.ExecuteAsync(new SetParametersCommand(new([driver],[new(Guid.NewGuid(),driver.Id,ParameterTarget.Datum(plane.Id,"Offset"))])));
            var command=await Clone(session,part);var id=command.ResultPartId!.Value;var copied=session.Snapshot.Features.Values.Single(f=>f.PartId==id);
            var copiedSketch=session.Snapshot.Sketches.Values.Single(s=>s.PartId==id);var copiedDatum=session.Snapshot.Datums.Planes.Values.Single(p=>p.PartId==id);
            Assert.Equal(copiedSketch.Id,copied.SketchSource!.SketchId);Assert.Equal(copiedDatum.Id,session.Snapshot.Datums.SketchBindings[copiedSketch.Id].Datum);
            Assert.Equal(2,session.Snapshot.Parameters.Bindings.Length);
            await session.ExecuteAsync(new SetParametersCommand(session.Snapshot.Parameters with{Parameters=[driver with{Expression="5 mm"}]}));
            Assert.Equal(5,session.Snapshot.Sketches[sketch.Id].Plane.Translation.Z);Assert.Equal(5,session.Snapshot.Sketches[copiedSketch.Id].Plane.Translation.Z);
            await session.ExecuteAsync(new SetParametersCommand(session.Snapshot.Parameters with
                {Bindings=session.Snapshot.Parameters.Bindings.Where(b=>b.Target.Owner!=copiedDatum.Id).ToImmutableArray()}));
            var originalResult=session.Snapshot.Features[original.Id].Result;
            await session.ExecuteAsync(new SetDatumsCommand(session.Snapshot.Datums with
                {Planes=session.Snapshot.Datums.Planes.SetItem(copiedDatum.Id,session.Snapshot.Datums.Planes[copiedDatum.Id] with{Value=7})}));
            Assert.Equal(7,session.Snapshot.Sketches[copiedSketch.Id].Plane.Translation.Z);Assert.Equal(5,session.Snapshot.Sketches[sketch.Id].Plane.Translation.Z);
            Assert.Equal(originalResult,session.Snapshot.Features[original.Id].Result);session.Snapshot.Validate();
        }
        Assert.Equal(0,assets.Count);
    }
}
