using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Kernel.Abstractions;

/// <summary>One caller/solve owns this cache. Keys contain the complete source
/// author values and recipe; generated endpoint coordinates are not cache keys.
/// Each group retains only its latest result, within the existing group budget.</summary>
public sealed class SketchRegionEvaluationContext
{
    private readonly IExactSketchExpressionRegions provider;
    private readonly ISketchExpressionRegionVerifier verifier;
    private readonly Dictionary<Guid,(SketchRegionOffsetAuthor Input,ExactExpressionRegionOffset Output)> cache=[];
    public int NativeEvaluations {get;private set;}
    public SketchRegionEvaluationContext(IExactSketchExpressionRegions provider,ISketchExpressionRegionVerifier? verifier=null)
    {
        this.provider=provider??throw new ArgumentNullException(nameof(provider));
        this.verifier=verifier??provider as ISketchExpressionRegionVerifier??
            throw new NotSupportedException("Whole-region evaluation requires complete exact replay verification.");
    }

    public CadSketch Refresh(CadSketch sketch,CancellationToken cancellationToken=default,bool synchronizeGeneratedPoints=false,bool verify=true,IReadOnlyDictionary<Guid,double>? distanceOverrides=null)
        =>RefreshCore(sketch,cancellationToken,synchronizeGeneratedPoints,verify,distanceOverrides,null);

    /// <summary>Incomplete equation-only candidate. Deferred groups retain their
    /// immutable author metadata; generated points remain independent variables.
    /// A caller must own the differential/dependency proof and must never publish
    /// this value. Final commands still use full Refresh and ValidateSolved.</summary>
    public CadSketch RefreshForLocalEquations(CadSketch sketch,IReadOnlySet<Guid> deferredGroups,CancellationToken cancellationToken=default)
    {
        ArgumentNullException.ThrowIfNull(deferredGroups);
        foreach(var id in deferredGroups)
            if(sketch.RegionOffsets.SingleOrDefault(g=>g.Id==id) is not {IsFrozen:false})
                throw new CadValidationException("Deferred equation metadata needs its current complete active group.");
        return RefreshCore(sketch,cancellationToken,false,false,null,deferredGroups);
    }

    private CadSketch RefreshCore(CadSketch sketch,CancellationToken cancellationToken,bool synchronizeGeneratedPoints,bool verify,
        IReadOnlyDictionary<Guid,double>? distanceOverrides,IReadOnlySet<Guid>? deferredGroups)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(distanceOverrides is not null)
            foreach(var(id,value) in distanceOverrides)
            {
                cancellationToken.ThrowIfCancellationRequested();CadGuard.Finite(value);
                if(Math.Abs(value)<=1e-7||sketch.RegionOffsets.SingleOrDefault(g=>g.Id==id) is not {IsFrozen:false})
                    throw new CadValidationException("A staged region distance needs its current unfrozen group and a finite nonzero value.");
            }
        if(sketch.RegionOffsets.IsEmpty)return SketchExpressionEntities.Refresh(sketch);
        var order=SketchRegionAuthorModel.OrderForRefresh(sketch);var candidate=sketch;
        foreach(var node in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(!node.IsGroup)
            {
                var id=new SketchEntityId(node.Id);candidate=SketchExpressionEntities.RefreshAuthor(candidate,id);
                if(synchronizeGeneratedPoints)candidate=Synchronize(candidate,[candidate.ExpressionCurves.Single(e=>e.Id==id)]);
                continue;
            }
            var original=candidate.RegionOffsets.Single(g=>g.Id==node.Id);
            if(original.IsFrozen)
            {
                if(synchronizeGeneratedPoints)candidate=Synchronize(candidate,OwnedCurves(candidate,original));
                continue;
            }
            if(deferredGroups?.Contains(original.Id)==true)continue;
            var sources=original.Sources.Select(l=>l.Select(s=>
            {
                var author=SketchExpressionEntities.Author(candidate,s.Entity);
                if(author.Kind!=s.Kind||!SketchExpressionEntities.SameFamily(s.Geometry,author.Geometry))
                    throw new CadValidationException("Whole-region source changed family; explicitly reselect it.");
                return s with{Geometry=author.Geometry};
            }).ToImmutableArray()).ToImmutableArray();
            var intent=distanceOverrides is not null&&distanceOverrides.TryGetValue(original.Id,out double distance)?original with{Distance=distance}:original;
            var input=intent with{Sources=sources};ExactExpressionRegionOffset output;
            if(cache.TryGetValue(original.Id,out var entry)&&SameInputs(entry.Input,input))output=entry.Output;
            else
            {
                var nativeSources=sources.Select(l=>l.Select(s=>new ExactExpressionRegionSource(s.Entity,s.Geometry,s.Reversed)).ToImmutableArray()).ToImmutableArray();
                output=provider.OffsetExpressionRegions(nativeSources,input.Distance,(ExactExpressionRegionJoin)input.Join);
                cancellationToken.ThrowIfCancellationRequested();NativeEvaluations++;
                // Cache only after the complete correspondence has been checked.
                _=SketchRegionOutputCorrespondence.ReplaceDerivedValues(intent,sources,output);
                cache[original.Id]=(input,output);
            }
            var updated=SketchRegionOutputCorrespondence.ReplaceDerivedValues(intent,sources,output);
            var outputs=updated.Loops.SelectMany(l=>l.Outputs).ToDictionary(o=>o.Curve);
            candidate=candidate with{RegionOffsets=candidate.RegionOffsets.Replace(original,updated),
                ExpressionCurves=candidate.ExpressionCurves.Select(c=>outputs.TryGetValue(c.Id,out var o)?c with{Geometry=o.Geometry}:c).ToImmutableArray()};
            if(synchronizeGeneratedPoints)candidate=Synchronize(candidate,OwnedCurves(candidate,updated));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if(verify){candidate.Validate();verifier.ConfirmExpressionRegions(candidate,cancellationToken);}
        return candidate;
    }

