using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;
using Operation=Cadoryx.Db.LocalFeatureOperation;

namespace Cadoryx.Tests;

public sealed partial class ContourLocalTests
{
    [Theory][InlineData("constant")][InlineData("linear")][InlineData("smooth")][InlineData("sampled")][InlineData("spline")]
    public async Task WholeTangentContourUsesCompleteRadiusLawAndOneAtomicHistory(string mode)
    {
        await using var f=await Fixture.Create(true);var seed=f.Select(ShapeKind.Edge,b=>Math.Abs(b.Maximum.X)<1e-5&&Math.Abs(b.Maximum.Y)<1e-5&&b.SizeZ>7.49);
        var chain=await f.Kernel.DiscoverContourAsync(f.Session.Snapshot,seed,Operation.Fillet,null,f.Assets);Assert.Equal(2,chain.Members.Length);Assert.Equal(15,chain.Length,5);
        ContourRadiusLaw law=mode switch
        {
            "constant"=>new ConstantContourRadius(.5),"linear"=>new LinearContourRadius(.5,1.5),"smooth"=>new SmoothContourRadius(.5,1.5),
            "sampled"=>new SampledContourRadius([new(Guid.NewGuid(),0,.5),new(Guid.NewGuid(),.5,1.2),new(Guid.NewGuid(),1,1.5)]),
            _=>new SplineContourRadius([new(Guid.NewGuid(),.5),new(Guid.NewGuid(),1.5),new(Guid.NewGuid(),1)],[0,1],[3,3],2)
        };
        var recipe=new ContourLocalRecipe(f.Source.Result,Operation.Fillet,[new(Guid.NewGuid(),seed,chain,law)]);var before=f.Session.Snapshot;
        await f.Session.ExecuteAsync(new AddContourLocalCommand(f.Source.OutputBodyId,recipe,"Whole contour"));var after=f.Session.Snapshot;after.Validate();
        var result=after.Features.Values.Single(p=>p.Recipe is ContourLocalRecipe);Assert.Single(after.Bodies);Assert.Equal(f.Source.Result,after.Features[f.Source.Id].Result);
        Assert.InRange(result.Result.VolumeMm3,1700,1800);using(var shape=OcctGeometryBridge.ReadShape(result.Result,f.Assets)){Assert.True(shape.GetValidationReport(true,true).IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);}
        if(mode=="constant")Assert.Equal(1800-15*(1-Math.PI/4)*.25,result.Result.VolumeMm3,4);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task IndependentContoursHaveNativeAnalyticFilletOrTwoDistanceChamfer(bool chamfer)
    {
        await using var f=await Fixture.Create();var programs=ImmutableArray.CreateBuilder<ContourLocalProgram>();var operation=chamfer?Operation.Chamfer:Operation.Fillet;
        foreach(bool opposite in new[]{false,true})
        {
            var edge=f.Select(ShapeKind.Edge,b=>Math.Abs(b.Minimum.X-(opposite?10:0))<1e-5&&Math.Abs(b.Maximum.X-(opposite?10:0))<1e-5&&Math.Abs(b.Minimum.Y-(opposite?12:0))<1e-5&&Math.Abs(b.Maximum.Y-(opposite?12:0))<1e-5&&b.SizeZ>14.99);
            var face=chamfer?f.Select(ShapeKind.Face,b=>opposite?b.Minimum.X>9.99:Math.Abs(b.Maximum.X)<1e-5):null;
            var chain=await f.Kernel.DiscoverContourAsync(f.Session.Snapshot,edge,operation,face,f.Assets);
            programs.Add(new(Guid.NewGuid(),edge,chain,chamfer?null:new ConstantContourRadius(.5),face,chamfer?1:null,chamfer?2:null));
        }
        await f.Session.ExecuteAsync(new AddContourLocalCommand(f.Source.OutputBodyId,new(f.Source.Result,operation,programs.ToImmutable()),"Two contours"));
        Assert.Equal(chamfer?1770:1800-30*(1-Math.PI/4)*.25,f.Session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,4);
    }
    [Theory][InlineData("order")][InlineData("length")][InlineData("endpoint")][InlineData("membership")]
    public async Task ForgedChainEvidenceRejectsAtomicallyBeforePublishing(string mutation)
    {
        await using var f=await Fixture.Create(true);var seed=f.Select(ShapeKind.Edge,b=>Math.Abs(b.Maximum.X)<1e-5&&Math.Abs(b.Maximum.Y)<1e-5&&b.SizeZ>7.49);
        var chain=await f.Kernel.DiscoverContourAsync(f.Session.Snapshot,seed,Operation.Fillet,null,f.Assets);
        chain=mutation switch{"order"=>chain with{Members=chain.Members.Reverse().ToImmutableArray()},"length"=>chain with{Length=chain.Length+1},
            "endpoint"=>chain with{FirstVertex=chain.LastVertex},_=>chain with{Members=chain.Members.Take(1).ToImmutableArray()}};
        var recipe=new ContourLocalRecipe(f.Source.Result,Operation.Fillet,[new(Guid.NewGuid(),seed,chain,new LinearContourRadius(.5,1.5))]);var before=f.Session.Snapshot;int count=f.Assets.Count;var history=f.Session.HistoryUsage;
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Session.ExecuteAsync(new AddContourLocalCommand(f.Source.OutputBodyId,recipe,"Bad chain")));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);Assert.Equal(history,f.Session.HistoryUsage);
    }
    [Fact]public async Task UpstreamChangeFreezesOldContourUntilExplicitFreshDiscoveryAndReselection()
    {
        await using var f=await Fixture.Create();var seed=f.Select(ShapeKind.Edge,b=>Math.Abs(b.Maximum.X)<1e-5&&Math.Abs(b.Maximum.Y)<1e-5&&b.SizeZ>14.99);
        var chain=await f.Kernel.DiscoverContourAsync(f.Session.Snapshot,seed,Operation.Fillet,null,f.Assets);
        var program=new ContourLocalProgram(Guid.NewGuid(),seed,chain,new ConstantContourRadius(.5));
        await f.Session.ExecuteAsync(new AddContourLocalCommand(f.Source.OutputBodyId,new(f.Source.Result,Operation.Fillet,[program]),"Contour"));
        var old=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is ContourLocalRecipe);
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));
        Assert.True(f.Session.Snapshot.Features[old.Id].IsStale);Assert.Equal(old.Result,f.Session.Snapshot.Features[old.Id].Result);
        seed=f.Select(ShapeKind.Edge,b=>Math.Abs(b.Maximum.X)<1e-5&&Math.Abs(b.Maximum.Y)<1e-5&&b.SizeZ>14.99);
        chain=await f.Kernel.DiscoverContourAsync(f.Session.Snapshot,seed,Operation.Fillet,null,f.Assets);
        await f.Session.ExecuteAsync(new ReselectContourLocalCommand(old.Id,new(f.Session.Snapshot.Features[f.Source.Id].Result,Operation.Fillet,[program with{Seed=seed,Chain=chain}])));
        Assert.False(f.Session.Snapshot.Features[old.Id].IsStale);Assert.Equal(program.Id,((ContourLocalRecipe)f.Session.Snapshot.Features[old.Id].Recipe).Programs[0].Id);
        Assert.Equal(1980-15*(1-Math.PI/4)*.25,f.Session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,4);
    }
    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets{get;}=new();internal OcctGeometryKernel Kernel{get;}=new();internal CadDocumentSession Session{get;}
        internal FeatureDefinition Source{get;private set;}=null!;internal CadDocumentViewModel Document{get;}
        private Fixture(){Session=new(DocumentSnapshot.Create("Contours"),Assets,Kernel,new InlineSessionDispatcher());Document=new(Session,Kernel,new CadMessageLog());}
        internal static async Task<Fixture> Create(bool chain=false)
        {
            var f=new Fixture();
            if(chain)
            {
                using var lower=ShapeFactory.CreateBox(10,12,7.5);using var upper=lower.Transformed(ShapeTransform.CreateTranslation(0,0,7.5));using var joined=lower.Fuse(upper);
                using var stored=OcctGeometryBridge.StoreShape(joined,f.Assets);await f.Session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Joined source"));
            }
            else await f.Session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,12,15,RigidTransform3d.Identity),"Box"));
            f.Source=f.Session.Snapshot.Features.Values.Single();return f;
        }
        internal ExactTopologySelection Select(ShapeKind kind,Func<BoundingBox3d,bool> predicate)
        {
            var source=Session.Snapshot.Features[Source.Id];using var shape=OcctGeometryBridge.ReadShape(source.Result,Assets);using var map=RepairSnapshot.Create(shape);
            foreach(var item in map.Topology.Where(t=>t.Kind==kind)){using var sub=map.CopySubshape(item.Selection);if(predicate(sub.GetBoundingBox()))return new(Session.Snapshot.Id,source.Id,source.Result.Revision,source.Result.AssetId,map.Fingerprint,item.Selection.Index,kind==ShapeKind.Edge?HistoryShapeKind.Edge:HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);}
            throw new InvalidOperationException("Fixture exact selection missing.");
        }
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
}
