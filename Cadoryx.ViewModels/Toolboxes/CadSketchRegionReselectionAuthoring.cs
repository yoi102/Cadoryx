using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadSketchRegionReselectionAuthoring
{
    internal static async Task<string> ApplyAsync(CadDocumentViewModel document,JsonElement args,CancellationToken token,DocumentCommandExpectation? expectation=null)
    {
        token.ThrowIfCancellationRequested();
        if(document.IsReadOnly||document.IsClosingRequested)throw new InvalidOperationException("The active document cannot be edited.");
        RequireFields(args,["action",..expectation is null?new[]{"documentId","stateId"}:[],"sketchId","revision","id","sources"]);
        if(RequiredString(args,"action",32)!="reselect_region_sources")throw new ArgumentException("Unknown region source action.");
        var before=document.Session.Snapshot;
        var expected=expectation??new DocumentCommandExpectation(new(RequiredGuid(args,"documentId")),new(RequiredGuid(args,"stateId")));expected.Validate(before);
        var sketch=before.Sketches.GetValueOrDefault(new(RequiredGuid(args,"sketchId")))??throw new ArgumentException("Exact sketch is missing.");
        if(sketch.Revision!=RequiredGuid(args,"revision"))throw new StaleDocumentException();
        var group=sketch.RegionOffsets.SingleOrDefault(g=>g.Id==RequiredGuid(args,"id"))??throw new ArgumentException("Exact whole-region group is missing.");
        if(!args.TryGetProperty("sources",out var input)||input.ValueKind!=JsonValueKind.Array||input.GetArrayLength()!=group.Sources.Length)
            throw new ArgumentException("Supply the complete ordered source map for every loop.");
        var slots=ImmutableArray.CreateBuilder<ImmutableArray<SketchEntityId>>();int i=0;
        foreach(var loop in input.EnumerateArray())
        {
            if(loop.ValueKind!=JsonValueKind.Array||loop.GetArrayLength()!=group.Sources[i++].Length)throw new ArgumentException("Supply every source slot in traversal order.");
            slots.Add(loop.EnumerateArray().Select(item=>item.ValueKind==JsonValueKind.String&&Guid.TryParseExact(item.GetString(),"D",out var id)&&id!=Guid.Empty?
                new SketchEntityId(id):throw new ArgumentException("Each source must be an explicit nonempty GUID.")).ToImmutableArray());
        }
        var kernel=document.GeometryKernel as IExactSketchExpressionRegions??throw new NotSupportedException("Whole-region sources require exact replay.");
        var prepared=await Task.Run(()=>SketchRegionSourceReselection.Prepare(sketch,group.Id,slots.ToImmutable(),kernel,token),token);
        token.ThrowIfCancellationRequested();var after=before with{Sketches=before.Sketches.SetItem(sketch.Id,prepared.Candidate)};
        SketchRegionParameterTargets.GuardStructure(before,after);
        await document.Session.ExecuteAsync(new UpsertSketchCommand(prepared.Candidate,new ManagedSketchConstraintSolver(kernel)),token,expected);
        var accepted=document.Session.Snapshot;var current=accepted.Sketches[sketch.Id];
        return JsonSerializer.Serialize(new{success=true,action="reselect_region_sources",changed=true,sketchId=sketch.Id.ToString(),id=group.Id,
            revision=current.Revision,documentId=accepted.Id.ToString(),stateId=accepted.StateId.ToString(),
            sources=current.RegionOffsets.Single(g=>g.Id==group.Id).Sources.Select(l=>l.Select(v=>v.Entity.ToString())),
            outputIds=current.RegionOffsets.Single(g=>g.Id==group.Id).Loops.Select(l=>l.Outputs.Select(o=>o.Curve.ToString()))});
    }
}
