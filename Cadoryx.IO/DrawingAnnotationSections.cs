using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject] public sealed record PackDrawingAttachment(
    [property:Key(0)] Guid[] Slots,[property:Key(1)] Guid Definition,[property:Key(2)] Guid Body,[property:Key(3)] Guid? Feature,
    [property:Key(4)] Guid Revision,[property:Key(5)] string Asset,[property:Key(6)] string Fingerprint,
    [property:Key(7)] int Index,[property:Key(8)] int Kind,[property:Key(9)] int Geometry,
    [property:Key(10)] int Boundary,[property:Key(11)] double Parameter,[property:Key(12)] double[] Point,[property:Key(13)] int Schema,
    [property:Key(14)] PackDrawingAttachment? HistoryOrigin=null);
[MessagePackObject] public sealed record PackDrawingDatumSymbol(
    [property:Key(0)] Guid Id,[property:Key(1)] Guid View,[property:Key(2)] string Name,
    [property:Key(3)] PackDrawingAttachment Source,[property:Key(4)] double[] Position,[property:Key(5)] string? Stale,
    [property:Key(6)] int FeatureType=0,[property:Key(7)] PackDrawingSizeEvidence? Size=null,
    [property:Key(8)] PackDrawingPlanarSizeEvidence? PlanarSize=null);
[MessagePackObject] public sealed record PackDrawingControlFrame(
    [property:Key(0)] Guid Id,[property:Key(1)] Guid View,[property:Key(2)] PackDrawingAttachment Source,
    [property:Key(3)] double[] Position,[property:Key(4)] int Characteristic,[property:Key(5)] double Tolerance,
    [property:Key(6)] int Target,[property:Key(7)] bool Diameter,[property:Key(8)] int Condition,
    [property:Key(9)] Guid[] Datums,[property:Key(10)] string? Stale,
    [property:Key(11)] PackDrawingDatumGroup[]? DatumSystem=null,[property:Key(12)] PackDrawingSizeEvidence? Size=null,
    [property:Key(13)] PackDrawingDatumBinding[]? DatumBindings=null,[property:Key(14)] PackDrawingPlanarSizeEvidence? PlanarSize=null);
[MessagePackObject] public sealed record PackDrawingDatumReference([property:Key(0)] Guid Id,[property:Key(1)] int Condition);
[MessagePackObject] public sealed record PackDrawingDatumGroup([property:Key(0)] PackDrawingDatumReference[] References);
[MessagePackObject] public sealed record PackDrawingDatumBinding([property:Key(0)] Guid Id,[property:Key(1)] int FeatureType,
    [property:Key(2)] PackDrawingAttachment Source,[property:Key(3)] PackDrawingAttachment? OppositeSource=null);
[MessagePackObject] public sealed record PackDrawingPlanarSizeEvidence([property:Key(0)] PackDrawingAttachment Source,
    [property:Key(1)] PackDrawingAttachment OppositeSource,[property:Key(2)] double[] Origin,[property:Key(3)] double[] Direction,
    [property:Key(4)] double Width,[property:Key(5)] bool Internal,[property:Key(6)] int[] Faces,
    [property:Key(7)] int[] FirstEdges,[property:Key(8)] int[] SecondEdges,[property:Key(9)] int[] ConnectingEdges,
    [property:Key(10)] int[] PrismCaps,[property:Key(11)] int[] PrismBoundaryEdges);
[MessagePackObject] public sealed record PackDrawingSizeEvidence([property:Key(0)] PackDrawingAttachment Source,
    [property:Key(1)] double[] Origin,[property:Key(2)] double[] Direction,[property:Key(3)] double Diameter,
    [property:Key(4)] double Length,[property:Key(5)] bool Internal,[property:Key(6)] int[] Faces,[property:Key(7)] int[] Rings);
[MessagePackObject] public sealed record PackDrawingBalloon(
    [property:Key(0)] Guid Id,[property:Key(1)] Guid View,[property:Key(2)] PackDrawingAttachment Source,
    [property:Key(3)] double[] Position,[property:Key(4)] int Number,[property:Key(5)] int Quantity,
    [property:Key(6)] string Name,[property:Key(7)] string? Stale);

