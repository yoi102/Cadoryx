using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

internal static class SketchExpressionCommands
{
    internal static SketchRegionEvaluationContext? Regions(DocumentCommandContext context,CadSketch sketch)=>sketch.RegionOffsets.IsEmpty?null:
        context.Kernel is IExactSketchExpressionRegions regions&&context.Kernel is ISketchExpressionRegionVerifier verifier?
            new(regions,verifier):throw new NotSupportedException("The active kernel cannot certify whole-region authors.");
    internal static CadSketch Refresh(DocumentCommandContext context,CadSketch sketch,CancellationToken token)=>
        Regions(context,sketch) is {} regions?regions.Refresh(sketch,token):SketchExpressionEntities.Refresh(sketch);
    internal static void Confirm(DocumentCommandContext context,CadSketch sketch,CancellationToken token)
    {
        if(sketch.ExpressionCurves.IsEmpty)return;
        if(context.Kernel is not IExactSketchExpressionGeometry exact)
            throw new NotSupportedException("The active kernel cannot certify exact expression entities.");
        foreach(var curve in sketch.ExpressionCurves)
        {token.ThrowIfCancellationRequested();exact.PrepareExpression(curve.Geometry);token.ThrowIfCancellationRequested();}
        if(Regions(context,sketch) is {} regions)regions.ValidateSolved(sketch,sketch,token);
    }
}
