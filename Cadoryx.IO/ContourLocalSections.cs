using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject]
public sealed record PackContourLocal([property:Key(0)] PackContourProgram[] Programs);
[MessagePackObject]
public sealed record PackContourProgram([property:Key(0)] Guid Id,[property:Key(1)] PackExactTopologySelection Seed,
    [property:Key(2)] PackContourChain Chain,[property:Key(3)] PackContourRadius? Radius,
    [property:Key(4)] PackExactTopologySelection? SupportFace,[property:Key(5)] double? Distance,[property:Key(6)] double? SecondDistance);
[MessagePackObject]
public sealed record PackContourChain([property:Key(0)] PackContourMember[] Members,[property:Key(1)] int? FirstVertex,
    [property:Key(2)] int? LastVertex,[property:Key(3)] bool Closed,[property:Key(4)] bool ClosedAndTangent,[property:Key(5)] double Length);
[MessagePackObject]
public sealed record PackContourMember([property:Key(0)] int Edge,[property:Key(1)] int? FirstVertex,[property:Key(2)] int? LastVertex,
    [property:Key(3)] double? FirstParameter,[property:Key(4)] double? LastParameter);
[MessagePackObject]
public sealed record PackContourRadius([property:Key(0)] string Kind,[property:Key(1)] double[] Numbers,
    [property:Key(2)] PackContourSample[]? Samples=null,[property:Key(3)] PackContourPole[]? Poles=null,
    [property:Key(4)] double[]? Knots=null,[property:Key(5)] int[]? Multiplicities=null,[property:Key(6)] int? Degree=null);
[MessagePackObject]
public sealed record PackContourSample([property:Key(0)] Guid Id,[property:Key(1)] double Parameter,[property:Key(2)] double Radius);
[MessagePackObject]
public sealed record PackContourPole([property:Key(0)] Guid Id,[property:Key(1)] double Radius);

