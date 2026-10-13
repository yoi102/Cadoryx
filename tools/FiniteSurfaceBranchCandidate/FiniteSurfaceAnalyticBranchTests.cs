using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed class FiniteSurfaceAnalyticBranchTests
{
    public static IEnumerable<object[]> FullCases()=>
        from kind in new[]{"sphere","cylinder"} from pose in new[]{0,1,2}
        from material in new[]{"plain","hole","island"}
        from operation in new[]{ExtrusionOperation.Fuse,ExtrusionOperation.Cut,ExtrusionOperation.NewBody}
        from branch in new[]{ExtrusionSurfaceBranch.Near,ExtrusionSurfaceBranch.Far}
        select new object[]{kind,pose,material,operation,branch};
    public static IEnumerable<object[]> AbsentCases()=>
        from kind in new[]{"sphere","cylinder"} from pose in new[]{0,1,2}
        from material in new[]{"plain","hole","island"} from far in new[]{false,true}
        select new object[]{kind,pose,material,far};

    [Theory,MemberData(nameof(FullCases))]
    public async Task ExplicitBranchRetainsCompleteMaterialOriginalAuthorsUndoAndReopen(
        string kind,int pose,string material,ExtrusionOperation operation,ExtrusionSurfaceBranch branch)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var storage=new CadDocumentStorage(surfaceExtrusionResolver:kernel);
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Explicit analytic branch"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,kind,pose,material,operation,branch);
            var producer=session.Snapshot.Features.Values.Single(f=>f.Name=="Target").Id;
            var target=session.Snapshot.Bodies.Values.Single(b=>b.Producer==producer);
            var before=session.Snapshot;var originalLimit=recipe.Options!.LimitGeometry!;
            byte[] originalBytes;using(var lease=assets.Acquire(originalLimit.AssetId))originalBytes=lease.Content.ToArray();
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Authored branch",target.PartId,
                operation==ExtrusionOperation.NewBody?null:target.Id));
            var accepted=session.Snapshot;var feature=accepted.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            double area=Measure(material,r=>kind=="sphere"?Math.PI*r*r:4*r*r);
            double integral=kind=="sphere"?Measure(material,r=>2*Math.PI/3*(216-Math.Pow(36-r*r,1.5))):
                Measure(material,a=>2*a*(a*Math.Sqrt(36-a*a)+36*Math.Asin(a/6)));
            double baseVolume=operation==ExtrusionOperation.Cut?4000:500;
            double changed=area*(operation==ExtrusionOperation.Cut?22:13)+(branch==ExtrusionSurfaceBranch.Far?integral:-integral);
            double expected=operation==ExtrusionOperation.Cut?baseVolume-changed:
                operation==ExtrusionOperation.NewBody?changed:baseVolume+changed;
            double tolerance=Math.Max(1e-6,baseVolume*1e-9);
            Assert.InRange(Math.Abs(feature.Result.VolumeMm3-expected),0,tolerance);
            Assert.Equal(recipe,feature.Recipe);Assert.Null(feature.TopologyHistory);
            Assert.Equal(originalLimit,accepted.Features[recipe.Options.LimitFace!.FeatureId].Result);
            using(var lease=assets.Acquire(originalLimit.AssetId))Assert.Equal(originalBytes,lease.Content.ToArray());
            using(var shape=OcctGeometryBridge.ReadShape(feature.Result,assets))
            {
                Assert.True(shape.GetValidationReport(true,true).IsValid);
                Assert.True(shape.GetSelfInterferenceReport().IsClear);
                Assert.Equal(operation==ExtrusionOperation.NewBody&&material=="island"?2:1,
                    shape.GetTopologySummary().UniqueCounts.SolidCount);
            }
            if(operation==ExtrusionOperation.NewBody)
            {
                Assert.Empty(feature.Inputs);Assert.Null(recipe.Options.Source);Assert.Null(recipe.Options.SupportFace);
                foreach(var body in before.Bodies)Assert.Equal(body.Value,accepted.Bodies[body.Key]);
            }
            await kernel.ConfirmFiniteSurfaceExtrusionAsync(recipe,feature.Result,assets);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);
            await session.RedoAsync();Assert.Same(accepted,session.Snapshot);
            string path=files.PathFor("branch.cadoryx");await storage.SaveAsync(accepted,assets,path);
            var reopenedAssets=new MemoryAssetStore();
            using(var reopened=await storage.LoadAsync(path,reopenedAssets))
            {
            var restored=reopened.Snapshot.Features[feature.Id];
            var author=Assert.IsType<ExtrudeRecipe>(restored.Recipe);
            Assert.Equal(recipe.Placement,author.Placement);
            Assert.Equal(recipe.Options.Operation,author.Options!.Operation);
            Assert.Equal(recipe.Options.Side,author.Options.Side);
            Assert.Equal(recipe.Options.SurfaceBranch,author.Options.SurfaceBranch);
            Assert.Equal(recipe.Options.LimitFace,author.Options.LimitFace);
            Assert.Equal(recipe.Options.SupportFace,author.Options.SupportFace);
            Assert.Equal(JsonSerializer.Serialize(recipe.Profile),JsonSerializer.Serialize(author.Profile));
            await kernel.ConfirmFiniteSurfaceExtrusionAsync((ExtrudeRecipe)restored.Recipe,restored.Result,reopenedAssets);
            Assert.InRange(Math.Abs(restored.Result.VolumeMm3-expected),0,tolerance);
            }
            Assert.Equal(0,reopenedAssets.Count);Assert.Equal(0,reopenedAssets.SizeBytes);
        }
        Assert.Equal(0,assets.Count);Assert.Equal(0,assets.SizeBytes);
    }

    [Theory,MemberData(nameof(AbsentCases))]
    public async Task MissingSelectedHalfCannotManufactureMaterialOrChangeHistory(string kind,int pose,string material,bool far)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Absent branch"),assets,kernel,new InlineSessionDispatcher()))
        {
            var branch=far?ExtrusionSurfaceBranch.Far:ExtrusionSurfaceBranch.Near;
            var recipe=await Seed(session,assets,kind,pose,material,ExtrusionOperation.Fuse,branch,
                far?"near-only":"far-only");
            var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            var before=session.Snapshot;var history=session.HistoryUsage;long generation=session.Generation;
            int count=assets.Count;long bytes=assets.SizeBytes;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddExtrusionCommand(recipe,"Reject absent branch",target.PartId,target.Id)));
            Assert.Same(before,session.Snapshot);Assert.Equal(history,session.HistoryUsage);
            Assert.Equal(generation,session.Generation);Assert.Equal(count,assets.Count);Assert.Equal(bytes,assets.SizeBytes);
        }
        Assert.Equal(0,assets.Count);Assert.Equal(0,assets.SizeBytes);
    }

    private static double Measure(string material,Func<double,double> f)=>
        f(2)-(material=="plain"?0:f(1))+(material=="island"?f(.5):0);
    private static RigidTransform3d Placement(int pose)=>pose switch
    {
        1=>new(Vector3d.Zero,Quaterniond.FromAxisAngle(new(1,0,0),Math.PI)),
        2=>new(new(10,-3,7),Quaterniond.FromAxisAngle(new Vector3d(1,1,0).Normalized(),.42)),
        _=>RigidTransform3d.Identity
    };
    private static async Task<ExtrudeRecipe> Seed(CadDocumentSession session,MemoryAssetStore assets,
        string kind,int pose,string material,ExtrusionOperation operation,ExtrusionSurfaceBranch branch,string domain="full")
    {
        var outer=Placement(pose);double start=operation==ExtrusionOperation.Cut?40:5;
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,10,start,outer),"Target"));
        var target=session.Snapshot.Bodies.Values.Single();
        var support=FiniteSurfaceExtrusionTests.Pick(session.Snapshot,target.Producer!.Value,assets,f=>
        {using var native=OcctGeometryBridge.ToNative(outer.Inverse());using var local=f.Transformed(native);return local.GetBoundingBox().Minimum.Z>start-1e-6;});
        using var localStop=kind=="sphere"?
            SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Sphere,new(new(5,5,18),new(1,0,0),new(0,1,0)),
                new(0,Math.Tau,domain=="far-only"?0:-Math.PI/2,domain=="near-only"?0:Math.PI/2),6):
            SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Cylinder,new(new(5,0,18),new(1,0,0),new(0,0,-1)),
                new(domain=="far-only"?Math.PI:0,domain=="near-only"?Math.PI:Math.Tau,0,10),6);
        using var transform=OcctGeometryBridge.ToNative(outer);using var stop=localStop.Transformed(transform);
        using var stored=OcctGeometryBridge.StoreShape(stop,assets);
        await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Finite stop",target.PartId));
        var limit=session.Snapshot.Features.Values.Single(f=>f.Name=="Finite stop");
        var face=FiniteSurfaceExtrusionTests.Pick(session.Snapshot,limit.Id,assets,_=>true);
        var profile=kind=="sphere"?SketchProfile.FromCircle(new(5,5),2):new SketchProfile([new(3,3),new(7,3),new(7,7),new(3,7)]);
        if(material!="plain")profile=kind=="sphere"?profile with{Holes=[new(new(5,5),1)]}:
            profile with{PolygonHoles=[ImmutableArray.Create(new Point2d(4,4),new(6,4),new(6,6),new(4,6))]};
        if(material=="island")profile=profile with{Islands=[kind=="sphere"?new([],new(new(5,5),.5)):
            new([new(4.5,4.5),new(5.5,4.5),new(5.5,5.5),new(4.5,5.5)])]};
        return new(profile,0,outer*RigidTransform3d.Translate(0,0,start),new(
            operation==ExtrusionOperation.Cut?ExtrusionSide.Negative:ExtrusionSide.Positive,
            Operation:operation,Source:operation==ExtrusionOperation.NewBody?null:target.Geometry,
            Termination:ExtrusionTermination.FiniteSurface,LimitFace:face,LimitGeometry:limit.Result,
            SupportFace:operation==ExtrusionOperation.NewBody?null:support,SurfaceBranch:branch));
    }
}
