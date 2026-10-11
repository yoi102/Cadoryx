using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>One authored distance belongs to one complete region, never to a
/// parallel segment or bridge. Owner is the sketch; Curve carries the explicit
/// group identity only for SketchRegion targets.</summary>
public static class SketchRegionParameterTargets
{
    public static (CadSketch Sketch,SketchRegionOffsetAuthor Group) Resolve(DocumentSnapshot doc,ParameterTarget target)
    {
        if(target.Kind!=ParameterTargetKind.SketchRegion||target.Owner==Guid.Empty||target.Constraint is not null||
            target.Curve is not {} id||id==Guid.Empty||target.Index is not null||target.Property!="Distance"||
            !doc.Sketches.TryGetValue(new(target.Owner),out var sketch))
            throw new CadValidationException("Whole-region parameter needs an explicit sketch/group identity and distance field.");
        return(sketch,sketch.RegionOffsets.SingleOrDefault(g=>g.Id==id)??throw new CadValidationException("Whole-region parameter group is missing."));
    }
    public static ParameterQuantity Read(DocumentSnapshot doc,ParameterTarget target)=>ParameterQuantity.Of(Resolve(doc,target).Group.Distance,ParameterDimension.Length);
    public static void RequireWritable(DocumentSnapshot doc,ParameterTarget target)
    {if(Resolve(doc,target).Group.IsFrozen)throw new CadValidationException("Detach the distance driver before freezing its complete region group.");}
    public static IEnumerable<ParameterTarget> All(DocumentSnapshot doc)
    {
        foreach(var sketch in doc.Sketches.Values.OrderBy(s=>s.Id.Value))
            foreach(var group in sketch.RegionOffsets.Where(g=>!g.IsFrozen).OrderBy(g=>g.Id))yield return ParameterTarget.SketchRegion(sketch.Id,group.Id);
    }
    public static void GuardStructure(DocumentSnapshot before,DocumentSnapshot after)
    {
        IEnumerable<ParameterTarget> Targets(ParameterBook book)=>book.Bindings.Select(b=>b.Target).Concat(book.Parameters.Select(p=>p.Reference).OfType<ParameterTarget>());
        var retained=Targets(after.Parameters).ToHashSet();
        foreach(var target in Targets(before.Parameters).Where(t=>t.Kind==ParameterTargetKind.SketchRegion&&retained.Contains(t)).Distinct())
        {
            var a=Resolve(before,target).Group;var b=Resolve(after,target).Group;
            if(a.Id!=b.Id||a.Algorithm!=b.Algorithm||a.Join!=b.Join||a.Sources.Length!=b.Sources.Length||a.Loops.Length!=b.Loops.Length)
                throw new CadValidationException("Detach region distance targets before changing the whole author correspondence.");
            for(int i=0;i<a.Sources.Length;i++)
            {
                if(a.Sources[i].Length!=b.Sources[i].Length)throw new CadValidationException("Region distance target source slots changed.");
                for(int j=0;j<a.Sources[i].Length;j++)
                {
                    var x=a.Sources[i][j];var y=b.Sources[i][j];
                    if(x.Entity!=y.Entity||x.Kind!=y.Kind||x.Reversed!=y.Reversed||!SketchExpressionEntities.SameFamily(x.Geometry,y.Geometry))
                        throw new CadValidationException("Detach region distance targets before reselecting their source authors.");
                }
                var xloop=a.Loops[i];var yloop=b.Loops[i];
                if(xloop.Depth!=yloop.Depth||xloop.Parent!=yloop.Parent||xloop.InputTraversalReversed!=yloop.InputTraversalReversed||xloop.Outputs.Length!=yloop.Outputs.Length||
                    !SketchRegionJointAuthors.SameStructure(xloop.Joints,yloop.Joints))
                    throw new CadValidationException("Region distance target containment or output count changed.");
                for(int j=0;j<xloop.Outputs.Length;j++)
                {
                    var x=xloop.Outputs[j];var y=yloop.Outputs[j];
                    if(x.Curve!=y.Curve||x.Endpoints!=y.Endpoints||x.Role!=y.Role||x.SourceTraversalReversed!=y.SourceTraversalReversed||!x.SourceSlots.SequenceEqual(y.SourceSlots))
                        throw new CadValidationException("Region distance target output role or identity changed.");
                }
            }
        }
    }
}
