using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Copied native ordered membership, bound to the program seed's entire exact source identity.</summary>
public sealed record ContourChainMember(int EdgeIndex,int? FirstVertex,int? LastVertex,double? FirstParameter,double? LastParameter);
public sealed record ContourChainEvidence(ImmutableArray<ContourChainMember> Members,int? FirstVertex,int? LastVertex,
    bool IsClosed,bool IsClosedAndTangent,double Length)
{
    public void Validate(int seed)
    {
        static bool Index(int? index)=>index is null or >=0 and <=100000;
        CadGuard.Positive(Length);
        if(Members.IsDefaultOrEmpty||Members.Length>4096||Members.Any(m=>m is null)||!Index(FirstVertex)||!Index(LastVertex)||
            IsClosedAndTangent&&!IsClosed||Members.Select(m=>m.EdgeIndex).Distinct().Count()!=Members.Length||!Members.Any(m=>m.EdgeIndex==seed))
            throw new CadValidationException("Invalid native contour membership evidence.");
        foreach(var member in Members)
        {
            if(member.EdgeIndex is <0 or >100000||!Index(member.FirstVertex)||!Index(member.LastVertex)||
                member.FirstParameter.HasValue!=member.LastParameter.HasValue)
                throw new CadValidationException("Invalid contour edge/vertex locator.");
            if(member.FirstParameter is {} first&&member.LastParameter is {} last)
            {CadGuard.Finite(first,last);if(first<0||last>1||last<=first)throw new CadValidationException("Invalid native contour parameter interval.");}
        }
        // Native discovery re-proves order, all shared junctions and exact parameter intervals before every build.
    }
}
public sealed record ContourLocalProgram(Guid Id,ExactTopologySelection Seed,ContourChainEvidence Chain,
    ContourRadiusLaw? Radius=null,ExactTopologySelection? SupportFace=null,double? Distance=null,double? SecondDistance=null)
{
    public void Validate(LocalFeatureOperation operation)
    {
        if(!Enum.IsDefined(operation))throw new CadValidationException("Invalid contour operation.");
        if(Id==Guid.Empty||Seed is null||Chain is null)throw new CadValidationException("A contour program requires stable identity and exact evidence.");
        Seed.Validate();if(Seed.Kind!=HistoryShapeKind.Edge)throw new CadValidationException("Choose an exact edge seed.");Chain.Validate(Seed.FullTopologyIndex);
        if(operation==LocalFeatureOperation.Fillet)
        {if(Radius is null||SupportFace is not null||Distance is not null||SecondDistance is not null)throw new CadValidationException("A fillet requires only its complete radius law.");Radius.Validate();}
        else
        {
            if(Radius is not null||SupportFace is null||Distance is not {} distance)throw new CadValidationException("A chamfer requires exact support face and distance.");
            SupportFace.Validate();if(SupportFace.Kind!=HistoryShapeKind.Face||!SameSource(Seed,SupportFace))throw new CadValidationException("Chamfer support face must belong to the exact source.");
            CadGuard.Positive(distance);if(distance>100000)throw new CadValidationException("Chamfer distance too large.");
            if(SecondDistance is {} second){CadGuard.Positive(second);if(second>100000)throw new CadValidationException("Second chamfer distance too large.");}
        }
    }
    internal static bool SameSource(ExactTopologySelection a,ExactTopologySelection b)=>a.DocumentId==b.DocumentId&&a.FeatureId==b.FeatureId&&a.Revision==b.Revision&&a.Asset==b.Asset&&a.Fingerprint==b.Fingerprint&&a.AdapterVersion==b.AdapterVersion;
}
/// <summary>One atomic native multi-contour operation. No guessed chain or automatic index rebinding.</summary>
public sealed record ContourLocalRecipe(GeometryAssetRef Source,LocalFeatureOperation Operation,ImmutableArray<ContourLocalProgram> Programs):GeometryRecipe
{
    public override IEnumerable<GeometryAssetRef> AssetInputs=>[Source];
    public override void Validate()
    {
        Source.Validate();
        if(Source.Kind!=BodyKind.Solid||!Enum.IsDefined(Operation)||Programs.IsDefaultOrEmpty||Programs.Length>256||Programs.Any(p=>p is null)||Programs.Select(p=>p.Id).Distinct().Count()!=Programs.Length)
            throw new CadValidationException("Select bounded distinct contour programs on a single solid.");
        foreach(var p in Programs){p.Validate(Operation);if(p.Seed.Asset!=Source.AssetId||p.Seed.Revision!=Source.Revision||!ContourLocalProgram.SameSource(p.Seed,Programs[0].Seed))throw new CadValidationException("Contour programs must share one exact source.");}
        var members=Programs.SelectMany(p=>p.Chain.Members).ToArray();if(members.Length>65536||members.Select(m=>m.EdgeIndex).Distinct().Count()!=members.Length)
            throw new CadValidationException("Contour programs overlap or exceed the chain budget.");
    }
    public bool HasCurrentSource(DocumentSnapshot document)=>!Programs.IsDefaultOrEmpty&&document.Id==Programs[0].Seed.DocumentId&&document.Features.TryGetValue(Programs[0].Seed.FeatureId,out var feature)&&!feature.IsStale&&!feature.IsSuppressed&&feature.Result==Source;
    public void ValidateApplied(DocumentSnapshot document,FeatureDefinition feature)
    {
        var seed=Programs[0].Seed;
        if(seed.DocumentId!=document.Id||feature.Inputs.Length!=1||feature.Inputs[0]!=seed.FeatureId||feature.SketchSource is not null||
            !document.Features.TryGetValue(seed.FeatureId,out var source)||source.PartId!=feature.PartId||!feature.IsStale&&!feature.IsSuppressed&&!HasCurrentSource(document))
            throw new CadValidationException("Contour feature input is foreign or stale.");
    }
}
