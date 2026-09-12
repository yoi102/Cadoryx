using System.IO;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.wpf.Diagnostics;

internal static class TopologySmokeRunner
{
    internal static async Task RunAsync(IServiceProvider services,string output)
    {
        var kernel=services.GetRequiredService<IGeometryKernel>();var resolver=(ITopologyResolver)kernel;
        var storage=services.GetRequiredService<IDocumentStorage>();var assets=new MemoryAssetStore();
        TopologyInspection report;
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Topology verification"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Box"));var feature=session.Snapshot.Features.Values.Single();
            var semantic=TopologyReference.Box(session.Snapshot,feature.Id,BoxBoundary.XMax,BoxBoundary.ZMax);
            var exact=TopologyReference.Box(session.Snapshot,feature.Id,BoxBoundary.XMax,policy:TopologyRebindPolicy.ExactRevision);
            await session.ExecuteAsync(new UpsertTopologyReferenceCommand(semantic));await session.ExecuteAsync(new UpsertTopologyReferenceCommand(exact));var before=session.Snapshot;
            await session.ExecuteAsync(new RecomputeCommand(feature.Id,new BoxRecipe(40,20,30,RigidTransform3d.Translate(10,20,30))));var after=session.Snapshot;
            report=await TopologyReferenceInspection.InspectAsync(after,assets,resolver);
            Check(report.Results.Count(r=>r.Status==TopologyResolutionStatus.Resolved)==1&&report.Results.Count(r=>r.Status==TopologyResolutionStatus.Stale)==1,"Rebind and stale reference");
            await session.UndoAsync();Check(ReferenceEquals(before,session.Snapshot),"Exact undo");
            Check((await resolver.ResolveAsync(session.Snapshot,exact,assets)).Status==TopologyResolutionStatus.Resolved,"Restored revision");
            await session.RedoAsync();Check(ReferenceEquals(after,session.Snapshot),"Exact redo");
            var path=Path.Combine(output,"topology.cadoryx");await session.SaveAsync(storage,path);
            using var loaded=await storage.LoadAsync(path,assets);Check(loaded.Snapshot.TopologyReferences[semantic.Id]==semantic,"Persistent semantic reference");
            var reopened=await TopologyReferenceInspection.InspectAsync(loaded.Snapshot,assets,resolver);Check(report.Results.SequenceEqual(reopened.Results),"Reopened resolutions");
            foreach(var extension in new[]{"step","iges","stl"})await kernel.ExportAsync(loaded.Snapshot,assets,Path.Combine(output,"topology."+extension));
        }
        Check(assets.Count==0,"All assets released");
        await File.WriteAllTextAsync(Path.Combine(output,"topology-result.json"),JsonSerializer.Serialize(new{passed=true,kernel=kernel.Version,remainingAssets=assets.Count,report},new JsonSerializerOptions{WriteIndented=true}));
    }
    private static void Check(bool value,string message){if(!value)throw new InvalidOperationException("Topology: "+message);}
}
