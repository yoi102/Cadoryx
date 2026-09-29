using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject] public sealed record PackDrawingBook([property:Key(0)] PackDrawingSheet[] Sheets);
[MessagePackObject] public sealed record PackDrawingSheet(
    [property:Key(0)] Guid Id,[property:Key(1)] string Name,[property:Key(2)] double Width,
    [property:Key(3)] double Height,[property:Key(4)] int Unit,[property:Key(5)] string Title,
    [property:Key(6)] string? Author,[property:Key(7)] PackDrawingView[] Views,
    [property:Key(8)] PackDrawingDimension[] Dimensions,
    [property:Key(9)] int? Standard=null);
[MessagePackObject] public sealed record PackDrawingView(
    [property:Key(0)] Guid Id,[property:Key(1)] string Name,[property:Key(2)] int Kind,
    [property:Key(3)] Guid? Parent,[property:Key(4)] double[] Center,[property:Key(5)] double Scale,
    [property:Key(6)] PackDrawingSource[] Sources,[property:Key(7)] PackDrawingStroke[] Strokes,
    [property:Key(8)] string? Stale,[property:Key(9)] double[]? Normal,[property:Key(10)] double Offset,
    [property:Key(11)] double[]? DetailCenter=null,[property:Key(12)] double DetailRadius=0);
[MessagePackObject] public sealed record PackDrawingSource(
    [property:Key(0)] Guid[] Slots,[property:Key(1)] Guid Body,[property:Key(2)] Guid? Feature,
    [property:Key(3)] Guid Revision,[property:Key(4)] string Asset,[property:Key(5)] PackTransform Transform);
[MessagePackObject] public sealed record PackDrawingStroke(
    [property:Key(0)] int Kind,[property:Key(1)] bool Hidden,[property:Key(2)] double[] Points,
    [property:Key(3)] bool Closed);
[MessagePackObject] public sealed record PackDrawingDimension(
    [property:Key(0)] Guid Id,[property:Key(1)] Guid View,[property:Key(2)] int Kind,
    [property:Key(3)] PackAssemblyDatum First,[property:Key(4)] PackAssemblyDatum? Second,
    [property:Key(5)] double[] TextPosition,[property:Key(6)] double Value,
    [property:Key(7)] uint Argb,[property:Key(8)] string? Stale,
    [property:Key(9)] double UpperTolerance=0,[property:Key(10)] double LowerTolerance=0);

internal static partial class MessagePackSections
{
    private static double[] DrawingPoint(Point2d p)=>[p.X,p.Y];
    private static Point2d DrawingPoint(double[] p)=>p is {Length:2}?new(p[0],p[1]):
        throw new InvalidDataException("Malformed drawing point.");
    private static double[] DrawingPoint(Vector3d p)=>[p.X,p.Y,p.Z];
    private static Vector3d DrawingVector(double[] p)=>p is {Length:3}?new(p[0],p[1],p[2]):
        throw new InvalidDataException("Malformed drawing vector.");

    public static byte[] EncodeDrawings(DocumentSnapshot document)=>Serialize(new PackDrawingBook(
        document.DrawingSheets.Values.OrderBy(s=>s.Id).Select(s=>new PackDrawingSheet(s.Id,s.Name,s.WidthMm,s.HeightMm,
            (int)s.Unit,s.Title,s.Author,s.Views.Select(v=>new PackDrawingView(v.Id,v.Name,(int)v.Kind,v.ParentView,
                DrawingPoint(v.CenterMm),v.Scale,v.Sources.Select(x=>new PackDrawingSource(
                    x.Path.Slots.Select(slot=>slot.Value).ToArray(),x.Body.Value,x.Feature?.Value,x.Revision.Value,
                    x.Asset.Sha256,T(x.WorldTransform))).ToArray(),v.Strokes.Select(line=>new PackDrawingStroke(
                    (int)line.Kind,line.Hidden,line.Points.SelectMany(DrawingPoint).ToArray(),line.Closed)).ToArray(),
                v.StaleReason,v.SectionNormal is {} n?DrawingPoint(n):null,v.SectionOffsetMm,
                v.DetailCenter is {} focus?DrawingPoint(focus):null,v.DetailRadius)).ToArray(),
            s.Dimensions.Select(d=>new PackDrawingDimension(d.Id,d.ViewId,(int)d.Kind,D(d.First),
                d.Second is {} other?D(other):null,DrawingPoint(d.TextPositionMm),d.Value,d.Argb,d.StaleReason,
                d.UpperTolerance,d.LowerTolerance)).ToArray(),(int)s.Standard)).ToArray()));

