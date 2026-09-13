using System.IO;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class HistorySmokeRunner
{
    internal static async Task RunAsync(IServiceProvider services,string output)
    {
        var kernel=services.GetRequiredService<IGeometryKernel>();var resolver=(ITopologyHistoryResolver)kernel;
        var storage=services.GetRequiredService<IDocumentStorage>();var assets=new MemoryAssetStore();var reports=new List<object>();
        foreach(var operation in Enum.GetValues<LocalFeatureOperation>())
        {
            await using var session=new CadDocumentSession(DocumentSnapshot.Create("History"),assets,kernel,new InlineSessionDispatcher());
            var box=new BoxRecipe(10,20,30,new(new(4,5,6),Quaterniond.FromAxisAngle(new(2,3,1),0.8)));
            await session.ExecuteAsync(new AddBodyCommand(box,"Box"));var source=session.Snapshot.Features.Values.Single();
            var face=TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.YMin);
            var edge=TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.YMin,BoxBoundary.ZMax);
            await session.ExecuteAsync(new LocalFeatureCommand(edge,operation,2));var local=session.Snapshot.Features.Values.Single(f=>f.Recipe is LocalFeatureRecipe);
            var before=session.Snapshot;Check(local.TopologyHistory is not null,"Verified native history");
            await session.ExecuteAsync(new RecomputeCommand(source.Id,box with{X=40}));var after=session.Snapshot;
            var modified=await resolver.TraceAsync(after,face,local.Id,assets);var generated=await resolver.TraceAsync(after,edge,local.Id,assets);
            Check(modified.Status==HistoryResolutionStatus.Resolved&&generated.Status==HistoryResolutionStatus.Generated,"Modified face and generated role");
            await session.UndoAsync();Check(ReferenceEquals(before,session.Snapshot),"Exact undo");
            await session.RedoAsync();Check(ReferenceEquals(after,session.Snapshot),"Exact redo");
            string path=Path.Combine(output,"history-"+operation+".cadoryx");await session.SaveAsync(storage,path);
            using var loaded=await storage.LoadAsync(path,assets);
            Check(await resolver.TraceAsync(loaded.Snapshot,face,local.Id,assets)==modified,"Reopened locator");
            Check(after.Features[local.Id].TopologyHistory!.Entries.SequenceEqual(loaded.Snapshot.Features[local.Id].TopologyHistory!.Entries),"Persisted relations");
            foreach(string extension in new[]{"step","iges","stl"})await kernel.ExportAsync(loaded.Snapshot,assets,Path.Combine(output,"history-"+operation+"."+extension));
            reports.Add(new{operation=operation.ToString(),modified,generated,history=loaded.Snapshot.Features[local.Id].TopologyHistory});
        }
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Boolean history"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var source=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,22,32,RigidTransform3d.Translate(4,-1,-1)),"Tool",source.PartId));
            var tool=session.Snapshot.Features.Values.Single(f=>f.Id!=source.Id);
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[source.OutputBodyId,tool.OutputBodyId]));
            var before=session.Snapshot;var target=before.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            await session.ExecuteAsync(new RecomputeCommand(tool.Id,new BoxRecipe(3,22,32,RigidTransform3d.Translate(4,-1,-1))));var after=session.Snapshot;
            Check(Math.Abs(after.Features[target.Id].Result.VolumeMm3-4200)<1e-5,"Boolean recomputed geometry");
            await session.UndoAsync();Check(ReferenceEquals(before,session.Snapshot),"Boolean exact undo");
            await session.RedoAsync();Check(ReferenceEquals(after,session.Snapshot),"Boolean exact redo");
            string path=Path.Combine(output,"history-Boolean.cadoryx");await session.SaveAsync(storage,path);
            using var loaded=await storage.LoadAsync(path,assets);
            var stable=await resolver.TraceAsync(loaded.Snapshot,TopologyReference.Box(loaded.Snapshot,source.Id,BoxBoundary.XMin),target.Id,assets);
            var split=await resolver.TraceAsync(loaded.Snapshot,TopologyReference.Box(loaded.Snapshot,source.Id,BoxBoundary.YMin),target.Id,assets);
            var deleted=await resolver.TraceAsync(loaded.Snapshot,TopologyReference.Box(loaded.Snapshot,tool.Id,BoxBoundary.ZMax),target.Id,assets);
            Check(stable.Status==HistoryResolutionStatus.Resolved&&split.Status==HistoryResolutionStatus.Ambiguous&&deleted.Status==HistoryResolutionStatus.Deleted,"Boolean reloaded source relations");
            var history=loaded.Snapshot.Features[target.Id].TopologyHistory!;
            Check(history.ArgumentCount==2&&history.Entries.SequenceEqual(after.Features[target.Id].TopologyHistory!.Entries),"Boolean persisted relations");
            foreach(string extension in new[]{"step","iges","stl"})await kernel.ExportAsync(loaded.Snapshot,assets,Path.Combine(output,"history-Boolean."+extension));
            reports.Add(new{operation="BooleanCut",stable,split,deleted,history});
        }
        Check(assets.Count==0,"Assets released");
        await File.WriteAllTextAsync(Path.Combine(output,"history-result.json"),JsonSerializer.Serialize(new{passed=true,kernel=kernel.Version,remainingAssets=assets.Count,reports},new JsonSerializerOptions{WriteIndented=true}));
    }
    private static void Check(bool condition,string message){if(!condition)throw new InvalidOperationException("History: "+message);}
}
