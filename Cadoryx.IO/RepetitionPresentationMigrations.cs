namespace Cadoryx.IO;

internal static partial class MessagePackSections
{
    internal static ReadOnlyMemory<byte> UpgradeRepetitionPresentation(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackFeaturesV3>(bytes);
        PackRepetition? Upgrade(PackRepetition? layout)
        {
            if(layout is null)return null;
            if(layout.StylePolicy is not null)throw new InvalidDataException("Current presentation policy in legacy feature schema.");
            return layout with{StylePolicy=0};
        }
        return Serialize(old with{Features=old.Features.Select(f=>
        {
            if(f.RepetitionPresentation is not null)throw new InvalidDataException("Current presentation evidence in legacy feature schema.");
            var r=f.Recipe;
            return f with{Recipe=r with{Repetition=Upgrade(r.Repetition),
                HolePattern=r.HolePattern is {} hole?hole with{Layout=Upgrade(hole.Layout)!}:null,
                ExtrusionPattern=r.ExtrusionPattern is {} extrusion?extrusion with{Layout=Upgrade(extrusion.Layout)!}:null}};
        }).ToArray()});
    }
}
