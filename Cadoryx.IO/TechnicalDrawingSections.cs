using System.Collections.Immutable;
using Cadoryx.Db;
using MessagePack;

namespace Cadoryx.IO;

[MessagePackObject] public sealed record PackDrawingBook([property:Key(0)] PackDrawingSheet[] Sheets,
    [property:Key(1)] PackDrawingTemplateEntry[]? Templates=null);
[MessagePackObject] public sealed record PackDrawingSheet(
    [property:Key(0)] Guid Id,[property:Key(1)] string Name,[property:Key(2)] double Width,
    [property:Key(3)] double Height,[property:Key(4)] int Unit,[property:Key(5)] string Title,
    [property:Key(6)] string? Author,[property:Key(7)] PackDrawingView[] Views,
    [property:Key(8)] PackDrawingDimension[] Dimensions,
    [property:Key(9)] int? Standard=null,[property:Key(10)] PackSheetTemplate? Template=null,
    [property:Key(11)] PackDrawingDatumSymbol[]? Datums=null,[property:Key(12)] PackDrawingControlFrame[]? Frames=null,
    [property:Key(13)] PackDrawingBalloon[]? Balloons=null,[property:Key(14)] double[]? PartsTable=null,
    [property:Key(15)] PackDrawingHoleTable[]? HoleTables=null);
[MessagePackObject] public sealed record PackSheetTemplate(
    [property:Key(0)] int Paper,[property:Key(1)] bool Landscape,[property:Key(2)] int Projection,
    [property:Key(3)] int Language,[property:Key(4)] double[] Margins,[property:Key(5)] string[] TitleBlock,
    [property:Key(6)] PackDrawingCustomField[]? Fields=null,[property:Key(7)] PackDrawingNote[]? Notes=null,
    [property:Key(8)] PackDrawingZones? Zones=null);
[MessagePackObject] public sealed record PackDrawingView(
    [property:Key(0)] Guid Id,[property:Key(1)] string Name,[property:Key(2)] int Kind,
    [property:Key(3)] Guid? Parent,[property:Key(4)] double[] Center,[property:Key(5)] double Scale,
    [property:Key(6)] PackDrawingSource[] Sources,[property:Key(7)] PackDrawingStroke[] Strokes,
    [property:Key(8)] string? Stale,[property:Key(9)] double[]? Normal,[property:Key(10)] double Offset,
    [property:Key(11)] double[]? DetailCenter=null,[property:Key(12)] double DetailRadius=0,
    [property:Key(13)] PackSectionHatching? Hatching=null);
[MessagePackObject] public sealed record PackHatchStyle(
    [property:Key(0)] double Angle,[property:Key(1)] double Spacing,[property:Key(2)] bool Enabled);
[MessagePackObject] public sealed record PackMaterialHatch(
    [property:Key(0)] Guid Material,[property:Key(1)] PackHatchStyle Style);
