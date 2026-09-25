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
using Xunit;

namespace Cadoryx.Tests;

public sealed class ExpandedMixedSketchTests
{
    private static ImmutableArray<SketchBoundaryCurve> RoundedLoop(double x,double y,double width,double height)=>
    [
        new(new(x,y),new(x+width,y),new(x+width/2,y-width/2)),
        new(new(x+width,y),new(x+width,y+height)),
        new(new(x+width,y+height),new(x,y+height),new(x+width/2,y+height+width/2)),
        new(new(x,y+height),new(x,y))
    ];

    [Fact] public async Task MultipleArcsFormOneExactClosedProfile()
    {
        var (_,part)=SketchCommandTests.Document();
        var draft=new SketchDraft(CadSketch.Create(part,"Two arcs",RigidTransform3d.Identity));
        draft.AddArc(new(0,0),new(5,-5),new(10,0));
        var first=draft.Value.Arcs[^1];
        draft.AddLine(new(10,0),new(10,10),first.End);
        draft.AddArc(new(10,10),new(5,15),new(0,10),draft.Value.Lines[^1].End);
        var second=draft.Value.Arcs[^1];
        draft.AddLine(new(0,10),new(0,0),second.End,first.Start);
        var loop=Assert.Single(SketchMixedLoops.Find(draft.Value));
        var profile=SketchProfileBuilder.Mixed(draft.Value,loop);
        Assert.Equal(2,profile.BoundaryCurves.Count(c=>c.Middle is not null));
        var assets=new MemoryAssetStore();
        using var result=await new OcctGeometryKernel().EvaluateAsync(new ExtrudeRecipe(profile,4,RigidTransform3d.Identity),assets);
        Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-(400+100*Math.PI)),0,.03);
        var topIndex=Array.FindIndex(profile.BoundaryCurves.ToArray(),c=>c.Middle is {Y:>10});
        Assert.True(topIndex>=0);
        var topCurve=profile.BoundaryCurves[topIndex];
        var crossed=profile with{BoundaryCurves=profile.BoundaryCurves.SetItem(topIndex,
            new(topCurve.Start,topCurve.End,new(5,-5)))};
        Assert.Throws<CadValidationException>(crossed.Validate);
    }

    [Fact] public async Task MixedOuterAcceptsSeparatedCircularAndPolygonHoles()
    {
        var profile=new SketchProfile([]){BoundaryCurves=RoundedLoop(0,4,20,10),
            Holes=[new(new(10,9),1)],
            PolygonHoles=[[new(15,7),new(17,7),new(17,9),new(15,9)]]};
        profile.Validate();
        var assets=new MemoryAssetStore();
        using var result=await new OcctGeometryKernel().EvaluateAsync(new ExtrudeRecipe(profile,3,RigidTransform3d.Identity),assets);
        Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-3*(196+99*Math.PI)),0,.03);
        Assert.Throws<CadValidationException>(()=>(profile with{Holes=[new(new(10,26),1)]}).Validate());
        Assert.Throws<CadValidationException>(()=>(profile with{PolygonHoles=[[new(19,10),new(22,10),new(22,12),new(19,12)]]}).Validate());
    }

    [Fact] public async Task LinkedMixedHoleRecomputesAndRoundTrips()
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var draft=new SketchDraft(CadSketch.Create(part,"Curved hole",RigidTransform3d.Identity));
        draft.AddRectangle(new(0,0),new(30,20));
        draft.AddArc(new(2,4),new(4,2),new(6,4));var bottom=draft.Value.Arcs[^1];
        draft.AddLine(new(6,4),new(6,8),bottom.End);
        draft.AddArc(new(6,8),new(4,10),new(2,8),draft.Value.Lines[^1].End);
        var top=draft.Value.Arcs[^1];
        draft.AddLine(new(2,8),new(2,4),top.End,bottom.Start);
        var outer=Assert.Single(SketchLoops.Find(draft.Value));
        var hole=Assert.Single(SketchMixedLoops.Find(draft.Value));
        await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,solver));
        var sketch=session.Snapshot.Sketches[draft.Value.Id];
        var source=SketchProfileReference.Create(sketch,outer) with{MixedHoleIds=[hole]};
        var recipe=(ExtrudeRecipe)source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane));
        await session.ExecuteAsync(new AddBodyCommand(recipe,"Curved hole extrusion",part,sketchSource:source));
        var feature=Assert.Single(session.Snapshot.Features.Values);
        Assert.InRange(Math.Abs(feature.Result.VolumeMm3-(1800-3*(16+4*Math.PI))),0,.03);
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            document.SelectedTargetPart=part;document.StartSketchFeature("Extrude");
            document.SelectedSketchProfile=document.SketchProfiles.Single(p=>p.Source.Lines.SequenceEqual(outer));
            Assert.Single(document.SketchMixedHoleChoices);
        }
        finally{document.Detach();}
        var before=session.Snapshot;var point=sketch.Points.Single(p=>p.Id==bottom.Middle);
        await session.ExecuteAsync(new UpsertSketchCommand(sketch with{Points=sketch.Points.Replace(point,point with{Position=new(4,1)})},solver));
        Assert.NotEqual(feature.Result.VolumeMm3,session.Snapshot.Features[feature.Id].Result.VolumeMm3);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
        await session.RedoAsync();
        var path=files.PathFor("mixed-hole.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets))
        {
            loaded.Snapshot.Validate();
            Assert.True(hole.SequenceEqual(loaded.Snapshot.Features[feature.Id].SketchSource!.MixedHoleIds.Single()));
        }
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?
            s with{SchemaVersion=15}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
    }

    [Fact] public void MixedHoleRejectsTouchingOrNestedRegions()
    {
        var outer=new SketchProfile([new(0,0),new(30,0),new(30,20),new(0,20)]);
        var hole=RoundedLoop(2,4,4,4);
        (outer with{MixedHoles=[hole]}).Validate();
        Assert.Throws<CadValidationException>(()=>(outer with{MixedHoles=[RoundedLoop(0,4,4,4)]}).Validate());
        Assert.Throws<CadValidationException>(()=>(outer with{MixedHoles=[hole,RoundedLoop(3,5,2,2)]}).Validate());
        Assert.Throws<CadValidationException>(()=>(outer with{MixedHoles=[hole],Holes=[new(new(4,6),1)]}).Validate());
    }
}
