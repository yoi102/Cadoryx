using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Physical sheet content. It has no model, view or topology references.</summary>
public sealed record DrawingCustomField(Guid Id,string Caption,string Value,Point2d PositionMm,double WidthMm,double HeightMm=12);
public sealed record DrawingManufacturingNote(Guid Id,string Text,Point2d PositionMm,double WidthMm);
public sealed record DrawingFrameZones(int Columns=8,int Rows=6,bool ReverseColumns=false,bool ReverseRows=false);

/// <summary>A detached snapshot of authored sheet defaults; applying it never edits projected model content.</summary>
public sealed record DrawingTemplateDefinition(Guid Id,string Name,double WidthMm,double HeightMm,LengthUnit Unit,
    DrawingStandard Standard,string Title,string? Author,DrawingSheetTemplate Template)
{
    public void Validate()
    {
        if(Id==Guid.Empty||string.IsNullOrWhiteSpace(Name)||Name.Length>64||Name.Any(char.IsControl))
            throw new CadValidationException("Template names require 1 to 64 printable characters.");
        if(Template is null)throw new CadValidationException("A library entry requires a manufacturing frame.");
        new TechnicalDrawingSheet(Guid.NewGuid(),Name,WidthMm,HeightMm,Unit,[],[],Title,Author)
            {Standard=Standard,Template=Template}.Validate(DocumentId.New());
    }
    public static DrawingTemplateDefinition Capture(TechnicalDrawingSheet sheet,Guid id,string name)=>
        new(id,name,sheet.WidthMm,sheet.HeightMm,sheet.Unit,sheet.Standard,sheet.Title,sheet.Author,
            (sheet.Template??throw new CadValidationException("Select a manufacturing frame before saving a template.")).CopyContent());
    public TechnicalDrawingSheet Apply(TechnicalDrawingSheet sheet)=>sheet with
        {WidthMm=WidthMm,HeightMm=HeightMm,Unit=Unit,Standard=Standard,Title=Title,Author=Author,Template=Template.CopyContent()};
}

internal static class DrawingTemplateContent
{
    internal static DrawingSheetTemplate CopyContent(this DrawingSheetTemplate template)=>template with
    {
        CustomFields=[..template.CustomFields.Select(f=>f with{Id=Guid.NewGuid()})],
        Notes=[..template.Notes.Select(n=>n with{Id=Guid.NewGuid()})]
    };
    internal static void Validate(DrawingSheetTemplate template,double width,double height)
    {
        if(template.CustomFields.IsDefault||template.Notes.IsDefault||template.CustomFields.Length>32||template.Notes.Length>32)
            throw new CadValidationException("A sheet supports up to 32 custom fields and 32 manufacturing notes.");
        var m=template.Margins;var ids=new HashSet<Guid>();var boxes=new List<(double X,double Y,double W,double H)>();
        void Box(Guid id,Point2d position,double w,double h)
        {
            if(id==Guid.Empty||!ids.Add(id)||!double.IsFinite(position.X)||!double.IsFinite(position.Y)||
               !double.IsFinite(w)||!double.IsFinite(h)||w<20||h<8||
               position.X<m.Left||position.Y<m.Top||position.X+w>width-m.Right||position.Y+h>height-m.Bottom)
                throw new CadValidationException("Sheet content IDs must be unique and its physical bounds must fit inside the frame.");
            bool Intersects(double x,double y,double bw,double bh)=>
                position.X<x+bw-1e-8&&position.X+w>x+1e-8&&position.Y<y+bh-1e-8&&position.Y+h>y+1e-8;
            if(Intersects(width-m.Right-180,height-m.Bottom-72,180,72)||boxes.Any(b=>Intersects(b.X,b.Y,b.W,b.H)))
                throw new CadValidationException("Custom fields and notes must not overlap each other or the title block and projection symbol.");
            boxes.Add((position.X,position.Y,w,h));
        }
        static bool Printable(string? text,int limit)=>text is not null&&text.Length<=limit&&!text.Any(char.IsControl);
        foreach(var field in template.CustomFields)
        {
            if(field is null||string.IsNullOrWhiteSpace(field.Caption)||!Printable(field.Caption,48)||!Printable(field.Value,128)||field.HeightMm>50)
                throw new CadValidationException("Custom fields require a printable caption (48 characters) and value (128 characters).");
            Box(field.Id,field.PositionMm,field.WidthMm,field.HeightMm);
        }
        foreach(var note in template.Notes)
        {
            if(note is null||string.IsNullOrWhiteSpace(note.Text)||note.Text.Length>20*97)throw new CadValidationException("Manufacturing notes require up to 20 lines of text.");
            var rows=note.Text.Split('\n');
            if(rows.Length>20||rows.Any(row=>!Printable(row,96)))
                throw new CadValidationException("Notes support 20 lines of 96 printable characters. Use line breaks to lay out text.");
            Box(note.Id,note.PositionMm,note.WidthMm,Math.Max(8,rows.Length*4+2));
        }
        if(template.Zones is {} zones&&(zones.Columns is <1 or >32||zones.Rows is <1 or >26||
            (width-m.Left-m.Right)/zones.Columns<20||(height-m.Top-m.Bottom)/zones.Rows<20))
            throw new CadValidationException("Frame zones require 1–32 columns and 1–26 rows, each at least 20 mm.");
    }
}