    /// <summary>Independently derives the returned coordinates from the original
    /// recipe. A solver cannot replace distance, sources, ownership or output IDs.</summary>
    public void ValidateSolved(CadSketch authored,CadSketch solved,CancellationToken cancellationToken=default)
    {
        var expected=Refresh(authored with{Points=solved.Points,Circles=solved.Circles},cancellationToken,verify:false);
        if(!SketchRegionAuthorModel.SameTable(expected,solved)||!SketchExpressionEntities.SameTable(expected,solved))
            throw new CadValidationException("Solver changed complete region intent, correspondence or derived values.");
        solved.Validate();verifier.ConfirmExpressionRegions(solved,cancellationToken);
    }

    public CadSketch ChangeDistance(CadSketch sketch,Guid id,double distance,CancellationToken cancellationToken=default)
    {
        sketch.Validate();return Refresh(sketch,cancellationToken,synchronizeGeneratedPoints:true,distanceOverrides:new Dictionary<Guid,double>{{id,distance}});
    }

    private static bool SameInputs(SketchRegionOffsetAuthor a,SketchRegionOffsetAuthor b)
    {
        if(a.Id!=b.Id||a.Distance!=b.Distance||a.Join!=b.Join||a.Algorithm!=b.Algorithm||a.Sources.Length!=b.Sources.Length||a.Loops.Length!=b.Loops.Length)return false;
        for(int i=0;i<a.Sources.Length;i++)
        {
            if(a.Sources[i].Length!=b.Sources[i].Length||!SketchRegionJointAuthors.SameStructure(a.Loops[i].Joints,b.Loops[i].Joints))return false;
            for(int j=0;j<a.Sources[i].Length;j++)
            {
                var x=a.Sources[i][j];var y=b.Sources[i][j];
                if(x.Entity!=y.Entity||x.Kind!=y.Kind||x.Reversed!=y.Reversed||!x.Geometry.SameValues(y.Geometry))return false;
            }
        }
        return true;
    }
    private static IEnumerable<SketchExpressionCurve> OwnedCurves(CadSketch sketch,SketchRegionOffsetAuthor group)
    {var ids=group.Loops.SelectMany(l=>l.Outputs).Select(o=>o.Curve).ToHashSet();return sketch.ExpressionCurves.Where(c=>ids.Contains(c.Id));}
    private static CadSketch Synchronize(CadSketch sketch,IEnumerable<SketchExpressionCurve> curves)
    {
        var positions=new Dictionary<SketchEntityId,Point2d>();
        foreach(var c in curves.Where(c=>c.Endpoints is not null))
        {
            Add(c.Endpoints!.Start,c.Geometry.At(0));Add(c.Endpoints.End,c.Geometry.At(1));
        }
        return sketch with{Points=sketch.Points.Select(p=>p.Origin==SketchPointOrigin.CurveGenerated&&positions.TryGetValue(p.Id,out var value)?p with{Position=value}:p).ToImmutableArray()};
        void Add(SketchEntityId id,Point2d value)
        {
            if(positions.TryGetValue(id,out var prior)&&double.Hypot(prior.X-value.X,prior.Y-value.Y)>1e-7)
                throw new CadValidationException("Whole-region generated endpoint providers disagree.");
            positions.TryAdd(id,value);
        }
    }
}
