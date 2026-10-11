using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Bound presentation report. Native correspondence and source/result XDE membership are rechecked on persistence.</summary>
public sealed record RepetitionPresentationEvidence(string SourceFingerprint,string ResultFingerprint,
    int SourceTopologyCount,int ResultTopologyCount,int StyledSourceSlots,int CopiedResultSlots,int SharedResultSlots,
    bool CompleteHistory,ImmutableArray<int> ConflictingResultIndices)
{
    public void ValidateFor(FeatureDefinition feature)
    {
        if(feature.IsStale||feature.Recipe is not RepetitionRecipe recipe||recipe.Source.Source is null||feature.Result.Source is null||
            SourceFingerprint is not {Length:64}||ResultFingerprint is not {Length:64}||!SourceFingerprint.All(Uri.IsHexDigit)||!ResultFingerprint.All(Uri.IsHexDigit)||
            SourceTopologyCount<1||ResultTopologyCount<1||
            SourceTopologyCount>1_000_000||ResultTopologyCount>1_000_000||StyledSourceSlots<0||StyledSourceSlots>SourceTopologyCount||
            CopiedResultSlots<0||CopiedResultSlots>ResultTopologyCount||SharedResultSlots<0||SharedResultSlots>ResultTopologyCount||
            ConflictingResultIndices.IsDefault||ConflictingResultIndices.Any(i=>i<0||i>=ResultTopologyCount)||
            ConflictingResultIndices.Distinct().Count()!=ConflictingResultIndices.Length||
            recipe.StylePolicy==RepetitionStylePolicy.VerifiedInheritance&&(!CompleteHistory||!ConflictingResultIndices.IsEmpty||feature.TopologyHistory is null))
            throw new CadValidationException("Invalid or stale repetition presentation evidence.");
    }
}
