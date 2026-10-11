using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Sketching;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadSketchOffsetAuthoring
{
    internal static async Task<string> ApplyAsync(CadDocumentViewModel document,JsonElement args,string action,
        CancellationToken token,DocumentCommandExpectation? expectation=null)
    {
        token.ThrowIfCancellationRequested();
        if(document.IsReadOnly||document.IsClosingRequested)throw new InvalidOperationException("The active document cannot be edited.");
        if(action is not ("offset_sketch_loop" or "offset_sketch_regions"))throw new ArgumentException("Unknown sketch offset action.");
        RequireFields(args,["action",..expectation is null?new[]{"documentId","stateId"}:[],"sketchId","revision","seeds","distanceMm","join"]);
        var before=document.Session.Snapshot;
        var expected=expectation??new DocumentCommandExpectation(new(RequiredGuid(args,"documentId")),new(RequiredGuid(args,"stateId")));
        expected.Validate(before);
        var sketch=before.Sketches.GetValueOrDefault(new(RequiredGuid(args,"sketchId")))??throw new ArgumentException("Exact sketch is missing.");
        if(sketch.Revision!=RequiredGuid(args,"revision"))throw new StaleDocumentException();
        if(!args.TryGetProperty("seeds",out var input)||input.ValueKind!=JsonValueKind.Array||input.GetArrayLength() is <1 or >256)
            throw new ArgumentException("Seeds must contain 1 to 256 exact source author IDs.");
        if(action=="offset_sketch_loop"&&input.GetArrayLength()!=1)throw new ArgumentException("A single-loop offset requires exactly one seed.");
        var seeds=input.EnumerateArray().Select(item=>item.ValueKind==JsonValueKind.String&&Guid.TryParse(item.GetString(),out var value)&&value!=Guid.Empty?
            new SketchEntityId(value):throw new ArgumentException("Each seed must be an exact nonempty author GUID.")).ToArray();
        if(!args.TryGetProperty("distanceMm",out var distance)||distance.ValueKind!=JsonValueKind.Number||
           !distance.TryGetDouble(out var mm)||!double.IsFinite(mm))throw new ArgumentException("Distance must be a finite number in mm.");
        var join=args.TryGetProperty("join",out var selected)?selected.ValueKind==JsonValueKind.String?selected.GetString() switch
            {"Miter"=>SketchOffsetJoin.Miter,"Round"=>SketchOffsetJoin.Round,"Bevel"=>SketchOffsetJoin.Bevel,
                _=>throw new ArgumentException("Join must be Miter, Round or Bevel.")}:throw new ArgumentException("Join must be a string."):SketchOffsetJoin.Miter;
        var result=SketchRegionOffset.Create(sketch,seeds,mm,join);
        token.ThrowIfCancellationRequested();
        await document.Session.ExecuteAsync(new UpsertSketchCommand(result.Sketch,new ManagedSketchConstraintSolver()),token,expected);
        var after=document.Session.Snapshot;var current=after.Sketches[sketch.Id];
        return JsonSerializer.Serialize(new{success=true,action,changed=true,sketchId=sketch.Id.ToString(),revision=current.Revision,
            documentId=after.Id.ToString(),stateId=after.StateId.ToString(),distanceMm=mm,join=join.ToString(),
            boundaries=result.Boundaries.Select((b,index)=>new{index,depth=b.Depth,parent=b.Parent,source=Describe(b.Source),created=Describe(b.Created)}),
            roots=result.Roots.Select(Describe)});
    }
    private static object Describe(SketchRegionReference reference)=>new
    {
        circleId=reference.CircleId?.ToString(),polygonLineIds=reference.PolygonLineIds.Select(id=>id.ToString()),
        mixedCurveIds=reference.MixedCurveIds.Select(id=>id.ToString()),children=reference.Children.Select(Describe)
    };
}
