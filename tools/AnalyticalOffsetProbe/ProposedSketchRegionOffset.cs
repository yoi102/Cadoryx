using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Editor;

namespace Cadoryx.OffsetPrototype;

public static class ProposedSketchRegionOffset
{
    private sealed record Item(SketchRegionReference Source,int Depth,int? Parent);
    public static SketchRegionOffset.Result Create(CadSketch sketch,IEnumerable<SketchEntityId> seeds,double distance,OffsetJoin join)
    {
        if(!Enum.IsDefined(join))throw new CadValidationException("Unknown offset join.");
        if(!double.IsFinite(distance)||Math.Abs(distance)<=1e-7)throw new CadValidationException("Invalid region offset distance.");
        var sourceItems=Flatten(SketchRegionOffset.SelectedRegions(sketch,seeds));
        var candidate=sketch;var created=new SketchRegionReference[sourceItems.Count];
        for(int i=0;i<created.Length;i++)
        {
            var item=sourceItems[i];var boundary=ProposedSketchLoopOffset.Create(candidate,Seed(item.Source),
                (item.Depth%2==0?1:-1)*distance,join);
            candidate=boundary.Sketch;created[i]=boundary.Reference;
        }
        var after=SketchRegionOffset.SelectedRegions(candidate,created.Select(Seed));var afterItems=Flatten(after);
        foreach(var item in afterItems)
        {
            int at=Array.FindIndex(created,c=>Contains(item.Source,Seed(c)));
            int? parent=item.Parent is {} p?Array.FindIndex(created,c=>Contains(afterItems[p].Source,Seed(c))):null;
            if(at<0||parent is <0||item.Depth!=sourceItems[at].Depth||parent!=sourceItems[at].Parent)
                throw new CadValidationException("Offset changes containment, splits or merges regions.");
        }
        if(afterItems.Count!=created.Length)throw new CadValidationException("Offset changes the number of boundaries.");
        return new(candidate,sourceItems.Select((item,i)=>new SketchRegionOffset.Boundary(item.Source,created[i],item.Depth,item.Parent)).ToImmutableArray(),after);
    }
    private static List<Item> Flatten(IEnumerable<SketchRegionReference> roots)
    {
        var result=new List<Item>();
        void Add(SketchRegionReference node,int depth,int? parent)
        {
            int at=result.Count;result.Add(new(node with{Children=[]},depth,parent));
            foreach(var child in node.Children)Add(child,depth+1,at);
        }
        foreach(var root in roots)Add(root,0,null);return result;
    }
    private static SketchEntityId Seed(SketchRegionReference node)=>node.CircleId??
        (!node.MixedCurveIds.IsEmpty?node.MixedCurveIds[0]:node.PolygonLineIds[0]);
    private static bool Contains(SketchRegionReference node,SketchEntityId seed)=>node.CircleId==seed||
        node.MixedCurveIds.Contains(seed)||node.PolygonLineIds.Contains(seed);
}
