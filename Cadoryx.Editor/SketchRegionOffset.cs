using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Editor;

/// <summary>Explicitly selected analytical boundaries; containment alternates material, holes and islands.</summary>
public static class SketchRegionOffset
{
    public sealed record Boundary(SketchRegionReference Source,SketchRegionReference Created,int Depth,int? Parent);
    public sealed record Result(CadSketch Sketch,ImmutableArray<Boundary> Boundaries,ImmutableArray<SketchRegionReference> Roots);
    private sealed record Selected(SketchEntityId Seed,SketchRegionReference Reference,ImmutableArray<SketchBoundaryCurve> Curves);
    private sealed record Selection(Selected[] Items,bool[,] Contains,int?[] Parents,int[] Depths);

    public static Result Create(CadSketch sketch,IEnumerable<SketchEntityId> seeds,double distance,SketchOffsetJoin join=SketchOffsetJoin.Miter)
    {
        sketch.Validate();
        if(!Enum.IsDefined(join))throw Error("LoopOffsetFailure","Unknown offset join.");
        if(!double.IsFinite(distance)||Math.Abs(distance)<=1e-7)throw Error("RegionOffsetDistance","Region offset distance must be finite and nonzero.");
        var selection=Select(sketch,seeds);var selected=selection.Items;var before=selection.Contains;
        var parents=selection.Parents;var depths=selection.Depths;
        var candidate=sketch;var created=new SketchRegionReference[selected.Length];
        for(int i=0;i<selected.Length;i++)
        {
            try
            {
                var offset=SketchLoopOffset.Create(candidate,selected[i].Seed,(depths[i]%2==0?1:-1)*distance,join);
                candidate=offset.Sketch;created[i]=offset.Reference;
            }
            catch(CadValidationException ex)
            {throw Error("RegionOffsetCollapse",ex.Message);}
        }
        var after=Containment(created.Select(r=>Curves(candidate,r)).ToArray());
        for(int i=0;i<selected.Length;i++)for(int j=0;j<selected.Length;j++)
            if(before[i,j]!=after[i,j])throw Error("RegionOffsetTopology","Offset changes containment, splits or merges material regions.");
        candidate.Validate();
        return new(candidate,selected.Select((s,i)=>new Boundary(s.Reference,created[i],depths[i],parents[i])).ToImmutableArray(),Tree(created,parents));
    }
    /// <summary>Builds a proven containment tree of these exact author selections, without creating copies.</summary>
    public static ImmutableArray<SketchRegionReference> SelectedRegions(CadSketch sketch,IEnumerable<SketchEntityId> seeds)
    {
        sketch.Validate();var selection=Select(sketch,seeds);return Tree(selection.Items.Select(i=>i.Reference).ToArray(),selection.Parents);
    }
    private static ImmutableArray<SketchRegionReference> Tree(SketchRegionReference[] references,int?[] parents)
    {
        SketchRegionReference Node(int i)=>references[i] with{Children=Enumerable.Range(0,references.Length).Where(j=>parents[j]==i).Select(Node).ToImmutableArray()};
        return Enumerable.Range(0,references.Length).Where(i=>parents[i] is null).Select(Node).ToImmutableArray();
    }
    private static Selection Select(CadSketch sketch,IEnumerable<SketchEntityId> seeds)
    {
        var ids=seeds.Distinct().Take(257).ToArray();
        if(ids.Length is <1 or >256)throw Error("RegionOffsetSelection","Select edges or circles in the boundaries to offset.");
        var loops=SketchLoops.Find(sketch).Concat(SketchMixedLoops.Find(sketch)).ToArray();
        var selected=new List<Selected>();var used=new HashSet<SketchEntityId>();
        foreach(var id in ids)
        {
            if(used.Contains(id))continue;
            SketchRegionReference reference;
            if(sketch.Circles.Any(c=>c.Id==id&&!c.IsConstruction))reference=new(CircleId:id);
            else
            {
                var found=loops.Where(l=>l.Contains(id)).ToArray();
                if(found.Length!=1||found[0].Any(e=>!sketch.Lines.Any(l=>l.Id==e)&&!sketch.Arcs.Any(a=>a.Id==e)))
                    throw Error("RegionOffsetSelection","Each seed must belong to one isolated line/arc loop or a non-construction circle.");
                reference=found[0].Any(e=>sketch.Arcs.Any(a=>a.Id==e))?new(MixedCurveIds:found[0]):new(PolygonLineIds:found[0]);
                used.UnionWith(found[0]);
            }
            used.Add(id);selected.Add(new(id,reference,Curves(sketch,reference)));
            if(selected.Count>64)throw Error("RegionOffsetLimit","At most 64 boundaries may be offset together.");
        }
        var original=selected.Select(s=>s.Curves).ToArray();
        var before=Containment(original);var parents=Parents(before);var depths=new int[selected.Count];
        for(int i=0;i<depths.Length;i++)
        {
            for(var parent=parents[i];parent is {} p;parent=parents[p])
                if(++depths[i]>12)throw Error("RegionOffsetLimit","Nested region depth exceeds 12.");
        }
        return new(selected.ToArray(),before,parents,depths);
    }
    private static bool[,] Containment(ImmutableArray<SketchBoundaryCurve>[] boundaries)
    {
        var result=new bool[boundaries.Length,boundaries.Length];
        for(int i=0;i<boundaries.Length;i++)for(int j=i+1;j<boundaries.Length;j++)
        {
            if(SketchMixedProfile.Touches(boundaries[i],boundaries[j]))
                throw Error("RegionOffsetTopology","Selected boundaries touch or intersect.");
            result[i,j]=SketchMixedProfile.StrictlyContains(boundaries[i],boundaries[j]);
            result[j,i]=SketchMixedProfile.StrictlyContains(boundaries[j],boundaries[i]);
            if(result[i,j]&&result[j,i])throw Error("RegionOffsetTopology","Containment is ambiguous.");
        }
        return result;
    }
    private static int?[] Parents(bool[,] contains)
    {
        int count=contains.GetLength(0);var result=new int?[count];
        for(int i=0;i<count;i++)
        {
            var candidates=Enumerable.Range(0,count).Where(j=>contains[j,i]&&!Enumerable.Range(0,count).Any(k=>contains[j,k]&&contains[k,i])).ToArray();
            if(candidates.Length>1)throw Error("RegionOffsetTopology","A boundary has more than one immediate parent.");
            if(candidates.Length==1)result[i]=candidates[0];
        }
        return result;
    }
    private static ImmutableArray<SketchBoundaryCurve> Curves(CadSketch sketch,SketchRegionReference reference)
    {
        var profile=reference.CircleId is {} c?SketchProfileBuilder.Circle(sketch,c):
            !reference.MixedCurveIds.IsEmpty?SketchProfileBuilder.Mixed(sketch,reference.MixedCurveIds):SketchProfileBuilder.Polygon(sketch,reference.PolygonLineIds);
        profile.Validate();
        return profile.Circle is {} circle?SketchMixedProfile.Circle(circle):!profile.BoundaryCurves.IsEmpty?profile.BoundaryCurves:SketchMixedProfile.Polygon(profile.Points);
    }
    private static CadValidationException Error(string key,string detail)=>SketchCurveFailure.Create(key,detail);
}