internal static partial class MessagePackSections
{
    private static PackDrawingAttachment Attachment(DrawingTopologyReference source)=>new(source.Path.Slots.Select(s=>s.Value).ToArray(),
        source.DefinitionId.Value,source.BodyId.Value,source.FeatureId?.Value,source.Revision.Value,source.Asset.Sha256,
        source.Fingerprint,source.FullTopologyIndex,(int)source.Kind,(int)source.Geometry,source.BoundaryEdgeIndex,
        source.EdgeParameter,DrawingPoint(source.LocalPoint),source.SchemaVersion,source.HistoryOrigin is {} origin?Attachment(origin):null);
    private static DrawingTopologyReference Attachment(PackDrawingAttachment source,DocumentId document)
    {
        if(source is null||source.Slots is not {Length:>=1 and <=128}||source.HistoryOrigin?.HistoryOrigin is not null)throw new InvalidDataException("Malformed drawing attachment path or nested history origin.");
        return new(new(document,source.Slots.Select(s=>new ComponentSlotId(s))),new(source.Definition),new(source.Body),
            source.Feature is {} feature?new FeatureId(feature):null,new(source.Revision),new(source.Asset),source.Fingerprint,
            source.Index,(TopologyKind)source.Kind,(DrawingAttachmentGeometry)source.Geometry,source.Boundary,
            source.Parameter,DrawingVector(source.Point),source.Schema){HistoryOrigin=source.HistoryOrigin is {} origin?Attachment(origin,document):null};
    }
    private static PackDrawingSizeEvidence Size(DrawingCylinderSizeEvidence s)=>new(Attachment(s.Source),DrawingPoint(s.Origin),DrawingPoint(s.Direction),
        s.DiameterMm,s.LengthMm,s.Internal,s.ProvenFaceIndices.ToArray(),s.RingEdgeIndices.ToArray());
    private static DrawingCylinderSizeEvidence Size(PackDrawingSizeEvidence s,DocumentId document)
    {
        if(s.Faces is not {Length:3}||s.Rings is not {Length:>=2 and <=32})throw new InvalidDataException("Malformed original cylindrical size proof.");
        return new(Attachment(s.Source,document),DrawingVector(s.Origin),DrawingVector(s.Direction),s.Diameter,s.Length,s.Internal)
            {ProvenFaceIndices=[..s.Faces],RingEdgeIndices=[..s.Rings]};
    }
    private static PackDrawingPlanarSizeEvidence PlanarSize(DrawingPlanarSizeEvidence s)=>new(Attachment(s.Source),Attachment(s.OppositeSource),DrawingPoint(s.Origin),
        DrawingPoint(s.Direction),s.WidthMm,s.Internal,s.ProvenFaceIndices.ToArray(),s.FirstBoundaryEdgeIndices.ToArray(),s.SecondBoundaryEdgeIndices.ToArray(),
        s.ConnectingEdgeIndices.ToArray(),s.PrismCapFaceIndices.ToArray(),s.PrismBoundaryEdgeIndices.ToArray());
    private static DrawingPlanarSizeEvidence PlanarSize(PackDrawingPlanarSizeEvidence s,DocumentId document)
    {
        if(s.Faces is not {Length:>=5 and <=66}||s.FirstEdges is not {Length:>=3 and <=64}||s.SecondEdges is not {Length:>=3 and <=64}||
            s.ConnectingEdges is not {Length:>=3 and <=64}||s.PrismCaps is not {Length:2}||s.PrismBoundaryEdges is not {Length:>=6 and <=128})
            throw new InvalidDataException("Malformed original finite planar size proof.");
        return new(Attachment(s.Source,document),Attachment(s.OppositeSource,document),DrawingVector(s.Origin),DrawingVector(s.Direction),s.Width,s.Internal)
            {ProvenFaceIndices=[..s.Faces],FirstBoundaryEdgeIndices=[..s.FirstEdges],SecondBoundaryEdgeIndices=[..s.SecondEdges],
             ConnectingEdgeIndices=[..s.ConnectingEdges],PrismCapFaceIndices=[..s.PrismCaps],PrismBoundaryEdgeIndices=[..s.PrismBoundaryEdges]};
    }
    private static PackDrawingDatumSymbol Datum(DrawingDatumSymbol d)=>new(d.Id,d.ViewId,d.Name,Attachment(d.Source),DrawingPoint(d.PositionMm),d.StaleReason,
        (int)d.FeatureType,d.SizeEvidence is {} size?Size(size):null,d.PlanarSizeEvidence is {} planar?PlanarSize(planar):null);
    private static DrawingDatumSymbol Datum(PackDrawingDatumSymbol d,DocumentId document)=>d is not null?
        new(d.Id,d.View,d.Name,Attachment(d.Source,document),DrawingPoint(d.Position),d.Stale)
            {FeatureType=(DrawingDatumFeatureType)d.FeatureType,SizeEvidence=d.Size is {} size?Size(size,document):null,
             PlanarSizeEvidence=d.PlanarSize is {} planar?PlanarSize(planar,document):null}:throw new InvalidDataException("Missing drawing datum symbol.");
    private static PackDrawingControlFrame Frame(DrawingFeatureControlFrame f)=>new(f.Id,f.ViewId,Attachment(f.Source),DrawingPoint(f.PositionMm),
        (int)f.Characteristic,f.ToleranceMm,(int)f.Target,f.DiameterZone,(int)f.MaterialCondition,f.Datums.ToArray(),f.StaleReason,
        f.DatumSystem.Select(g=>new PackDrawingDatumGroup(g.References.Select(r=>new PackDrawingDatumReference(r.Id,(int)r.BoundaryCondition)).ToArray())).ToArray(),
        f.SizeEvidence is {} size?Size(size):null,f.DatumBindings.Select(b=>new PackDrawingDatumBinding(b.Id,(int)b.FeatureType,Attachment(b.Source),
            b.OppositeSource is {} opposite?Attachment(opposite):null)).ToArray(),f.PlanarSizeEvidence is {} planar?PlanarSize(planar):null);
    private static DrawingFeatureControlFrame Frame(PackDrawingControlFrame f,DocumentId document)
    {
        if(f is null||f.Datums is null||f.Datums.Length>3||f.DatumSystem is null||f.DatumSystem.Length>3||
            f.DatumSystem.Any(g=>g is null||g.References is not {Length:>=1 and <=3}||g.References.Any(r=>r is null))||
            f.DatumBindings is null||f.DatumBindings.Length>9||f.DatumBindings.Any(b=>b is null))throw new InvalidDataException("Malformed control frame datum system.");
        return new(f.Id,f.View,Attachment(f.Source,document),DrawingPoint(f.Position),(DrawingGeometricCharacteristic)f.Characteristic,
            f.Tolerance,(DrawingToleranceTarget)f.Target,f.Diameter,(DrawingMaterialCondition)f.Condition,f.Stale){Datums=[..f.Datums],
            DatumSystem=[..f.DatumSystem.Select(g=>new DrawingDatumGroup([..g.References.Select(r=>new DrawingDatumReference(r.Id,(DrawingMaterialCondition)r.Condition))]))],
            SizeEvidence=f.Size is {} size?Size(size,document):null,PlanarSizeEvidence=f.PlanarSize is {} planar?PlanarSize(planar,document):null,
            DatumBindings=[..f.DatumBindings.Select(b=>new DrawingDatumFeatureBinding(b.Id,(DrawingDatumFeatureType)b.FeatureType,Attachment(b.Source,document))
                {OppositeSource=b.OppositeSource is {} opposite?Attachment(opposite,document):null})]};
    }
    private static PackDrawingBalloon Balloon(DrawingAssemblyBalloon b)=>new(b.Id,b.ViewId,Attachment(b.Source),DrawingPoint(b.PositionMm),b.ItemNumber,b.Quantity,b.PartName,b.StaleReason);
    private static DrawingAssemblyBalloon Balloon(PackDrawingBalloon b,DocumentId document)=>b is not null?
        new(b.Id,b.View,Attachment(b.Source,document),DrawingPoint(b.Position),b.Number,b.Stale){Quantity=b.Quantity,PartName=b.Name}:
        throw new InvalidDataException("Missing drawing balloon.");
    internal static ReadOnlyMemory<byte> UpgradeDrawingAnnotations(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackDrawingBook>(bytes);
        if(old.Sheets is null||old.Sheets.Any(s=>s is null||s.Datums is not null||s.Frames is not null||s.Balloons is not null||s.PartsTable is not null))
            throw new InvalidDataException("New manufacturing annotations in legacy drawing section.");
        return SerializeLegacyDrawingBook(old with{Sheets=old.Sheets.Select(s=>s with{Datums=[],Frames=[],Balloons=[]}).ToArray()});
    }
}
