using System.Globalization;
using System.Collections.Immutable;

namespace Cadoryx.Db;

// Persisted values: append only.
public enum DrawingPaperFormat { Custom,A4,A3,A2,A1,A0,AnsiA,AnsiB,AnsiC,AnsiD,AnsiE }
public enum DrawingProjectionMethod { FirstAngle,ThirdAngle }
public enum DrawingCaptionLanguage { English,Chinese,Japanese }

public sealed record DrawingFrameMargins(double Left=20,double Top=10,double Right=10,double Bottom=10)
{
    public void Validate()
    {
        if(new[]{Left,Top,Right,Bottom}.Any(x=>!double.IsFinite(x)||x is <5 or >100))
            throw new CadValidationException("Drawing frame margins must be 5 to 100 mm.");
    }
}

/// <summary>Authored manufacturing data, not inferred certification or measured geometry.</summary>
public sealed record DrawingTitleBlock(string DrawingNumber="",string Revision="",string Organization="",
    string Material="",string SurfaceFinish="",string GeneralTolerance="",string DrawnBy="",
    string CheckedBy="",string ApprovedBy="",string IssueDate="")
{
    public void Validate()
    {
        foreach(var field in new[]{DrawingNumber,Revision,Organization,Material,SurfaceFinish,GeneralTolerance,
            DrawnBy,CheckedBy,ApprovedBy,IssueDate})
            if(field is null||field.Length>128||field.Any(char.IsControl))
                throw new CadValidationException("Title block fields must contain at most 128 printable characters.");
        if(DrawingNumber.Length>28||Revision.Length>12||Organization.Length>48||Material.Length>28||
           SurfaceFinish.Length>28||GeneralTolerance.Length>28||DrawnBy.Length>28||CheckedBy.Length>28||ApprovedBy.Length>28)
            throw new CadValidationException("Title block field exceeds its cell capacity (number and small fields 28, revision 12, organization 48 characters).");
        if(IssueDate.Length!=0&&!DateOnly.TryParseExact(IssueDate,"yyyy-MM-dd",CultureInfo.InvariantCulture,
            DateTimeStyles.None,out _))throw new CadValidationException("Issue date must use yyyy-MM-dd.");
    }
}

public sealed record DrawingSheetTemplate(DrawingPaperFormat Paper,bool Landscape,
    DrawingProjectionMethod Projection,DrawingCaptionLanguage Language,
    DrawingFrameMargins Margins,DrawingTitleBlock TitleBlock)
{
    public ImmutableArray<DrawingCustomField> CustomFields {get;init;}=[];
    public ImmutableArray<DrawingManufacturingNote> Notes {get;init;}=[];
    public DrawingFrameZones? Zones {get;init;}
    public bool Equals(DrawingSheetTemplate? other)=>other is not null&&Paper==other.Paper&&Landscape==other.Landscape&&
        Projection==other.Projection&&Language==other.Language&&Margins==other.Margins&&TitleBlock==other.TitleBlock&&
        Zones==other.Zones&&CustomFields.SequenceEqual(other.CustomFields)&&Notes.SequenceEqual(other.Notes);
    public override int GetHashCode()
    {
        var hash=new HashCode();hash.Add(Paper);hash.Add(Landscape);hash.Add(Projection);hash.Add(Language);
        hash.Add(Margins);hash.Add(TitleBlock);hash.Add(Zones);
        foreach(var field in CustomFields)hash.Add(field);foreach(var note in Notes)hash.Add(note);return hash.ToHashCode();
    }
    public void Validate(double width,double height)
    {
        if(!Enum.IsDefined(Paper)||!Enum.IsDefined(Projection)||!Enum.IsDefined(Language)||Margins is null||TitleBlock is null)
            throw new CadValidationException("Invalid manufacturing drawing template.");
        Margins.Validate();TitleBlock.Validate();
        if(width-Margins.Left-Margins.Right<180-1e-8||height-Margins.Top-Margins.Bottom<72)
            throw new CadValidationException("The frame must leave at least 180 by 72 mm for the title block and drawing.");
        if(Paper!=DrawingPaperFormat.Custom)
        {
            var size=PaperSize(Paper,Landscape);
            if(Math.Abs(width-size.Width)>1e-8||Math.Abs(height-size.Height)>1e-8)
                throw new CadValidationException("Sheet dimensions differ from the selected paper format. Select Custom for a custom size.");
        }
        DrawingTemplateContent.Validate(this,width,height);
    }
    public static (double Width,double Height) PaperSize(DrawingPaperFormat paper,bool landscape)
    {
        var (shorter,longer)=paper switch
        {
            DrawingPaperFormat.A4=>(210d,297d),DrawingPaperFormat.A3=>(297d,420d),
            DrawingPaperFormat.A2=>(420d,594d),DrawingPaperFormat.A1=>(594d,841d),DrawingPaperFormat.A0=>(841d,1189d),
            DrawingPaperFormat.AnsiA=>(215.9,279.4),DrawingPaperFormat.AnsiB=>(279.4,431.8),
            DrawingPaperFormat.AnsiC=>(431.8,558.8),DrawingPaperFormat.AnsiD=>(558.8,863.6),
            DrawingPaperFormat.AnsiE=>(863.6,1117.6),
            _=>throw new CadValidationException("Custom paper has no predefined dimensions.")
        };
        return landscape?(longer,shorter):(shorter,longer);
    }
    public static TechnicalDrawingSheet Create(string name,DrawingStandard standard=DrawingStandard.ISO,
        DrawingPaperFormat? paper=null,bool landscape=true,DrawingCaptionLanguage language=DrawingCaptionLanguage.English)
    {
        var format=paper??(standard==DrawingStandard.ASME?DrawingPaperFormat.AnsiB:DrawingPaperFormat.A4);
        var size=PaperSize(format,landscape);
        return new TechnicalDrawingSheet(Guid.NewGuid(),name,size.Width,size.Height,LengthUnit.Millimeter,[],[],name)
        {Standard=standard,Template=new(format,landscape,
            standard==DrawingStandard.ASME?DrawingProjectionMethod.ThirdAngle:DrawingProjectionMethod.FirstAngle,
            language,new(),new())};
    }
}
