using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Sketching;

internal sealed partial class SketchRegionSolveChart
{
    // TryCreate deliberately retains the original independent-route refusal.
    // A separate equation entry may admit the explicitly proved point-variable
    // graph, and the returned chart records that it is a coupled system.
    internal static SketchRegionSolveChart? TryCreateForEquations(CadSketch sketch)
    {
        if(TryCreate(sketch) is {} independent)return independent;
        var active=sketch.RegionOffsets.Where(g=>!g.IsFrozen).ToArray();
        if(active.Length<2)return null;
        var selected=ImmutableDictionary.CreateBuilder<Guid,SketchRegionOffsetAuthor>();bool hasNatural=false;
        foreach(var group in active)
            if(ProveGroup(sketch,group,out bool natural)){selected.Add(group.Id,group);hasNatural|=natural;}
        if(!hasNatural)return null;
        var ids=selected.Keys.ToImmutableHashSet();
        var proof=SketchRegionPointCouplingProof.TryCreate(sketch,ids);
        return proof is null?null:new(selected.ToImmutable(),active.Length,proof);
    }
}
