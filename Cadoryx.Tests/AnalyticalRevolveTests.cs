using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AnalyticalRevolveTests
{
    private static (SketchDraft Draft,SketchProfileReference Source,double Volume) Make(DefinitionId part,string kind)
    {
        var d=new SketchDraft(CadSketch.Create(part,kind,RigidTransform3d.Translate(2,3,4)));
        if(kind=="circle")
        {
            d.AddCircle(new(10,0),2);
            return(d,SketchProfileReference.CreateCircle(d.Value,d.Value.Circles[0].Id),80*Math.PI*Math.PI);
        }
        if(kind=="arc")
        {
            d.AddArc(new(8,0),new(10,2),new(12,0));
            return(d,SketchProfileReference.CreateArcSegment(d.Value,d.Value.Arcs[0].Id),40*Math.PI*Math.PI);
        }
        d.AddArc(new(12,-2),new(14,0),new(12,2));var arc=d.Value.Arcs[0];
        d.AddLine(new(12,2),new(10,2),arc.End);var top=d.Value.Lines[^1];
        d.AddLine(new(10,2),new(10,-2),top.End);var left=d.Value.Lines[^1];
        d.AddLine(new(10,-2),new(12,-2),left.End,arc.Start);
        // Pappus: rectangle moment 8*11 plus semicircle moment 2*pi*12+16/3.
        return(d,SketchProfileReference.CreateMixed(d.Value,SketchMixedLoops.Find(d.Value).Single()),
            2*Math.PI*(88+24*Math.PI+16d/3));
    }
    [Theory] [InlineData("circle")] [InlineData("arc")] [InlineData("mixed")]
    public async Task ExactCurvedRevolutionHasExpectedVolumeSolidAndAngleHandle(string kind)
    {
        var (d,source,volume)=Make(DefinitionId.New(),kind);
        var profile=kind switch{"circle"=>SketchProfileBuilder.Circle(d.Value,source.CircleId!.Value),
            "arc"=>SketchProfileBuilder.ArcSegment(d.Value,source.ArcId!.Value),_=>SketchProfileBuilder.Mixed(d.Value,source.MixedBoundaryIds)};
        var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        foreach(double angle in new[]{Math.PI,Math.Tau})
        {
            var recipe=new RevolveRecipe(profile,angle,d.Value.Plane);
            using var result=await kernel.EvaluateAsync(recipe,assets);
            Assert.Equal(BodyKind.Solid,result.Geometry.Kind);Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-volume*angle/Math.Tau),0,.002);
            Assert.Single(SolidDimensionHandles.Describe(recipe,RigidTransform3d.Identity));
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public void AxisChecksUseArcExtremaAndRejectCrossings()
    {
        static void Reject(SketchProfile p)=>Assert.Throws<CadValidationException>(()=>new RevolveRecipe(p,Math.PI,RigidTransform3d.Identity).Validate());
        Reject(SketchProfile.FromCircle(new(2,0),2));Reject(SketchProfile.FromCircle(new(-10,0),2));
        Point2d At(double degrees)=>new(1+2*Math.Cos(degrees*Math.PI/180),2*Math.Sin(degrees*Math.PI/180));
        var crossing=new SketchProfile([]){Arc=new(At(100),At(260),At(280))};
        Assert.True(crossing.Arc.Start.X>0&&crossing.Arc.Middle.X>0&&crossing.Arc.End.X>0);Reject(crossing);
        var arc=crossing.Arc;Reject(new SketchProfile([]){Arc=new(arc.End,arc.Middle,arc.Start)});
        Reject(new SketchProfile([new(-1,0),new(2,0),new(2,2),new(-1,2)]));
        new RevolveRecipe(SketchProfile.FromCircle(new(10,0),3) with{Holes=[new(new(10,0),1)]},Math.PI,RigidTransform3d.Identity).Validate();
        new RevolveRecipe(new([]){Bezier=new(new(8,0),new(10,2),new(12,0))},Math.PI,RigidTransform3d.Identity).Validate();
        new RevolveRecipe(new([new(0,0),new(2,0),new(2,3),new(0,3)]),Math.Tau,RigidTransform3d.Identity).Validate();
        new RevolveRecipe(new([]){Arc=new(new(0,-2),new(2,0),new(0,2))},Math.Tau,RigidTransform3d.Identity).Validate();
    }
    [Theory] [InlineData("circle")] [InlineData("arc")] [InlineData("mixed")]
    public async Task LinkedCurvePreviewRecomputeUndoPersistenceAndLegacyVersionRejection(string kind)
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var (d,_,_)=Make(part,kind);
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            document.SelectedTargetPart=part;document.SelectedSketchId=d.Value.Id;document.StartSketchFeature("Revolve");
            var selected=document.SketchProfiles.Single(p=>kind switch{"circle"=>p.Source.CircleId is not null,
                "arc"=>p.Source.ArcId is not null,_=>!p.Source.MixedBoundaryIds.IsEmpty});
            document.SelectedSketchProfile=selected;document.AngleDegrees=180;var uncommitted=session.Snapshot;
            await document.PreviewCommand.ExecuteAsync(null);Assert.True(document.HasPreview,document.ToolStatus);
            Assert.Same(uncommitted,session.Snapshot);await document.ConfirmCommand.ExecuteAsync(null);
        }
        finally{document.Detach();}
        var feature=Assert.Single(session.Snapshot.Features.Values);var before=session.Snapshot;var sketch=before.Sketches[d.Value.Id];
        var edit=new SketchDraft(sketch);
        if(kind=="circle")edit.Change(s=>s with{Circles=s.Circles.SetItem(0,s.Circles[0] with{Radius=3}),
            Constraints=s.Constraints.Select(c=>c is RadiusConstraint r?r with{Radius=3}:c).ToImmutableArray()});
        else edit.MovePoint(sketch.Arcs[0].Middle,kind=="arc"?new(10,3):new(15,0));
        await session.ExecuteAsync(new UpsertSketchCommand(edit.Value,solver));var after=session.Snapshot;
        Assert.NotEqual(feature.Result.VolumeMm3,after.Features[feature.Id].Result.VolumeMm3);after.Validate();
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        var invalid=new SketchDraft(after.Sketches[d.Value.Id]);
        var point=invalid.Value.Points[0];invalid.MovePoint(point.Id,new(-20,point.Position.Y));
        await Assert.ThrowsAnyAsync<Exception>(()=>session.ExecuteAsync(new UpsertSketchCommand(invalid.Value,solver)));Assert.Same(after,session.Snapshot);
        var path=files.PathFor(kind+".cadoryx");var storage=new CadDocumentStorage();await session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,assets))
        await using(var reopened=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher()))
        {
            loaded.Snapshot.Validate();
            var expected=(RevolveRecipe)after.Features[feature.Id].Recipe;var actual=(RevolveRecipe)loaded.Snapshot.Features[feature.Id].Recipe;
            Assert.Equal(expected.Profile.Circle,actual.Profile.Circle);Assert.Equal(expected.Profile.Arc,actual.Profile.Arc);
            Assert.True(SketchMixedProfile.SameLoop(expected.Profile.BoundaryCurves,actual.Profile.BoundaryCurves));
            await reopened.ExecuteAsync(new RecomputeCommand(feature.Id,loaded.Snapshot.Features[feature.Id].Recipe));
            Assert.Equal(after.Features[feature.Id].Result.VolumeMm3,reopened.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
        }
        foreach(int version in new[]{21,20})
        {
            FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=version}:s).ToImmutableArray()});
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        }
    }
}
