using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private GeometryResult EvaluateSolidOffset(SolidOffsetRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        using var input=OcctGeometryBridge.ReadShape(recipe.Source,assets);using var source=CopyOffsetSolid(input,token);
        using var candidate=source.Offset(recipe.Distance);using var result=CopyOffsetSolid(candidate,token);
        double before=AuthoringMeasure(source,InspectionPropertyKind.Volume),after=AuthoringMeasure(result,InspectionPropertyKind.Volume);
        using var common=source.Common(result);double overlap=AuthoringMeasure(common,InspectionPropertyKind.Volume);
        double tolerance=Math.Max(1e-6,Math.Max(before,after)*1e-7);
        if(Math.Abs(after-before)<=tolerance||Math.Abs(overlap-Math.Min(before,after))>tolerance||
            recipe.Distance>0&&after<=before||recipe.Distance<0&&after>=before)
            throw new CadValidationException("Solid offset did not produce nested material in its authored direction.");
        token.ThrowIfCancellationRequested();using var stored=OcctGeometryBridge.StoreShape(result,assets);
        return new(stored.Geometry,GeometryAssetLease.Acquire(stored.Geometry,assets),
            [new("HISTORY.UNSUPPORTED","Solid offset original-to-final topology history is unavailable; select final topology explicitly."),
             new("STYLE.UNSUPPORTED","Body appearance and material are retained; source per-face styles cannot be proven across solid offset.")]);
    }
    private static Shape CopyOffsetSolid(Shape shape,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(shape.Kind==ShapeKind.Solid){RequireFaceFeatureSolid(shape,token);return shape.Clone();}
        var summary=shape.GetTopologySummary();
        if(shape.Kind!=ShapeKind.Compound||summary.UniqueCounts.SolidCount!=1||summary.OccurrenceCounts.SolidCount!=1||
            summary.UniqueCounts.CompSolidCount!=0||!shape.GetValidationReport(true,true).IsValid||!shape.GetSelfInterferenceReport().IsClear)
            throw new CadValidationException("Solid offset requires a single exact solid, optionally inside compound wrappers.");
        using var map=RepairSnapshot.Create(shape);var member=map.Topology.Single(p=>p.Kind==ShapeKind.Solid);
        var solid=map.CopySubshape(member.Selection);
        try
        {
            var child=solid.GetTopologySummary();
            // Removing only compound containers must preserve every lower topology kind and its occurrence count.
            // SDK occurrence summaries count the root a second time; source solid occurrence==1 is checked above.
            // This rejects loose faces/edges/vertices and repeated members instead of silently discarding them.
            if(summary.UniqueCounts with{CompoundCount=0}!=child.UniqueCounts with{CompoundCount=0}||
                summary.OccurrenceCounts with{CompoundCount=0,SolidCount=0}!=child.OccurrenceCounts with{CompoundCount=0,SolidCount=0})
                throw new CadValidationException($"Solid offset source contains topology outside its sole solid. SourceUnique={summary.UniqueCounts}; childUnique={child.UniqueCounts}; sourceOccurrences={summary.OccurrenceCounts}; childOccurrences={child.OccurrenceCounts}.");
            RequireFaceFeatureSolid(solid,token);return solid;
        }
        catch{solid.Dispose();throw;}
    }
}