internal static partial class MessagePackSections
{
    private static PackRecipe PackContourRecipe(ContourLocalRecipe recipe)
    {
        recipe.Validate();return new("contour-local",[],null,[G(recipe.Source)],[],(int)recipe.Operation,
            ContourLocal:new(recipe.Programs.Select(p=>new PackContourProgram(p.Id,PackExact(p.Seed),
                new(p.Chain.Members.Select(m=>new PackContourMember(m.EdgeIndex,m.FirstVertex,m.LastVertex,m.FirstParameter,m.LastParameter)).ToArray(),
                    p.Chain.FirstVertex,p.Chain.LastVertex,p.Chain.IsClosed,p.Chain.IsClosedAndTangent,p.Chain.Length),
                p.Radius is {} law?PackContourLaw(law):null,p.SupportFace is {} face?PackExact(face):null,p.Distance,p.SecondDistance)).ToArray()));
    }
    private static PackContourRadius PackContourLaw(ContourRadiusLaw law)=>law switch
    {
        ConstantContourRadius r=>new("constant",[r.Value]),LinearContourRadius r=>new("linear",[r.First,r.Last]),
        SmoothContourRadius r=>new("smooth",[r.First,r.Last,r.FirstDerivative,r.LastDerivative]),
        SampledContourRadius r=>new("sampled",[],Samples:r.Samples.Select(s=>new PackContourSample(s.Id,s.Parameter,s.Radius)).ToArray()),
        SplineContourRadius r=>new("spline",[],Poles:r.Poles.Select(s=>new PackContourPole(s.Id,s.Radius)).ToArray(),Knots:r.Knots.ToArray(),Multiplicities:r.Multiplicities.ToArray(),Degree:r.Degree),
        _=>throw new InvalidDataException("Unknown contour law.")
    };
    private static ContourRadiusLaw ReadContourLaw(PackContourRadius r)
    {
        ContourRadiusLaw law;
        if(r.Samples is null&&r.Poles is null&&r.Knots is null&&r.Multiplicities is null&&r.Degree is null)
            law=(r.Kind,r.Numbers) switch
            {
                ("constant",{Length:1})=>new ConstantContourRadius(r.Numbers[0]),
                ("linear",{Length:2})=>new LinearContourRadius(r.Numbers[0],r.Numbers[1]),
                ("smooth",{Length:4})=>new SmoothContourRadius(r.Numbers[0],r.Numbers[1],r.Numbers[2],r.Numbers[3]),
                _=>throw new InvalidDataException("Invalid analytical contour law payload.")
            };
        else if(r.Kind=="sampled"&&r.Numbers is {Length:0}&&r.Samples is {Length:>=2 and <=256} samples&&samples.All(s=>s is not null)&&
            r.Poles is null&&r.Knots is null&&r.Multiplicities is null&&r.Degree is null)
            law=new SampledContourRadius(samples.Select(s=>new ContourRadiusSample(s.Id,s.Parameter,s.Radius)).ToImmutableArray());
        else if(r.Kind=="spline"&&r.Numbers is {Length:0}&&r.Samples is null&&r.Poles is {Length:>=2 and <=256} poles&&poles.All(p=>p is not null)&&
            r.Knots is {Length:>=2 and <=256} knots&&r.Multiplicities is {Length:>=2 and <=256} mults&&r.Degree is {} degree)
            law=new SplineContourRadius(poles.Select(p=>new ContourRadiusPole(p.Id,p.Radius)).ToImmutableArray(),knots.ToImmutableArray(),mults.ToImmutableArray(),degree);
        else throw new InvalidDataException("Unexpected contour law companion.");
        law.Validate();return law;
    }
    private static ContourLocalRecipe ReadContourRecipe(PackRecipe r)
    {
        if(r.Numbers is not {Length:0}||r.Placement is not null||r.Sources is not {Length:1}||r.Profile is not {Length:0}||
            !Enum.IsDefined((LocalFeatureOperation)r.Operation)||r.Holes is not null||r.PolygonHoles is not null||r.MixedCurves is not null||
            r.MixedHoles is not null||r.Islands is not null||r.SplineControls is not null||r.SplineHoles is not null||r.Repetition is not null||
            r.Hole is not null||r.Extrusion is not null||r.Guided is not null||r.FaceFeature is not null||r.RevolveAxis is not null||
            r.HolePattern is not null||r.ExtrusionPattern is not null||r.SplineKnots is not null||r.SplineHoleKnots is not null||
            r.ContourLocal?.Programs is not {Length:>=1 and <=256} programs||programs.Any(p=>p is null||p.Seed is null||p.Chain is null||
                p.Chain.Members is not {Length:>=1 and <=4096}||p.Chain.Members.Any(m=>m is null)))
            throw new InvalidDataException("Unexpected contour recipe or companion payload.");
        var recipe=new ContourLocalRecipe(G(r.Sources[0]),(LocalFeatureOperation)r.Operation,programs.Select(p=>new ContourLocalProgram(p.Id,UnpackExact(p.Seed),
            new(p.Chain.Members.Select(m=>new ContourChainMember(m.Edge,m.FirstVertex,m.LastVertex,m.FirstParameter,m.LastParameter)).ToImmutableArray(),
                p.Chain.FirstVertex,p.Chain.LastVertex,p.Chain.Closed,p.Chain.ClosedAndTangent,p.Chain.Length),
            p.Radius is {} law?ReadContourLaw(law):null,p.SupportFace is {} face?UnpackExact(face):null,p.Distance,p.SecondDistance)).ToImmutableArray());
        recipe.Validate();return recipe;
    }
    internal static ReadOnlyMemory<byte> UpgradeContourLocalFeatures(ReadOnlyMemory<byte> bytes)
    {
        static bool ContainsContour(PackRecipe r)=>r.ContourLocal is not null||r.Kind=="contour-local"||
            r.ExtrusionPattern is {} pattern&&ContainsContour(pattern.Template)||
            r.Guided?.Sweep is {} sweep&&ContainsContour(sweep.Section)||
            r.Guided?.Loft is {} loft&&loft.Channels.Any(c=>c.Profiles.Any(ContainsContour));
        if(Read<PackFeaturesV3>(bytes).Features.Any(f=>f.Recipe.ContourLocal is not null||f.Recipe.Kind=="contour-local"||
            f.Recipe.ExtrusionPattern is {} pattern&&ContainsContour(pattern.Template)||
            f.Recipe.Guided?.Sweep is {} sweep&&ContainsContour(sweep.Section)||
            f.Recipe.Guided?.Loft is {} loft&&loft.Channels.Any(c=>c.Profiles.Any(ContainsContour))))
            throw new InvalidDataException("Contour authoring in legacy feature schema.");
        return bytes;
    }
}
