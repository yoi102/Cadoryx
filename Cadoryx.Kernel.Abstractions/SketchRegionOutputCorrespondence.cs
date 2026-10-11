using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Kernel.Abstractions;

public static class SketchRegionOutputCorrespondence
{
    public static SketchRegionOffsetAuthor ReplaceDerivedValues(SketchRegionOffsetAuthor original,
        ImmutableArray<ImmutableArray<SketchRegionSourceAuthor>> currentSources,ExactExpressionRegionOffset computed)
    {
        if(original.IsFrozen)throw new CadValidationException("Resume or explicitly reselect the frozen whole region group.");
        if(currentSources.IsDefault||currentSources.Length!=original.Sources.Length||computed.Loops.IsDefault||computed.Loops.Length!=original.Loops.Length||computed.OutwardDistance!=original.Distance)
            throw new CadValidationException("Whole-region provider changed its source/loop count or authored distance.");
        for(int i=0;i<currentSources.Length;i++)
        {
            if(currentSources[i].IsDefault||currentSources[i].Length!=original.Sources[i].Length)throw new CadValidationException("Region source slots changed; explicitly reselect them.");
            for(int j=0;j<currentSources[i].Length;j++)
            {
                var old=original.Sources[i][j];var current=currentSources[i][j];current.Geometry.Validate();
                if(old.Entity!=current.Entity||old.Kind!=current.Kind||old.Reversed!=current.Reversed||
                    !SketchExpressionEntities.SameFamily(old.Geometry,current.Geometry))
                    throw new CadValidationException("Provider changed a declared source identity, family or traversal.");
            }
        }
        var updated=ImmutableArray.CreateBuilder<SketchRegionLoopAuthor>();
        for(int i=0;i<computed.Loops.Length;i++)
        {
            var old=original.Loops[i];var current=computed.Loops[i];
            if(current.SourceLoopIndex!=i||current.Depth!=old.Depth||current.ParentLoopIndex!=old.Parent||current.InputTraversalReversed!=old.InputTraversalReversed||
                current.OutwardLoopDistance!=(old.Depth%2==0?original.Distance:-original.Distance)||current.Segments.IsDefault||current.Segments.Length!=old.Outputs.Length)
                throw new CadValidationException("Region loop direction, containment or output count changed; keep the last valid group and reselect.");
            var outputs=ImmutableArray.CreateBuilder<SketchRegionOutputAuthor>();
            for(int j=0;j<current.Segments.Length;j++)
            {
                var prior=old.Outputs[j];var segment=current.Segments[j];segment.Geometry.Validate();
                if(segment.OriginalCurveIndices.IsDefault||segment.SourceIds.IsDefault||segment.OriginalCurveIndices.Any(slot=>slot<0||slot>=currentSources[i].Length)||
                    !Enum.IsDefined(segment.Kind)||(int)segment.Kind!=(int)prior.Role||!segment.OriginalCurveIndices.SequenceEqual(prior.SourceSlots)||
                    !segment.SourceIds.SequenceEqual(segment.OriginalCurveIndices.Select(slot=>currentSources[i][slot].Entity)))
                    throw new CadValidationException("Output kind and original source slots do not prove a complete author correspondence.");
                if(segment.Kind==ExactExpressionRegionSegmentKind.Parallel)
                {
                    if(segment.OriginalCurveIndices.Length!=1||segment.BasisDepth is <1 or >2||segment.SourceTraversalReversed!=prior.SourceTraversalReversed)
                        throw new CadValidationException("Parallel output has unknown source traversal or incomplete finite operation prefix.");
                    var basis=segment.Geometry;for(int k=0;k<segment.BasisDepth;k++)basis=basis.Basis??throw new CadValidationException("Missing parallel prefix.");
                    if(!basis.SameValues(currentSources[i][segment.OriginalCurveIndices[0]].Geometry))throw new CadValidationException("Provider replaced a parallel source value.");
                    var operation=segment.BasisDepth==2?segment.Geometry.Basis!:segment.Geometry;
                    if(operation.Kind!=SketchCurveExpressionKind.Offset||operation.SignedOffset!=original.Distance*(segment.SourceTraversalReversed?-1:1)||
                        segment.Geometry.Reversed!=(basis.Reversed^segment.SourceTraversalReversed))
                        throw new CadValidationException("Provider changed the parallel operation distance or direction.");
                }
                else if(segment.BasisDepth!=0||segment.Geometry.Basis is not null)
                    throw new CadValidationException("A bridge cannot inherit an undeclared parallel source prefix.");
                // Only values derived by the full operation change. Curve, endpoint
                // and equation IDs stay on the already proven same author role.
                outputs.Add(prior with{Geometry=segment.Geometry,BasisDepth=segment.BasisDepth});
            }
            var joints=original.Algorithm==SketchRegionJointAuthors.CurrentAlgorithm?SketchRegionJointCorrespondence.Copy(current.Joints):ImmutableArray<SketchRegionJointAuthor>.Empty;
            if(!SketchRegionJointAuthors.SameStructure(old.Joints,joints))
                throw new CadValidationException("Region creating-operation connection branch changed; explicitly reselect the complete group.");
            updated.Add(old with{Outputs=outputs.ToImmutable(),Joints=joints});
        }
        return original with{Sources=currentSources,Loops=updated.ToImmutable()};
    }
}

