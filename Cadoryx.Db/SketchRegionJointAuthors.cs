using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum SketchRegionJointKind { SourceEndpoints,NativeIntersection,RoundBridge,BevelBridge }

/// <summary>Owned creating-operation branch and source parameter values. This
/// does not certify a maintained differential chart or native topology history.</summary>
public sealed record SketchRegionJointAuthor(int PreviousSourceSlot,int NextSourceSlot,
    SketchEntityId PreviousSource,SketchEntityId NextSource,SketchRegionJointKind Kind,
    double PreviousParameter,double NextParameter,bool PreviousTraversalReversed,bool NextTraversalReversed);

public static class SketchRegionJointAuthors
{
    public const string LegacyAlgorithm="cadoryx.expression-region-offset.1";
    public const string CurrentAlgorithm="cadoryx.expression-region-offset.2";

    public static bool SameValues(ImmutableArray<SketchRegionJointAuthor> a,ImmutableArray<SketchRegionJointAuthor> b)
        =>!a.IsDefault&&!b.IsDefault&&a.SequenceEqual(b);
    public static bool SameStructure(ImmutableArray<SketchRegionJointAuthor> a,ImmutableArray<SketchRegionJointAuthor> b)
        =>!a.IsDefault&&!b.IsDefault&&a.Length==b.Length&&a.Zip(b).All(pair=>pair.First is {} x&&pair.Second is {} y&&
            x.PreviousSourceSlot==y.PreviousSourceSlot&&x.NextSourceSlot==y.NextSourceSlot&&x.PreviousSource==y.PreviousSource&&
            x.NextSource==y.NextSource&&x.Kind==y.Kind&&x.PreviousTraversalReversed==y.PreviousTraversalReversed&&x.NextTraversalReversed==y.NextTraversalReversed);

    internal static void Validate(SketchRegionOffsetAuthor group,int loopIndex)
    {
        var loop=group.Loops[loopIndex];var source=group.Sources[loopIndex];var joints=loop.Joints;
        if(joints.IsDefault)throw new CadValidationException("Uninitialized region connection author table.");
        if(group.Algorithm==LegacyAlgorithm)
        {
            if(!joints.IsEmpty)throw new CadValidationException("Legacy region semantics cannot carry invented creating-operation connections.");
            return;
        }
        if(group.Algorithm!=CurrentAlgorithm||joints.Length!=(source.Length==1?0:source.Length))
            throw new CadValidationException("Region connections must cover exactly the declared original corner set.");
        int step=loop.InputTraversalReversed?-1:1;
        for(int i=0;i<joints.Length;i++)
        {
            var joint=joints[i]??throw new CadValidationException("Missing region connection author.");
            int previous=loop.InputTraversalReversed?source.Length-1-i:i,next=(previous+step+source.Length)%source.Length;
            if(joint.PreviousSourceSlot!=previous||joint.NextSourceSlot!=next||!Enum.IsDefined(joint.Kind)||
                joint.PreviousSource!=source[previous].Entity||joint.NextSource!=source[next].Entity||
                joint.PreviousTraversalReversed!=(source[previous].Reversed^loop.InputTraversalReversed)||
                joint.NextTraversalReversed!=(source[next].Reversed^loop.InputTraversalReversed))
                throw new CadValidationException("Region connection lost its original source identity, adjacency or directed traversal.");
            CadGuard.Finite(joint.PreviousParameter,joint.NextParameter);
            int a=Parallel(previous),b=Parallel(next);
            bool bridge=joint.Kind is SketchRegionJointKind.RoundBridge or SketchRegionJointKind.BevelBridge;
            if(b!=(a+(bridge?2:1))%loop.Outputs.Length)
                throw new CadValidationException("Region connection does not match its complete output adjacency.");
            if(bridge)
            {
                var middle=loop.Outputs[(a+1)%loop.Outputs.Length];
                var role=joint.Kind==SketchRegionJointKind.RoundBridge?SketchRegionAuthorRole.RoundBridge:SketchRegionAuthorRole.BevelBridge;
                if(middle.Role!=role||!middle.SourceSlots.SequenceEqual(new[]{previous,next}))
                    throw new CadValidationException("Region bridge differs from its declared connection branch.");
            }
            Bound(loop.Outputs[a].Geometry,joint.PreviousParameter,false);
            Bound(loop.Outputs[b].Geometry,joint.NextParameter,true);
            if(joint.Kind!=SketchRegionJointKind.NativeIntersection)
            {
                var p=source[previous].Geometry;var q=source[next].Geometry;
                if(joint.PreviousParameter!=(p.Reversed^joint.PreviousTraversalReversed?p.FirstParameter:p.LastParameter)||
                    joint.NextParameter!=(q.Reversed^joint.NextTraversalReversed?q.LastParameter:q.FirstParameter))
                    throw new CadValidationException("Natural or bridged region connection must retain its directed full source endpoints.");
            }
        }
        int Parallel(int slot)
        {
            var indices=Enumerable.Range(0,loop.Outputs.Length).Where(i=>loop.Outputs[i].Role==SketchRegionAuthorRole.Parallel&&loop.Outputs[i].SourceSlots.SequenceEqual(new[]{slot})).ToArray();
            if(indices.Length!=1)throw new CadValidationException("Region connection requires one complete parallel output per original source.");
            return indices[0];
        }
        static void Bound(SketchCurveExpressionGeometry geometry,double parameter,bool start)
        {
            double bound=start?(geometry.Reversed?geometry.LastParameter:geometry.FirstParameter):(geometry.Reversed?geometry.FirstParameter:geometry.LastParameter);
            if(Math.Abs(parameter-bound)>1e-8*Math.Max(1,Math.Abs(parameter)))
                throw new CadValidationException("Region connection parameter differs from its complete parallel output bound.");
        }
    }
}
