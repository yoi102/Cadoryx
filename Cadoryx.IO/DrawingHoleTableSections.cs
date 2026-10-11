using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject] public sealed record PackDrawingHoleEvidence(
    [property:Key(0)] PackDrawingAttachment Source,[property:Key(1)] double[] Entry,[property:Key(2)] double[] Direction,
    [property:Key(3)] double Diameter,[property:Key(4)] bool Through,[property:Key(5)] double Depth,[property:Key(6)] int Head,
    [property:Key(7)] double HeadDiameter,[property:Key(8)] double HeadDepth,[property:Key(9)] double HeadAngle,
    [property:Key(10)] int Specification,[property:Key(11)] Guid? Author,[property:Key(12)] int[]? Standard,[property:Key(13)] int[] Faces,
    [property:Key(14)] PackMetricThread? Thread=null);
[MessagePackObject] public sealed record PackDrawingHoleRow([property:Key(0)] Guid Id,[property:Key(1)] int Number,
    [property:Key(2)] PackDrawingHoleEvidence Evidence,[property:Key(3)] double[] Coordinates,[property:Key(4)] string? Stale);
[MessagePackObject] public sealed record PackDrawingHoleCoordinates([property:Key(0)] Guid Plane,[property:Key(1)] Guid? Origin,
    [property:Key(2)] Guid? XAxis,[property:Key(3)] bool ReverseX);
[MessagePackObject] public sealed record PackDrawingHoleTable([property:Key(0)] Guid Id,[property:Key(1)] Guid View,
    [property:Key(2)] string Name,[property:Key(3)] Guid[] Slots,[property:Key(4)] Guid Definition,[property:Key(5)] PackTransform Frame,
    [property:Key(6)] double[] Position,[property:Key(7)] PackDrawingHoleRow[] Rows,[property:Key(8)] string? Stale,
    [property:Key(9)] PackDrawingHoleCoordinates? Coordinates=null,[property:Key(10)] Guid[]? CoordinateRevisions=null);

internal static partial class MessagePackSections
{
    private static PackDrawingHoleEvidence Hole(DrawingHoleEvidence e)=>new(Attachment(e.Source),DrawingPoint(e.Entry),DrawingPoint(e.Direction),
        e.DiameterMm,e.Through,e.DepthMm,(int)e.Head,e.HeadDiameterMm,e.HeadDepthMm,e.HeadAngle,(int)e.SpecificationSource,
        e.AuthoredFeature?.Value,e.Standard is {} spec?[spec.NominalMm,(int)spec.Fit]:null,e.ProvenFaceIndices.ToArray(),Thread(e.Thread));
    private static DrawingHoleEvidence Hole(PackDrawingHoleEvidence e,DocumentId document)
    {
        if(e is null||e.Faces is not {Length:>=3 and <=4096}||
            e.Thread?.Representation!=(int)ThreadHoleRepresentation.Modeled&&e.Faces.Length>8||e.Standard is not null&&e.Standard.Length!=2)
            throw new InvalidDataException("Malformed current hole evidence.");
        return new(Attachment(e.Source,document),DrawingVector(e.Entry),DrawingVector(e.Direction),e.Diameter,e.Through,e.Depth,
            (HoleHead)e.Head,e.HeadDiameter,e.HeadDepth,e.HeadAngle,(DrawingHoleSpecificationSource)e.Specification,
            e.Author is {} author?new FeatureId(author):null,e.Standard is {} s?new(s[0],(ClearanceFit)s[1]):null,Thread(e.Thread)){ProvenFaceIndices=[..e.Faces]};
    }
    private static PackDrawingHoleTable HoleTable(DrawingHoleTable t)=>new(t.Id,t.ViewId,t.Name,t.CoordinatePath.Slots.Select(s=>s.Value).ToArray(),
        t.CoordinateDefinition.Value,T(t.LocalFrame),DrawingPoint(t.PositionMm),t.Rows.Select(r=>new PackDrawingHoleRow(r.Id,r.Number,Hole(r.Evidence),DrawingPoint(r.CoordinatesMm),r.StaleReason)).ToArray(),t.StaleReason,
        t.CoordinateBinding is {} c?new(c.Plane,c.OriginPoint,c.XAxis,c.ReverseX):null,t.CoordinateRevisions.ToArray());
    private static DrawingHoleTable HoleTable(PackDrawingHoleTable t,DocumentId document)
    {
        if(t is null||t.Slots is not {Length:>=1 and <=128}||t.Rows is null||t.Rows.Length>32||t.CoordinateRevisions is null)
            throw new InvalidDataException("Malformed hole table.");
        return new(t.Id,t.View,t.Name,new(document,t.Slots.Select(s=>new ComponentSlotId(s))),new(t.Definition),T(t.Frame),DrawingPoint(t.Position))
        {StaleReason=t.Stale,CoordinateBinding=t.Coordinates is {} c?new(c.Plane,c.Origin,c.XAxis,c.ReverseX):null,
            CoordinateRevisions=[..t.CoordinateRevisions],Rows=[..t.Rows.Select(r=>r is not null?
            new DrawingHoleRow(r.Id,r.Number,Hole(r.Evidence,document),DrawingPoint(r.Coordinates),r.Stale):throw new InvalidDataException("Missing hole row."))]};
    }
    internal static ReadOnlyMemory<byte> UpgradeDrawingHoleTables(ReadOnlyMemory<byte> bytes)
    {
        VisitLegacyDrawingAnnotations(bytes);
        var old=Read<PackDrawingBook>(bytes);
        if(old.Sheets is null||old.Sheets.Any(s=>s is null||s.HoleTables is not null))
            throw new InvalidDataException("New current hole tables in a legacy drawing section.");
        return SerializeLegacyDrawingBook(old with{Sheets=old.Sheets.Select(s=>s with{HoleTables=[]}).ToArray()});
    }
    internal static ReadOnlyMemory<byte> UpgradeDrawingHoleCoordinates(ReadOnlyMemory<byte> bytes)
    {
        VisitLegacyDrawingAnnotations(bytes);
        var old=Read<PackDrawingBook>(bytes);
        if(old.Sheets is null||old.Sheets.Any(s=>s is null||s.HoleTables is null||s.HoleTables.Any(t=>t is null)))
            throw new InvalidDataException("Missing legacy hole tables.");
        // Inspect array length as well as values: even explicit nil at a new numeric key is not a v8 field.
        var reader=new MessagePackReader(bytes);reader.ReadArrayHeader();int sheets=reader.ReadArrayHeader();
        for(int s=0;s<sheets;s++)
        {
            int fields=reader.ReadArrayHeader();
            for(int key=0;key<fields;key++)
            {
                if(key!=15){reader.Skip();continue;}
                int tables=reader.ReadArrayHeader();
                for(int t=0;t<tables;t++)
                {
                    int count=reader.ReadArrayHeader();if(count>9)throw new InvalidDataException("New coordinate fields in legacy drawings v8.");
                    for(int k=0;k<count;k++)reader.Skip();
                }
            }
        }
        return SerializeLegacyDrawingBook(old with{Sheets=old.Sheets.Select(s=>s with{HoleTables=s.HoleTables!.Select(t=>t with{Coordinates=null,CoordinateRevisions=[]}).ToArray()}).ToArray()});
    }
}
