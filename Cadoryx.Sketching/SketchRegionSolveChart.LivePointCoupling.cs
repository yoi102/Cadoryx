using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Sketching;

internal sealed partial class SketchRegionSolveChart
{
    internal static SketchRegionSolveChart? TryCreateForAllEquations(CadSketch sketch)
    {
        if(TryCreateForEquations(sketch) is {} established)return established;
        var active=sketch.RegionOffsets.Where(g=>!g.IsFrozen).ToArray();
        if(active.Length<2)return null;
        var selected=ImmutableDictionary.CreateBuilder<Guid,SketchRegionOffsetAuthor>();bool hasNatural=false;
        foreach(var group in active)
            if(ProveGroup(sketch,group,out bool natural)){selected.Add(group.Id,group);hasNatural|=natural;}
        if(!hasNatural)return null;
        var proof=SketchRegionLivePointCouplingProof.TryCreate(sketch,selected.Keys.ToImmutableHashSet());
        return proof is null?null:new(selected.ToImmutable(),active.Length,null,proof);
    }

    // Derive only each already-proved full parallel author from the current
    // independent coordinates. This does not construct an off-manifold loop,
    // modify generated variables, update persisted joints or prove topology.
    internal ImmutableDictionary<SketchEntityId,SketchCurveExpressionGeometry> DeriveLocalAuthors(CadSketch candidate,Guid id)
    {
        var group=candidate.RegionOffsets.Single(g=>g.Id==id);
        if(!IsLivePointCoupled||!Contains(group))throw new CadValidationException("Local authors require this solve's exact natural group proof.");
        var values=ImmutableDictionary.CreateBuilder<SketchEntityId,SketchCurveExpressionGeometry>();
        for(int i=0;i<group.Loops.Length;i++)
        foreach(var output in group.Loops[i].Outputs)
        {
            if(output.Role!=SketchRegionAuthorRole.Parallel||output.SourceSlots.Length!=1||output.BasisDepth is <1 or >2)
                throw new CadValidationException("Only proved full natural parallel authors may be derived for live equations.");
            var source=group.Sources[i][output.SourceSlots[0]];
            var current=SketchExpressionEntities.Author(candidate,source.Entity);
            if(current.Kind!=source.Kind||!SketchExpressionEntities.SameFamily(source.Geometry,current.Geometry))
                throw new CadValidationException("Live equation source changed its exact author family.");
            var path=new List<SketchCurveExpressionGeometry>();var old=output.Geometry;
            for(int depth=0;depth<output.BasisDepth;depth++)
            {
                if(old.Basis is not {} basis||old.FirstParameter!=basis.FirstParameter||old.LastParameter!=basis.LastParameter)
                    throw new CadValidationException("Live natural equation authors require the full original finite operation window.");
                path.Add(old);old=basis;
            }
            if(!old.SameValues(source.Geometry))throw new CadValidationException("Live natural author lost its declared complete source basis.");
            var geometry=current.Geometry;
            for(int depth=path.Count-1;depth>=0;depth--)
            {
                var operation=path[depth];geometry=operation with{Basis=geometry,FirstParameter=geometry.FirstParameter,
                    LastParameter=geometry.LastParameter,Reversed=operation.Reversed^operation.Basis!.Reversed^geometry.Reversed};
            }
            geometry.Validate();values.Add(output.Curve,geometry);
        }
        return values.ToImmutable();
    }
}