    internal static ReadOnlyMemory<byte> UpgradeDrawingStandards(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackDrawingBook>(bytes);
        if(old.Sheets is null||old.Sheets.Any(s=>s.Standard is not (null or 0)||
            s.Views is null||s.Views.Any(v=>v.DetailCenter is not null||v.DetailRadius!=0)||
            s.Dimensions is null||s.Dimensions.Any(d=>d.UpperTolerance!=0||d.LowerTolerance!=0)))
            throw new InvalidDataException("New drawing fields in legacy section.");
        return Serialize(old);
    }

    public static DocumentSnapshot AttachDrawings(ReadOnlyMemory<byte> bytes,DocumentSnapshot document)
    {
        var packed=Read<PackDrawingBook>(bytes);
        if(packed.Sheets is null||packed.Sheets.Length>64)throw new InvalidDataException("Malformed drawing book.");
        var sheets=ImmutableDictionary.CreateBuilder<Guid,TechnicalDrawingSheet>();
        foreach(var sheet in packed.Sheets)
        {
            if(sheet.Views is null||sheet.Views.Length>128||sheet.Dimensions is null||sheet.Dimensions.Length>10000)
                throw new InvalidDataException("Malformed drawing sheet arrays.");
            var views=ImmutableArray.CreateBuilder<TechnicalDrawingView>();
            foreach(var view in sheet.Views)
            {
                if(view.Sources is null||view.Sources.Length is <1 or >256||view.Strokes is null||view.Strokes.Length>100000)
                    throw new InvalidDataException("Malformed drawing view arrays.");
                var sources=view.Sources.Select(source=>
                {
                    if(source.Slots is null||source.Slots.Length is <1 or >128)
                        throw new InvalidDataException("Malformed drawing source path.");
                    return new DrawingSource(new(document.Id,source.Slots.Select(id=>new ComponentSlotId(id))),
                        new(source.Body),source.Feature is {} feature?new FeatureId(feature):null,
                        new(source.Revision),new(source.Asset),T(source.Transform));
                }).ToImmutableArray();
                var strokes=view.Strokes.Select(line=>
                {
                    if(line.Points is null||line.Points.Length is <4 or >8192||line.Points.Length%2!=0)
                        throw new InvalidDataException("Malformed drawing stroke points.");
                    return new DrawingStroke((DrawingLineKind)line.Kind,line.Hidden,
                        [..Enumerable.Range(0,line.Points.Length/2).Select(i=>new Point2d(line.Points[2*i],line.Points[2*i+1]))],
                        line.Closed);
                }).ToImmutableArray();
                views.Add(new TechnicalDrawingView(view.Id,view.Name,(DrawingViewKind)view.Kind,view.Parent,DrawingPoint(view.Center),view.Scale,
                    sources,strokes,view.Stale,view.Normal is null?null:DrawingVector(view.Normal),view.Offset)
                {DetailCenter=view.DetailCenter is null?null:DrawingPoint(view.DetailCenter),DetailRadius=view.DetailRadius});
            }
            var dimensions=sheet.Dimensions.Select(d=>new TechnicalDrawingDimension(d.Id,d.View,(DrawingMeasureKind)d.Kind,
                D(d.First,document.Id),d.Second is {} second?D(second,document.Id):null,DrawingPoint(d.TextPosition),
                d.Value,d.Argb,d.Stale){UpperTolerance=d.UpperTolerance,LowerTolerance=d.LowerTolerance}).ToImmutableArray();
            var value=new TechnicalDrawingSheet(sheet.Id,sheet.Name,sheet.Width,sheet.Height,(LengthUnit)sheet.Unit,
                views.ToImmutable(),dimensions,sheet.Title,sheet.Author){Standard=(DrawingStandard)(sheet.Standard??0)};
            if(!sheets.TryAdd(value.Id,value))throw new InvalidDataException("Duplicate drawing sheet ID.");
        }
        return document with{DrawingSheets=sheets.ToImmutable()};
    }
}