[MessagePackObject] public sealed record PackSectionHatching(
    [property:Key(0)] PackHatchStyle Default,[property:Key(1)] bool Alternate,
    [property:Key(2)] PackMaterialHatch[] Materials);
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
    private static PackHatchStyle Hatch(DrawingHatchStyle style)=>new(style.AngleDegrees,style.SpacingMm,style.Enabled);
    private static DrawingHatchStyle Hatch(PackHatchStyle style)=>style is not null?
        new(style.Angle,style.Spacing,style.Enabled):throw new InvalidDataException("Missing hatch style.");
    private static DrawingSectionHatching? Hatch(PackSectionHatching? settings)
    {
        if(settings is null)return null;
        if(settings.Materials is null||settings.Materials.Length>256)
            throw new InvalidDataException("Malformed material hatch rules.");
        return new(Hatch(settings.Default),settings.Alternate)
        {Materials=[..settings.Materials.Select(rule=>rule is not null?
            new DrawingMaterialHatch(new(rule.Material),Hatch(rule.Style)):
            throw new InvalidDataException("Missing material hatch rule."))]};
    }
    private static PackSectionHatching? Hatch(DrawingSectionHatching? settings)=>settings is null?null:
        new(Hatch(settings.Default),settings.AlternateSources,
            settings.Materials.Select(rule=>new PackMaterialHatch(rule.Material.Value,Hatch(rule.Style))).ToArray());
    private static PackSheetTemplate? Template(DrawingSheetTemplate? template)=>template is null?null:
        new((int)template.Paper,template.Landscape,(int)template.Projection,(int)template.Language,
            [template.Margins.Left,template.Margins.Top,template.Margins.Right,template.Margins.Bottom],
            [template.TitleBlock.DrawingNumber,template.TitleBlock.Revision,template.TitleBlock.Organization,
                template.TitleBlock.Material,template.TitleBlock.SurfaceFinish,template.TitleBlock.GeneralTolerance,
                template.TitleBlock.DrawnBy,template.TitleBlock.CheckedBy,template.TitleBlock.ApprovedBy,template.TitleBlock.IssueDate],
            template.CustomFields.Select(f=>new PackDrawingCustomField(f.Id,f.Caption,f.Value,DrawingPoint(f.PositionMm),f.WidthMm,f.HeightMm)).ToArray(),
            template.Notes.Select(n=>new PackDrawingNote(n.Id,n.Text,DrawingPoint(n.PositionMm),n.WidthMm)).ToArray(),
            template.Zones is {} z?new(z.Columns,z.Rows,z.ReverseColumns,z.ReverseRows):null);
    private static DrawingSheetTemplate? Template(PackSheetTemplate? template)
    {
        if(template is null)return null;
        if(template.Margins is not {Length:4}||template.TitleBlock is not {Length:10}||template.TitleBlock.Any(s=>s is null)||
           template.Fields is null||template.Fields.Length>32||template.Notes is null||template.Notes.Length>32)
            throw new InvalidDataException("Malformed manufacturing sheet template.");
        var m=template.Margins;var t=template.TitleBlock;
        return new((DrawingPaperFormat)template.Paper,template.Landscape,(DrawingProjectionMethod)template.Projection,
            (DrawingCaptionLanguage)template.Language,new(m[0],m[1],m[2],m[3]),
            new(t[0],t[1],t[2],t[3],t[4],t[5],t[6],t[7],t[8],t[9]))
        {CustomFields=[..template.Fields.Select(f=>f is not null?new DrawingCustomField(f.Id,f.Caption,f.Value,DrawingPoint(f.Position),f.Width,f.Height):throw new InvalidDataException("Missing custom field."))],
            Notes=[..template.Notes.Select(n=>n is not null?new DrawingManufacturingNote(n.Id,n.Text,DrawingPoint(n.Position),n.Width):throw new InvalidDataException("Missing manufacturing note."))],
            Zones=template.Zones is {} z?new(z.Columns,z.Rows,z.ReverseColumns,z.ReverseRows):null};
    }

    public static byte[] EncodeDrawings(DocumentSnapshot document)=>Serialize(new PackDrawingBook(
        document.DrawingSheets.Values.OrderBy(s=>s.Id).Select(s=>new PackDrawingSheet(s.Id,s.Name,s.WidthMm,s.HeightMm,
            (int)s.Unit,s.Title,s.Author,s.Views.Select(v=>new PackDrawingView(v.Id,v.Name,(int)v.Kind,v.ParentView,
                DrawingPoint(v.CenterMm),v.Scale,v.Sources.Select(x=>new PackDrawingSource(
                    x.Path.Slots.Select(slot=>slot.Value).ToArray(),x.Body.Value,x.Feature?.Value,x.Revision.Value,
                    x.Asset.Sha256,T(x.WorldTransform))).ToArray(),v.Strokes.Select(line=>new PackDrawingStroke(
                    (int)line.Kind,line.Hidden,line.Points.SelectMany(DrawingPoint).ToArray(),line.Closed)).ToArray(),
                v.StaleReason,v.SectionNormal is {} n?DrawingPoint(n):null,v.SectionOffsetMm,
                v.DetailCenter is {} focus?DrawingPoint(focus):null,v.DetailRadius,Hatch(v.Hatching))).ToArray(),
            s.Dimensions.Select(d=>new PackDrawingDimension(d.Id,d.ViewId,(int)d.Kind,D(d.First),
                d.Second is {} other?D(other):null,DrawingPoint(d.TextPositionMm),d.Value,d.Argb,d.StaleReason,
                d.UpperTolerance,d.LowerTolerance)).ToArray(),(int)s.Standard,Template(s.Template),
            s.DatumSymbols.Select(Datum).ToArray(),s.ControlFrames.Select(Frame).ToArray(),s.Balloons.Select(Balloon).ToArray(),
            s.PartsTablePositionMm is {} table?DrawingPoint(table):null,s.HoleTables.Select(HoleTable).ToArray())).ToArray(),
        document.DrawingTemplates.Values.OrderBy(t=>t.Id).Select(t=>new PackDrawingTemplateEntry(t.Id,t.Name,t.WidthMm,t.HeightMm,
            (int)t.Unit,(int)t.Standard,t.Title,t.Author,Template(t.Template)!)).ToArray()));

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
            if(sheet.Datums is null||sheet.Datums.Length>256||sheet.Frames is null||sheet.Frames.Length>512||sheet.Balloons is null||sheet.Balloons.Length>512||sheet.HoleTables is null||sheet.HoleTables.Length>16)
                throw new InvalidDataException("Malformed manufacturing annotation arrays.");
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
                {DetailCenter=view.DetailCenter is null?null:DrawingPoint(view.DetailCenter),DetailRadius=view.DetailRadius,
                    Hatching=Hatch(view.Hatching)});
            }
            var dimensions=sheet.Dimensions.Select(d=>new TechnicalDrawingDimension(d.Id,d.View,(DrawingMeasureKind)d.Kind,
                D(d.First,document.Id),d.Second is {} second?D(second,document.Id):null,DrawingPoint(d.TextPosition),
                d.Value,d.Argb,d.Stale){UpperTolerance=d.UpperTolerance,LowerTolerance=d.LowerTolerance}).ToImmutableArray();
            var value=new TechnicalDrawingSheet(sheet.Id,sheet.Name,sheet.Width,sheet.Height,(LengthUnit)sheet.Unit,
                views.ToImmutable(),dimensions,sheet.Title,sheet.Author)
                {Standard=(DrawingStandard)(sheet.Standard??0),Template=Template(sheet.Template),
                    DatumSymbols=[..sheet.Datums.Select(d=>Datum(d,document.Id))],ControlFrames=[..sheet.Frames.Select(f=>Frame(f,document.Id))],
                    Balloons=[..sheet.Balloons.Select(b=>Balloon(b,document.Id))],PartsTablePositionMm=sheet.PartsTable is {} table?DrawingPoint(table):null,
                    HoleTables=[..sheet.HoleTables.Select(t=>HoleTable(t,document.Id))]};
            if(!sheets.TryAdd(value.Id,value))throw new InvalidDataException("Duplicate drawing sheet ID.");
        }
        return document with{DrawingSheets=sheets.ToImmutable(),DrawingTemplates=Library(packed.Templates)};
    }

    internal static ReadOnlyMemory<byte> UpgradeSectionHatching(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackDrawingBook>(bytes);
        if(old.Sheets is null||old.Sheets.Any(s=>s.Views is null||s.Views.Any(v=>
            v.Strokes is null||v.Strokes.Any(line=>line.Kind is <0 or >4))))
            throw new InvalidDataException("New section hatch fields in legacy drawing section.");
        return Serialize(old with{Sheets=old.Sheets.Select(sheet=>sheet with{Views=sheet.Views.Select(view=>
        {
            var parent=sheet.Views.FirstOrDefault(v=>v.Id==view.Parent);
            bool section=view.Kind==(int)DrawingViewKind.Section||
                view.Kind==(int)DrawingViewKind.Detail&&parent?.Kind==(int)DrawingViewKind.Section;
            return section?view with{Stale=view.Stale??"Refresh the section to generate exact material hatching."}:view;
        }).ToArray()}).ToArray()});
    }

    internal static ReadOnlyMemory<byte> UpgradeSectionHatchRules(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackDrawingBook>(bytes);
        if(old.Sheets is null||old.Sheets.Any(s=>s.Views is null||s.Views.Any(v=>v.Hatching is not null)))
            throw new InvalidDataException("New hatch settings in legacy drawing section.");
        return Serialize(old);
    }
    internal static ReadOnlyMemory<byte> UpgradeManufacturingTemplates(ReadOnlyMemory<byte> bytes)
    {
        var old=Read<PackDrawingBook>(bytes);
        if(old.Sheets is null||old.Sheets.Any(s=>s.Template is not null))
            throw new InvalidDataException("New manufacturing template in legacy drawing section.");
        return Serialize(old);
    }
}
