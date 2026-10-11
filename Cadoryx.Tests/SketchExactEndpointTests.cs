using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchExactEndpointTests
{
    internal static SketchFreeformGeometry Geometry(int kind)=>kind==1?
        new(SketchFreeformKind.Bezier,[new(10,0),new(15,5),new(20,0)],[1,.7,1],[],[],2,false,0,1):
        new(SketchFreeformKind.BSpline,[new(10,0),new(12,4),new(14,6),new(16,3),new(18,0)],
            [1,.8,1.2,.7,1],[0,.45,1],[4,1,4],3,false,.1,.9);
    internal static (SketchDraft Draft,SketchEntityId Curve,ImmutableArray<SketchEntityId> Boundary) Loop(DefinitionId part,int kind,bool polygon)
    {
        var d=new SketchDraft(CadSketch.Create(part,"Explicit finite boundary",RigidTransform3d.Identity));
        SketchEntityId id;
        if(kind==0){d.AddEllipse(new(new(20,0),new(5,0),new(0,2),0,Math.PI));id=d.Value.Ellipses[0].Id;}
        else{d.AddFreeform(Geometry(kind));id=d.Value.Freeforms[0].Id;}
        d.ExposeCurveEndpoints(id);Close(d,id,polygon);
        return(d,id,[id,..d.Value.Lines.Select(l=>l.Id)]);
    }
    internal static void Close(SketchDraft d,SketchEntityId id,bool polygon)
    {
        var ends=SketchFixedCurveEndpoints.All(d.Value).Single(e=>e.Curve==id).Ends;
        var(a,b)=SketchFixedCurveEndpoints.Positions(d.Value,id);
        if(polygon)
        {
            var c=new Point2d(b.X,-3);var e=new Point2d(a.X,-3);
            d.AddLine(b,c,start:ends.End);var first=d.Value.Lines[^1];
            d.AddLine(c,e,start:first.End);var second=d.Value.Lines[^1];
            d.AddLine(e,a,start:second.End,end:ends.Start);
        }
        else d.AddLine(b,a,start:ends.End,end:ends.Start);
    }

    [Theory][InlineData(0,false)][InlineData(0,true)][InlineData(1,false)][InlineData(1,true)][InlineData(2,false)][InlineData(2,true)]
    public async Task FiniteMixedLoopsKeepExactDefinitionsThroughSolidStorageAndUndo(int kind,bool polygon)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var(d,id,ids)=Loop(part,kind,polygon);var authored=d.Value;
            Assert.True(Assert.Single(SketchMixedLoops.Find(authored)).ToHashSet().SetEquals(ids));
            var profile=SketchProfileBuilder.Mixed(authored,ids);var region=kernel.InspectRegion(profile.BoundaryCurves.Select(ExactSketchCurve.FromBoundary).ToImmutableArray());
            Assert.True(region.Area>0);
            if(kind==0)Assert.InRange(Math.Abs(region.Area-(5*Math.PI+(polygon?30:0))),0,1e-7);
            else Assert.True(Geometry(kind).SameValues(profile.BoundaryCurves[0].Freeform!));
            var reversed=SketchProfileBuilder.Mixed(authored,ids.Reverse());
            Assert.InRange(Math.Abs(region.Area-kernel.InspectRegion(reversed.BoundaryCurves.Select(ExactSketchCurve.FromBoundary).ToImmutableArray()).Area),0,1e-7);
            await session.ExecuteAsync(new UpsertSketchCommand(authored,new ManagedSketchConstraintSolver()));var sketch=session.Snapshot.Sketches[authored.Id];
            var reference=SketchProfileReference.CreateMixed(sketch,ids);
            await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane)),"Finite mixed solid",part,sketchSource:reference));
            var feature=Assert.Single(session.Snapshot.Features.Values);
            Assert.InRange(Math.Abs(feature.Result.VolumeMm3-region.Area*3)/(region.Area*3),0,1e-6);
            var before=session.Snapshot;string path=files.PathFor("explicit-ends.cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                var restored=loaded.Snapshot.Sketches[sketch.Id];
                Assert.Equal(SketchFixedCurveEndpoints.All(sketch).Single().Ends,SketchFixedCurveEndpoints.All(restored).Single().Ends);
                Assert.True(SketchMixedProfile.SameLoop(SketchProfileBuilder.Mixed(sketch,ids).BoundaryCurves,SketchProfileBuilder.Mixed(restored,ids).BoundaryCurves));loaded.Snapshot.Validate();
            }
            await session.UndoAsync();await session.RedoAsync();Assert.Same(before,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public void NumericEditsMoveSharedAuthorEndpointsButPointOrAnchorEditsAreRejected(int kind)
    {
        var(d,id,ids)=Loop(DefinitionId.New(),kind,true);var original=d.Value;var ends=SketchFixedCurveEndpoints.All(original).Single().Ends;
        Assert.Throws<CadValidationException>(()=>d.MovePoint(ends.Start,new(99,99)));Assert.Same(original,d.Value);
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Constraints=s.Constraints.Select(c=>c.Id==ends.StartAnchor?c with{IsEnabled=false}:c).ToImmutableArray()}));
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Constraints=s.Constraints.Where(c=>c.Id!=ends.StartAnchor).ToImmutableArray()}));
        if(kind==0)d.EditEllipse(id,original.Ellipses[0].Geometry with{Center=new(21,1)});
        else d.EditFreeform(id,original.Freeforms[0].Geometry with{Poles=original.Freeforms[0].Geometry.Poles.Select(p=>new Point2d(p.X+1,p.Y+1)).ToImmutableArray()});
        var(a,b)=SketchFixedCurveEndpoints.Positions(d.Value,id);
        Assert.Equal(a,d.Value.Points.Single(p=>p.Id==ends.Start).Position);Assert.Equal(b,d.Value.Points.Single(p=>p.Id==ends.End).Position);
        Assert.Equal(ends,SketchFixedCurveEndpoints.All(d.Value).Single().Ends);_ =SketchProfileBuilder.Mixed(d.Value,ids);
        d.Undo();Assert.Same(original,d.Value);d.Redo();d.Value.Validate();
        d.RemoveEntities([id]);Assert.Empty(SketchFixedCurveEndpoints.All(d.Value));Assert.DoesNotContain(d.Value.Constraints,c=>c.Id==ends.StartAnchor||c.Id==ends.EndAnchor);
        d.Undo();d.RemoveEntities([ends.Start]);Assert.Empty(SketchFixedCurveEndpoints.All(d.Value));Assert.DoesNotContain(d.Value.Lines,l=>l.Start==ends.Start||l.End==ends.Start);d.Value.Validate();
    }

    [Fact] public void MatchingCoordinatesNeverSubstituteForSharedIdentityAndExplicitReuseIsChecked()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Explicit",RigidTransform3d.Identity));d.AddFreeform(Geometry(1));var id=d.Value.Freeforms[0].Id;
        var(a,b)=SketchFixedCurveEndpoints.Positions(d.Value,id);d.AddLine(b,a);var independent=d.Value.Lines.Single();
        d.ExposeCurveEndpoints(id);Assert.Empty(SketchMixedLoops.Find(d.Value));
        Assert.Throws<CadValidationException>(()=>SketchProfileBuilder.Mixed(d.Value,[id,independent.Id]));
        d.Undo();d.ExposeCurveEndpoints(id,independent.End,independent.Start);Assert.Equal(2,Assert.Single(SketchMixedLoops.Find(d.Value)).Length);
        d.Undo();Assert.Throws<CadValidationException>(()=>d.ExposeCurveEndpoints(id,independent.Start,independent.End));
        d.AddEllipse(new(new(0,0),new(5,0),new(0,2),0,Math.Tau));Assert.Throws<CadValidationException>(()=>d.ExposeCurveEndpoints(d.Value.Ellipses[0].Id));
    }

    [Fact] public async Task LinkedSketchRefreshMovesExactEndpointsAndRecomputesSharedBoundaryAtomically()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var source=new SketchDraft(CadSketch.Create(part,"Source",RigidTransform3d.Identity));source.AddFreeform(Geometry(1));await session.ExecuteAsync(new UpsertSketchCommand(source.Value,solver));
            var s=session.Snapshot.Sketches[source.Value.Id];var target=new SketchDraft(CadSketch.Create(part,"Linked ends",RigidTransform3d.Translate(0,0,10)));
            target.Project(s,s.Freeforms[0].Id);var id=target.Value.Freeforms[0].Id;target.SetConstruction([id],false);target.ExposeCurveEndpoints(id);Close(target,id,false);
            await session.ExecuteAsync(new UpsertSketchCommand(target.Value,solver));var t=session.Snapshot.Sketches[target.Value.Id];var ends=SketchFixedCurveEndpoints.All(t).Single().Ends;
            var reference=SketchProfileReference.CreateMixed(t,[id,t.Lines[0].Id]);await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,t.Plane)),"Consumer",part,sketchSource:reference));
            var before=session.Snapshot;var feature=before.Features.Values.Single();var edit=new SketchDraft(s);
            edit.EditFreeform(s.Freeforms[0].Id,Geometry(1) with{Poles=Geometry(1).Poles.Select(p=>new Point2d(p.X+2,p.Y*1.2)).ToImmutableArray()});
            await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver));var after=session.Snapshot;var updated=after.Sketches[t.Id];
            Assert.Equal(ends,SketchFixedCurveEndpoints.All(updated).Single().Ends);Assert.Equal(new(12,0),updated.Points.Single(p=>p.Id==ends.Start).Position);
            Assert.False(after.Features[feature.Id].IsStale);Assert.InRange(Math.Abs(after.Features[feature.Id].Result.VolumeMm3/feature.Result.VolumeMm3-1.2),0,1e-6);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData(0,"legacy")][InlineData(1,"legacy")][InlineData(0,"point")][InlineData(1,"anchor")]
    public async Task StorageRejectsEndpointForgeryAndLegacySchemaDisguise(int kind,string change)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var(d,_,_)=Loop(part,kind,false);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));
            string path=files.PathFor("endpoints.cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,path);
            if(change=="legacy")FormatEvolutionTests.RewriteManifest(path,manifest=>manifest with{Sections=manifest.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=14}:s).ToImmutableArray()});
            else FormatEvolutionTests.RewriteSection(path,"sketches",bytes=>
            {
                var packed=MessagePackSerializer.Deserialize<PackSketches>(bytes);var sketch=packed.Sketches[0];
                var ends=sketch.Ellipses?.Single().Endpoints??sketch.Freeforms!.Single().Endpoints!;
                if(change=="point")sketch=sketch with{Points=sketch.Points.Select(p=>p.Id==ends.Start?p with{X=p.X+1}:p).ToArray()};
                else sketch=sketch with{Constraints=sketch.Constraints.Select(c=>c.Id==ends.StartAnchor?c with{Enabled=false}:c).ToArray()};
                return MessagePackSerializer.Serialize(new PackSketches([sketch]));
            });
            var error=await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
            Assert.Contains(change=="legacy"?"legacy sketch schema":"Exact curve endpoints",error.ToString());
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task EditorExposesEndpointsWithoutPublishingUntilPreviewConfirmation()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var d=new SketchDraft(CadSketch.Create(part,"Editor",RigidTransform3d.Identity));d.AddFreeform(Geometry(1));var id=d.Value.Freeforms[0].Id;
            await using var editor=new SketchEditorViewModel(doc,kernel,new ManagedSketchConstraintSolver(),d.Value,true);editor.Select(id);editor.ExposeCurveEndpointsCommand.Execute(null);
            Assert.Equal(2,editor.Sketch.Points.Length);Assert.Same(initial,session.Snapshot);
            Close(editor.Draft,id,false);await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(initial,session.Snapshot);
            await editor.ConfirmCommand.ExecuteAsync(null);Assert.NotEmpty(session.Snapshot.Sketches);session.Snapshot.Validate();
        }
        finally{doc.Detach();}
    }

    [Fact] public void ParametersCannotBypassTheNumericEndpointDefinitionThroughItsAnchors()
    {
        var(initial,part)=SketchCommandTests.Document();var(d,_,_)=Loop(part,1,false);var sketch=d.Value;
        var doc=initial with{Sketches=initial.Sketches.Add(sketch.Id,sketch)};var ends=SketchFixedCurveEndpoints.All(sketch).Single().Ends;
        foreach(var id in new[]{ends.StartAnchor,ends.EndAnchor})foreach(string key in new[]{"X","Y"})
        {
            var target=ParameterTarget.Sketch(sketch.Id,id,key);Assert.DoesNotContain(target,ParameterTargets.All(doc));
            Assert.Throws<CadValidationException>(()=>ParameterTargets.Read(doc,target));Assert.Throws<CadValidationException>(()=>ParameterTargets.WriteConstraint(doc,target,123));
        }
    }
}
