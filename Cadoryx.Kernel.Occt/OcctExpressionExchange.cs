using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private static IEnumerable<SketchCurveExpressionGeometry> ProfileExpressions(SketchProfile profile)=>
        profile.BoundaryCurves.Concat(profile.MixedHoles.SelectMany(h=>h)).Where(c=>c.Expression is not null).Select(c=>c.Expression!)
            .Concat(profile.Islands.SelectMany(i=>ProfileExpressions(i.Profile)));
    private static IEnumerable<SketchCurveExpressionGeometry> RecipeExpressions(GeometryRecipe recipe)=>recipe switch
    {
        ExtrudeRecipe e=>ProfileExpressions(e.Profile),RevolveRecipe r=>ProfileExpressions(r.Profile),
        SweepRecipe s=>ProfileExpressions(s.Section.Profile).Concat(s.Path.Curves.Where(c=>c.Expression is not null).Select(c=>c.Expression!)),
        LoftRecipe l=>l.Channels.SelectMany(c=>c.Profiles.SelectMany(ProfileExpressions)),
        ExtrusionPatternRecipe p=>RecipeExpressions(p.Template),_=>[]
    };
    private static bool HasExpressionIntent(DocumentSnapshot snapshot)=>snapshot.Sketches.Values.Any(s=>!s.ExpressionCurves.IsEmpty)||
        snapshot.Features.Values.Any(f=>RecipeExpressions(f.Recipe).Any());
    private static bool HasOffsetOperation(SketchCurveExpressionGeometry expression)
    {for(var node=expression;node is not null;node=node.Basis)if(node.Kind==Cadoryx.Db.SketchCurveExpressionKind.Offset)return true;return false;}
    private static CadDiagnostic ExpressionExchangeLoss()=>new("EXPORT.EXPRESSION_AUTHOR_VALUES",
        "Exchange does not store the Cadoryx primitive/offset/continuation author tree, signed-distance operations, native author intervals or source dependencies. " +
        "Actual exchange geometry may use another representation; save Cadoryx to retain these values. No curve fitting is performed by this export path.");

    private static void DescribeGuidedExchange(DocumentSnapshot snapshot,CadBody body,
        ImmutableArray<CadDiagnostic>.Builder diagnostics)
    {
        if(body.Producer is not {} producer||!snapshot.Features.TryGetValue(producer,out var feature)||feature.Result!=body.Geometry)return;
        if(feature.GuidedModelingReport is {} report)
        {
            report.ValidateFor(body.Geometry);
            string Estimate(GuidedModelingOperation operation)
            {
                var values=report.Stages.Where(s=>s.Operation==operation&&s.NativeApproximationErrorEstimate is not null)
                    .Select(s=>s.NativeApproximationErrorEstimate!.Value).ToArray();
                return values.Length==0?"not reported":values.Max().ToString("R",System.Globalization.CultureInfo.InvariantCulture);
            }
            diagnostics.Add(new("EXPORT.GUIDED_MODELING_OBSERVATIONS",
                $"Body {body.Id}: stored as-built modeling observations for asset {report.ResultAsset}, revision {report.ResultRevision}; " +
                $"SDK {report.SdkVersion}, native bridge {report.NativeBridgeVersion}, ABI {report.NativeAbiVersion}, OCCT {report.OcctVersion}; " +
                $"stages {report.Stages.Length}, as-built recipe hash {report.BuiltRecipeSha256}. " +
                $"Maximum reported native curve-preparation estimate: {Estimate(GuidedModelingOperation.CurvePreparation)} mm; " +
                $"sweep estimate: {Estimate(GuidedModelingOperation.Sweep)} mm; loft estimate: {Estimate(GuidedModelingOperation.Loft)} mm. " +
                "Missing estimates remain unknown. These observations are not a maximum total modeling-error certificate or topology identity proof. " +
                "Exchange geometry uses the generated BRep; STEP/IGES does not transfer this report or editable author semantics. Save Cadoryx to retain them."));
        }
        bool prepared=feature.Recipe switch
        {
            SweepRecipe sweep=>!IsPrismSweep(sweep),
            LoftRecipe loft=>loft.Channels.Any(channel=>!IsPrismLoftChannel(loft,channel)),
            _=>false
        };
        if(prepared)diagnostics.Add(new("EXPORT.GUIDED_GENERATED_BREP",
            $"Body {body.Id}: exchange uses the stored generated guided BRep. This recipe takes the native curve-preparation branch " +
            "(current curve tolerance 1e-10 mm, maximum 64 segments, degree 11) and may contain modeling approximations; " +
            "these are not an algebraically exact representation of all author curves. No per-result maximum total approximation-bound or topology-history certificate is stored, " +
            "so current recipe policy does not certify a previously saved result's modeling error. Export performs no additional curve fitting; " +
            "measured re-import differences compare with the generated source BRep, not the exact author expression. Save Cadoryx to retain author values."));
    }

    private static Shape ExchangeExpressionShape(DocumentSnapshot snapshot,CadBody body,IAssetStore assets,
        ImmutableArray<CadDiagnostic>.Builder diagnostics,CancellationToken token)
    {
        DescribeGuidedExchange(snapshot,body,diagnostics);
        // Rebuilding is limited to the exact live producer and an unstyled author result.
        // In particular, there is no guessed face correspondence for imported subshape colors.
        if(body.Geometry.Source is not null||body.Producer is not {} producer||!snapshot.Features.TryGetValue(producer,out var feature)||
            feature.Result!=body.Geometry||feature.Recipe is not (ExtrudeRecipe {Options:null} or RevolveRecipe))
            return OcctGeometryBridge.ReadShape(body.Geometry,assets);
        var expressions=RecipeExpressions(feature.Recipe).ToArray();
        if(expressions.Length==0)return OcctGeometryBridge.ReadShape(body.Geometry,assets);
        var kernel=new OcctGeometryKernel();
        int canonicalCount=0;
        foreach(var expression in expressions)
        {
            token.ThrowIfCancellationRequested();kernel.PrepareExpression(expression);
            if(CanonicalExpressionCurve(expression) is not null){canonicalCount++;continue;}
            if(HasOffsetOperation(expression))
            {
                var error=new CadValidationException("This expression profile has no proven exact circle/line exchange representation. " +
                    "Save Cadoryx to retain the exact offset; this export does not approximate curves.");
                error.Data["CadDiagnostic.Code"]="EXPORT.EXPRESSION_UNREPRESENTABLE";error.Data["Body.Id"]=body.Id.ToString();throw error;
            }
        }
        if(canonicalCount==0)return OcctGeometryBridge.ReadShape(body.Geometry,assets);
        using var original=OcctGeometryBridge.ReadShape(body.Geometry,assets);
        var converted=Create(feature.Recipe,assets,token,canonicalExpressions:true);
        try
        {
            RequireClear(converted,token);
            if(original.GetTopologySummary().UniqueCounts.SolidCount!=converted.GetTopologySummary().UniqueCounts.SolidCount)
                throw new CadValidationException("Exact expression exchange changed the number of material regions.");
            var before=original.GetBoundingBox();var after=converted.GetBoundingBox();
            double volumeError=Math.Abs(AuthoringMeasure(original,InspectionPropertyKind.Volume)-AuthoringMeasure(converted,InspectionPropertyKind.Volume));
            double areaError=Math.Abs(AuthoringMeasure(original,InspectionPropertyKind.Area)-AuthoringMeasure(converted,InspectionPropertyKind.Area));
            double boundsError=new[]{Math.Abs(before.Minimum.X-after.Minimum.X),Math.Abs(before.Minimum.Y-after.Minimum.Y),Math.Abs(before.Minimum.Z-after.Minimum.Z),
                Math.Abs(before.Maximum.X-after.Maximum.X),Math.Abs(before.Maximum.Y-after.Maximum.Y),Math.Abs(before.Maximum.Z-after.Maximum.Z)}.Max();
            if(!double.IsFinite(volumeError)||!double.IsFinite(areaError)||!double.IsFinite(boundsError)||volumeError>1e-7||areaError>1e-7||boundsError>1e-7)
                throw new CadValidationException("Exact expression exchange failed the unchanged 1e-7 geometry consistency check.");
            diagnostics.Add(new("EXPORT.EXPRESSION_EXACT_CANONICALIZATION",
                $"Body {body.Id}: {canonicalCount} author expressions rebuilt as algebraically identical circles/lines in a temporary exchange BRep. " +
                $"No fitting or source-subshape remapping; original author tree and assets unchanged. Volume/area/bounds discrepancies: {volumeError:R}/{areaError:R}/{boundsError:R}."));
            return converted;
        }
        catch{converted.Dispose();throw;}
    }
}
