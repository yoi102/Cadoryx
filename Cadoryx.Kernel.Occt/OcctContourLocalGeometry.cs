using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;
using LocalOperation=Cadoryx.Db.LocalFeatureOperation;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:IContourLocalResolver
{
    public async Task ConfirmContourRecipeAsync(ContourLocalRecipe recipe,IAssetStore assets,CancellationToken cancellationToken=default)
    {
        recipe.Validate();
        await Run(()=>
        {
            using var shape=OcctGeometryBridge.ReadShape(recipe.Source,assets);using var accepted=CopyOffsetSolid(shape,cancellationToken);
            using var map=RepairSnapshot.Create(shape);
            foreach(var p in recipe.Programs){ConfirmContourSelection(map,p.Seed);if(p.SupportFace is {} face)ConfirmContourSelection(map,face);}
            using var discovered=recipe.Operation==LocalOperation.Fillet?
                ContourFilletRecipe.Create(map,recipe.Programs.Select(p=>NativeContourRadius(map.Select(p.Seed.FullTopologyIndex),p.Radius!)).ToArray()).Discover(map):
                ContourChamferRecipe.Create(map,recipe.Programs.Select(p=>p.SecondDistance is {} second?
                    new ChamferContourProgram(map.Select(p.Seed.FullTopologyIndex),map.Select(p.SupportFace!.FullTopologyIndex),ChamferDimensions.TwoDistances,p.Distance!.Value,second):
                    new ChamferContourProgram(map.Select(p.Seed.FullTopologyIndex),map.Select(p.SupportFace!.FullTopologyIndex),ChamferDimensions.Symmetric,p.Distance!.Value)).ToArray()).Discover(map);
            ConfirmContourChains(recipe,discovered);return true;
        },cancellationToken).ConfigureAwait(false);
    }
    public async Task<ContourChainEvidence> DiscoverContourAsync(DocumentSnapshot snapshot,ExactTopologySelection seed,
        LocalOperation operation,ExactTopologySelection? supportFace,IAssetStore assets,CancellationToken cancellationToken=default)
    {
        if(!Enum.IsDefined(operation)||seed.Kind!=HistoryShapeKind.Edge)throw new CadValidationException("Choose a contour operation and exact edge.");
        await ConfirmAsync(snapshot,seed,assets,cancellationToken).ConfigureAwait(false);
        if(snapshot.Features[seed.FeatureId].IsSuppressed)throw new CadValidationException("Restore the suppressed contour source first.");
        if(operation==LocalOperation.Chamfer)
        {
            if(supportFace is null||supportFace.Kind!=HistoryShapeKind.Face||!SameContourSource(seed,supportFace))throw new CadValidationException("Choose a support face on the same exact source.");
            await ConfirmAsync(snapshot,supportFace,assets,cancellationToken).ConfigureAwait(false);
        }
        else if(supportFace is not null)throw new CadValidationException("Fillet discovery does not use a support face.");
        return await Run(()=>
        {
            using var shape=OcctGeometryBridge.ReadShape(snapshot.Features[seed.FeatureId].Result,assets);
            using var accepted=CopyOffsetSolid(shape,cancellationToken);using var map=RepairSnapshot.Create(shape);
            ConfirmContourSelection(map,seed);
            using var discovery=operation==LocalOperation.Fillet?
                ContourFilletRecipe.Create(map,[FilletContourProgram.Constant(map.Select(seed.FullTopologyIndex),1)]).Discover(map):
                ContourChamferRecipe.Create(map,[new(map.Select(seed.FullTopologyIndex),map.Select(supportFace!.FullTopologyIndex),ChamferDimensions.Symmetric,1)]).Discover(map);
            if(!discovery.Diagnostics.Ready||discovery.Contours.Count!=1)throw new CadValidationException("Native contour discovery failed: "+discovery.Diagnostics.Message);
            var result=CopyContourChain(discovery,discovery.Contours[0]);result.Validate(seed.FullTopologyIndex);return result;
        },cancellationToken).ConfigureAwait(false);
    }
    private GeometryResult EvaluateContourLocal(ContourLocalRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        using var shape=OcctGeometryBridge.ReadShape(recipe.Source,assets);using var accepted=CopyOffsetSolid(shape,token);
        using var map=RepairSnapshot.Create(shape);
        foreach(var p in recipe.Programs){ConfirmContourSelection(map,p.Seed);if(p.SupportFace is {} face)ConfirmContourSelection(map,face);}
        if(recipe.Operation==LocalOperation.Fillet)
        {
            var native=ContourFilletRecipe.Create(map,recipe.Programs.Select(p=>NativeContourRadius(map.Select(p.Seed.FullTopologyIndex),p.Radius!)).ToArray());
            using(var discovered=native.Discover(map))ConfirmContourChains(recipe,discovered);
            var built=native.Build(map);return FinishAcceptedContour(recipe.Source,map,built,assets,token);
        }
        else
        {
            var native=ContourChamferRecipe.Create(map,recipe.Programs.Select(p=>p.SecondDistance is {} second?
                new ChamferContourProgram(map.Select(p.Seed.FullTopologyIndex),map.Select(p.SupportFace!.FullTopologyIndex),ChamferDimensions.TwoDistances,p.Distance!.Value,second):
                new ChamferContourProgram(map.Select(p.Seed.FullTopologyIndex),map.Select(p.SupportFace!.FullTopologyIndex),ChamferDimensions.Symmetric,p.Distance!.Value)).ToArray());
            using(var discovered=native.Discover(map))ConfirmContourChains(recipe,discovered);
            var built=native.Build(map);return FinishAcceptedContour(recipe.Source,map,built,assets,token);
        }
    }
    private GeometryResult FinishAcceptedContour(GeometryAssetRef source,RepairSnapshot map,LocalFeatureResult built,IAssetStore assets,CancellationToken token)
    {
        try{using var valid=CopyOffsetSolid(built.RequireShape(),token);}
        catch{built.Dispose();throw;}
        return FinishLocal(source,map,built,assets,token);
    }
    private static FilletContourProgram NativeContourRadius(RepairSelection seed,ContourRadiusLaw law)=>law switch
    {
        ConstantContourRadius r=>FilletContourProgram.Constant(seed,r.Value),
        LinearContourRadius r=>FilletContourProgram.FromLaw(seed,ScalarLawDefinition.Linear(new(0,1),r.First,r.Last)),
        SmoothContourRadius r=>FilletContourProgram.FromLaw(seed,ScalarLawDefinition.Smooth(new(0,1),r.First,r.Last,r.FirstDerivative,r.LastDerivative)),
        SampledContourRadius r=>FilletContourProgram.Sampled(seed,r.Samples.Select(s=>new FilletRadiusSample(s.Parameter,s.Radius))),
        SplineContourRadius r=>FilletContourProgram.FromLaw(seed,ScalarLawDefinition.BSpline(r.Poles.Select(p=>p.Radius),r.Knots,r.Multiplicities,r.Degree)),
        _=>throw new CadValidationException("Unknown contour radius law.")
    };
    private static bool SameContourSource(ExactTopologySelection a,ExactTopologySelection b)=>a.DocumentId==b.DocumentId&&a.FeatureId==b.FeatureId&&a.Revision==b.Revision&&a.Asset==b.Asset&&a.Fingerprint==b.Fingerprint&&a.AdapterVersion==b.AdapterVersion;
    private static void ConfirmContourSelection(RepairSnapshot map,ExactTopologySelection selected)
    {
        if(selected.AdapterVersion!=HistoryAdapterVersion||selected.Fingerprint!=map.Fingerprint||selected.FullTopologyIndex>=map.Topology.Count||
            map.Topology[selected.FullTopologyIndex].Kind!=(selected.Kind==HistoryShapeKind.Edge?ShapeKind.Edge:ShapeKind.Face))
            throw new CadValidationException("Contour selection differs from the exact native source. Reselect explicitly.");
    }
    private static ContourChainEvidence CopyContourChain(LocalFeatureResult discovered,LocalFeatureContour contour)
    {
        var edges=discovered.ContourEdges.Where(e=>e.ContourIndex==contour.Index).OrderBy(e=>e.Ordinal).ToArray();
        if(edges.Length is <1 or >4096||!edges.Select(e=>e.Ordinal).SequenceEqual(Enumerable.Range(0,edges.Length)))
            throw new CadValidationException("Native contour ordinals are not complete and bounded.");
        return new(edges.Select(e=>new ContourChainMember(e.Edge.Index,e.FirstVertex?.Index,e.LastVertex?.Index,e.FirstParameter,e.LastParameter)).ToImmutableArray(),
            contour.FirstVertex?.Index,contour.LastVertex?.Index,contour.IsClosed,contour.IsClosedAndTangent,contour.Length);
    }
    private static void ConfirmContourChains(ContourLocalRecipe recipe,LocalFeatureResult discovered)
    {
        if(!discovered.Diagnostics.Ready||discovered.Contours.Count!=recipe.Programs.Length||discovered.Contours.Any(c=>c.ProgramIndex<0||c.ProgramIndex>=recipe.Programs.Length)||discovered.Contours.Select(c=>c.ProgramIndex).Distinct().Count()!=recipe.Programs.Length)
            throw new CadValidationException("Native contour assignment is not complete and unique.");
        for(int i=0;i<recipe.Programs.Length;i++)
        {
            var program=recipe.Programs[i];var contour=discovered.Contours.Single(c=>c.ProgramIndex==i);var actual=CopyContourChain(discovered,contour);var expected=program.Chain;
            if(contour.Seed.Index!=program.Seed.FullTopologyIndex||actual.FirstVertex!=expected.FirstVertex||actual.LastVertex!=expected.LastVertex||
                actual.IsClosed!=expected.IsClosed||actual.IsClosedAndTangent!=expected.IsClosedAndTangent||actual.Length!=expected.Length||!actual.Members.SequenceEqual(expected.Members))
                throw new CadValidationException("Native contour membership/order/direction differs from the reviewed evidence. Rediscover and reselect explicitly.");
        }
    }
}
