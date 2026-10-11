using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private sealed class GuidedModelingRecorder(GeometryRecipe recipe,CancellationToken token)
    {
        private readonly List<GuidedModelingStage> stages=[];
        private bool metadataLimitExceeded;
        private string Hash(Shape shape)
        {
            token.ThrowIfCancellationRequested();using var files=new KernelFiles();string path=files.PathFor("observed.brep");
            ShapeExchange.WriteBrep(shape,path);using var input=File.OpenRead(path);
            string digest=Convert.ToHexStringLower(SHA256.HashData(input));token.ThrowIfCancellationRequested();return digest;
        }
        public void Record(GuidedModelingOperation operation,string scope,Guid? channel,int? station,
            IReadOnlyList<Shape> inputs,Shape output,AuthoringDiagnostics? native=null,double? tolerance=null,int? segments=null,int? degree=null,
            double? boundaryTolerance=null,double? angularTolerance=null,object? nativeOptions=null)
        {
            // A metadata limit must not shrink the already-supported geometry
            // contract or publish a partial report as complete evidence.
            if(metadataLimitExceeded||stages.Count>=GuidedModelingReport.MaximumStages)
            {metadataLimitExceeded=true;return;}
            var stage=new GuidedModelingStage(operation,scope,channel,station,inputs.Select(Hash).ToImmutableArray(),Hash(output),
                tolerance,boundaryTolerance,angularTolerance,segments,degree,native?.ApproximationError,native?.Ready,native?.AlgorithmDone,
                native?.ShapeIsValid,native?.AlgorithmStatus,native?.ContinuityLimit,native?.Message,
                nativeOptions is null?null:JsonSerializer.Serialize(nativeOptions,GuidedModelingReport.JsonOptions));
            stage.Validate();
            // 64 channels x 128 stations plus build stages fit the existing loft
            // contract. This bound limits copied report metadata, never solver size.
            stages.Add(stage);
        }
        public GuidedModelingReport? Complete(GeometryAssetRef result,string sdkVersion)
        {
            token.ThrowIfCancellationRequested();if(metadataLimitExceeded)return null;var runtime=OcctRuntime.Info;
            var report=new GuidedModelingReport(1,GuidedModelingReport.CurrentAdapterVersion,sdkVersion,runtime.ManagedVersion,
                runtime.BridgeVersion,runtime.AbiVersion.ToString(),runtime.OcctVersion,recipe,GuidedModelingReport.RecipeHash(recipe),
                result.Revision,result.AssetId,stages.ToImmutableArray());report.ValidateFor(result);return report;
        }
    }
    /// <summary>Explicit new complete-path authoring contract; legacy retained member tables keep their original meaning.</summary>
    public Task<GeometryResult> EvaluateGuidedForDocumentWithOccurrencesAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)
    {
        recipe.Validate();if(recipe is not (SweepRecipe or LoftRecipe))throw new CadValidationException("Complete guided occurrence authoring requires a sweep or loft recipe.");
        return Run(()=>EvaluateGuidedWithReport(recipe,assets,token,false,true),token);
    }
    private GeometryResult EvaluateGuidedWithReport(GeometryRecipe recipe,IAssetStore assets,CancellationToken token,bool retainReferences,bool retainOccurrences=false,bool preferOccurrences=false)
    {
        var recorder=new GuidedModelingRecorder(recipe,token);
        using var shape=recipe switch
        {
            SweepRecipe sweep=>BuildSweep(sweep,token,recorder),
            LoftRecipe loft=>BuildLoft(loft,token,recorder),
            _=>throw new CadValidationException("A guided modeling report requires an actual sweep or loft evaluation.")
        };
        token.ThrowIfCancellationRequested();using var stored=preferOccurrences?OcctGeometryBridge.StoreShapeWithPreferredOccurrences(shape,assets,token):retainOccurrences?OcctGeometryBridge.StoreShapeWithOccurrences(shape,assets,token):retainReferences?StoreGuidedReferences(shape,assets,token):OcctGeometryBridge.StoreShape(shape,assets);
        var report=recorder.Complete(stored.Geometry,Version);
        var note=report is null?new CadDiagnostic("MODELING.GUIDED_REPORT_LIMIT",
            "Generated geometry passed its existing checks; no complete modeling report was retained because the copied metadata budget was exceeded."):
            new CadDiagnostic("MODELING.GUIDED_REPORT","Recorded actual native controls, copied error estimates, as-built author recipe and source/result BRep hashes. " +
                "Estimates do not certify complete geometric equivalence, algebraic exactness or source-to-result topology identity.");
        return new GeometryResult(stored.Geometry,GeometryAssetLease.Acquire(stored.Geometry,assets),[..stored.Diagnostics,note]){GuidedModelingReport=report};
    }
    private static GeometryResult StoreGuidedReferences(Shape shape,IAssetStore assets,CancellationToken token)
    {
        // Reference budgets restrict evidence, never the supported modeling size.
        var counts=shape.GetTopologySummary().UniqueCounts;
        if((long)counts.EdgeCount+counts.FaceCount>BrepReferenceSnapshot.MaximumReferences)
        {
            using var ordinary=OcctGeometryBridge.StoreShape(shape,assets);
            return new(ordinary.Geometry,GeometryAssetLease.Acquire(ordinary.Geometry,assets),
                [new("MODELING.REFERENCE_LIMIT","Current result has no complete saved edge/face reference table; explicitly select its decoded graph.")]);
        }
        using var topology=shape.GetTopologyAdjacency(ShapeKind.Edge,ShapeKind.Face);
        var members=topology.Items.Concat(topology.Ancestors).ToArray();
        // Existing model contracts also allow large surface payloads with few faces.
        using var ordinarySize=OcctGeometryBridge.StoreShape(shape,assets);
        using var bytes=assets.Acquire(ordinarySize.Geometry.AssetId);
        if(bytes.Length>BrepReferenceSnapshot.MaximumBytes)
            return new(ordinarySize.Geometry,GeometryAssetLease.Acquire(ordinarySize.Geometry,assets),
                [new("MODELING.REFERENCE_LIMIT","Current BRep exceeds the saved reference byte budget; explicitly select its decoded graph.")]);
        token.ThrowIfCancellationRequested();var captured=BrepReferenceSnapshot.Capture(shape,members.Length==0?[shape]:members);
        return OcctGeometryBridge.StoreCapturedReferences(ordinarySize.Geometry,captured,assets,token);
    }
}
