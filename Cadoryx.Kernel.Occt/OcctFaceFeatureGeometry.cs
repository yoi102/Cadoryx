using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private static void RequireFaceFeatureSolid(Shape shape,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var topology=shape.GetTopologySummary();var validation=shape.GetValidationReport(geometryChecks:true,exact:true);
        var interference=shape.GetSelfInterferenceReport();double volume=AuthoringMeasure(shape,InspectionPropertyKind.Volume);
        if(shape.Kind!=ShapeKind.Solid||!validation.IsValid||!interference.IsClear||topology.UniqueCounts.SolidCount!=1||volume<=1e-9)
            throw new CadValidationException($"Face or solid offset authoring requires one exact valid solid without native self-interference. Kind={shape.Kind}; solids={topology.UniqueCounts.SolidCount}; exactValid={validation.IsValid}; selfClear={interference.IsClear}; volume={volume:R}.");
        token.ThrowIfCancellationRequested();
    }
    private static void ConfirmFaceFeatureMap(RepairSnapshot map,IEnumerable<ExactTopologySelection> faces)
    {
        foreach(var face in faces)
            if(face.AdapterVersion!=HistoryAdapterVersion||face.Fingerprint!=map.Fingerprint||
                face.FullTopologyIndex>=map.Topology.Count||map.Topology[face.FullTopologyIndex].Kind!=ShapeKind.Face)
                throw new CadValidationException("Selected face differs from its exact native source map. Reselect explicitly.");
    }
    private GeometryResult EvaluateShell(ShellRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        using var shape=OcctGeometryBridge.ReadShape(recipe.Source,assets);RequireFaceFeatureSolid(shape,token);
        using(var map=RepairSnapshot.Create(shape))ConfirmFaceFeatureMap(map,recipe.ClosingFaces);
        var faces=shape.GetFaces();
        try
        {
            var members=faces.ToDictionary(f=>RepairSnapshot.FindTopologyIndex(shape,f));
            var closing=recipe.ClosingFaces.Select(f=>members.GetValueOrDefault(f.FullTopologyIndex)??throw new CadValidationException("Closing face is not a native member of its selected source.")).ToArray();
            using var result=shape.MakeThickSolid(closing,recipe.Outward?recipe.Thickness:-recipe.Thickness);RequireFaceFeatureSolid(result,token);
            using var common=shape.Common(result);double overlap=AuthoringMeasure(common,InspectionPropertyKind.Volume),before=AuthoringMeasure(shape,InspectionPropertyKind.Volume),after=AuthoringMeasure(result,InspectionPropertyKind.Volume);
            double tolerance=Math.Max(1e-6,before*1e-7);
            if(before-overlap<=tolerance||(!recipe.Outward&&Math.Abs(after-overlap)>tolerance)||(recipe.Outward&&after-overlap<=tolerance))
                throw new CadValidationException("Shell did not remove source material or differs from its authored offset direction.");
            token.ThrowIfCancellationRequested();using var stored=OcctGeometryBridge.StoreShape(result,assets);
            return new(stored.Geometry,GeometryAssetLease.Acquire(stored.Geometry,assets),[new("HISTORY.UNSUPPORTED","Shell original-to-final topology history is unavailable; select final topology explicitly.")]);
        }
        finally{foreach(var face in faces)face.Dispose();}
    }
    private GeometryResult EvaluateDraft(DraftRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        using var shape=OcctGeometryBridge.ReadShape(recipe.Source,assets);RequireFaceFeatureSolid(shape,token);using var map=RepairSnapshot.Create(shape);ConfirmFaceFeatureMap(map,recipe.Programs.Select(p=>p.Face));
        var native=FaceDraftRecipe.Create(map,recipe.Programs.Select(p=>
        {
            var origin=p.NeutralPlane.Translation;var normal=p.NeutralPlane.Rotation.Rotate(Vector3d.UnitZ);var pull=p.PullDirection;
            return new FaceDraftProgram(map.Select(p.Face.FullTopologyIndex),p.Angle,new(pull.X,pull.Y,pull.Z),new(origin.X,origin.Y,origin.Z),new(normal.X,normal.Y,normal.Z),
                p.Propagation==FaceDraftPropagation.SelectedFaceOnly?DraftPropagation.SelectedFaceOnly:DraftPropagation.NativeTangentChain);
        }));
        using(var preflight=native.Preflight(map)){if(!preflight.Diagnostics.Ready)throw new CadValidationException("Draft preflight failed: "+preflight.Diagnostics.Message);}
        var result=native.Build(map);
        try
        {
            if(!result.Diagnostics.AlgorithmDone||!result.Diagnostics.ShapeIsValid)throw new CadValidationException("Draft did not complete: "+result.Diagnostics.Message);
            RequireFaceFeatureSolid(result.RequireShape(),token);return FinishLocal(recipe.Source,map,result,assets,token);
        }
        catch{result.Dispose();throw;}
    }
}
