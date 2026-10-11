using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.IO;

public sealed record ExchangeComparison(string Aspect,string Status,string Note,double? MaximumDifference=null);
public sealed record ExchangeMeasuredFormat(string Format,string Entry,long Bytes,string Sha256,
    ExchangeModelObservation Reopened,ImmutableArray<CadDiagnostic> ExportDiagnostics,ImmutableArray<CadDiagnostic> ImportDiagnostics,
    ImmutableArray<ExchangeComparison> Comparisons);
public sealed record ExchangeMeasuredReport(int SchemaVersion,string DocumentId,string StateId,DateTimeOffset RecordedUtc,
    string Kernel,string Basis,DeliveryOptions Options,ImmutableArray<string> SourceAssetHashes,ExchangeModelObservation Source,ImmutableArray<ExchangeMeasuredFormat> Formats,
    ImmutableArray<string> NotMeasured)
{
    public const string LocalBasis="Cadoryx export and Cadoryx/OCCT re-import; no external CAD result is implied";
    public static ImmutableArray<ExchangeComparison> Compare(ExchangeModelObservation source,ExchangeModelObservation reopened,string format,CadExportOptions options)
    {
        var rows=ImmutableArray.CreateBuilder<ExchangeComparison>();
        void Count(string name,bool same,string note)=>rows.Add(new(name,same?"Same":"Different",note));
        double delta=new[]{Math.Abs(source.BoundsMm.Min.X-reopened.BoundsMm.Min.X),Math.Abs(source.BoundsMm.Min.Y-reopened.BoundsMm.Min.Y),
            Math.Abs(source.BoundsMm.Min.Z-reopened.BoundsMm.Min.Z),Math.Abs(source.BoundsMm.Max.X-reopened.BoundsMm.Max.X),
            Math.Abs(source.BoundsMm.Max.Y-reopened.BoundsMm.Max.Y),Math.Abs(source.BoundsMm.Max.Z-reopened.BoundsMm.Max.Z)}.Max();
        double boundsTolerance=format=="STL"?Math.Max(.00001,options.LinearDeflectionMm):.00001;
        rows.Add(new("WorldBoundsMm",delta<=boundsTolerance?"WithinTolerance":"Different",
            $"Maximum coordinate difference; tolerance {boundsTolerance:R} mm. Bounds do not prove complete geometric equivalence or maximum mesh error.",delta));
        double areaDelta=Math.Abs(source.SurfaceAreaMm2-reopened.SurfaceAreaMm2);
        rows.Add(new("SurfaceAreaMm2",areaDelta<=Math.Max(.00001,source.SurfaceAreaMm2*1e-6)?"WithinTolerance":"Different",
            "Sum counts each instance independently; no overlap subtraction. Mesh area can change with tessellation.",areaDelta));
        if(source.OtherBodies==0&&reopened.OtherBodies==0)
        {
            double volumeDelta=Math.Abs(source.KnownSolidVolumeMm3-reopened.KnownSolidVolumeMm3);
            rows.Add(new("SolidVolumeMm3",volumeDelta<=Math.Max(.00001,source.KnownSolidVolumeMm3*1e-6)?"WithinTolerance":"Different",
                "Native volume of single-solid bodies, summed per instance; no geometric equivalence claim.",volumeDelta));
        }
        else rows.Add(new("SolidVolumeMm3","NotComparable","One or both observations contain sheets, meshes, wires or mixed compounds; no inferred closed-solid volume."));
        Count("BodyInstances",source.BodyInstances==reopened.BodyInstances,"Observed body instances; flattening can change this count.");
        Count("PartDefinitionSharing",source.PartDefinitions==reopened.PartDefinitions&&source.PartReuseCounts.SequenceEqual(reopened.PartReuseCounts),
            "Sorted definition reuse counts; matching counts do not prove that the same parts share identity.");
        var sourceNames=source.Occurrences.Select(o=>(o.Name,o.DefinitionName,o.Assembly,o.Depth)).ToArray();
        var resultNames=reopened.Occurrences.Select(o=>(o.Name,o.DefinitionName,o.Assembly,o.Depth)).ToArray();
        Count("HierarchyAndNames",sourceNames.SequenceEqual(resultNames),"Ordered occurrence depth, names and assembly/part kinds; reader-created wrapper nodes are included.");
        Count("EffectiveColorPalette",source.EffectiveColorPalette.SequenceEqual(reopened.EffectiveColorPalette),
            "Observed effective RGB palette; matching colors do not prove per-face assignment, transparency or full style retention.");
        rows.Add(new("PMI","NotMeasured","No source PMI inventory or semantic PMI transfer proof is available."));
        rows.Add(new("IntrinsicUnitDeclaration",format=="STL"?"Absent":"NotMeasured",
            format=="STL"?"STL has no intrinsic unit declaration; coordinates are explicitly interpreted as millimeters.":
                "Reader geometry is in millimeters; unit declaration itself is not parsed by this measurement. Independent inch/meter fixtures are separate evidence."));
        return rows.ToImmutable();
    }
}
