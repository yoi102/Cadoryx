using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum DrawingViewKind { Front,Top,Right,Isometric,Section,Detail }
public enum DrawingLineKind { Sharp,Smooth,Sewn,Outline,Isoparameter,SectionHatch }
public enum DrawingMeasureKind { Length,Angle,Radius,Diameter }
public enum DrawingStandard { ISO,GB,ASME }

/// <summary>Persisted 2D geometry is a cache. Source identity, not the polyline order, controls refresh.</summary>
public sealed record DrawingStroke(DrawingLineKind Kind,bool Hidden,ImmutableArray<Point2d> Points,bool Closed=false)
{
    public void Validate()
    {
        if(!Enum.IsDefined(Kind)||Points.IsDefault||Points.Length is <2 or >4096||
           Points.Any(p=>!double.IsFinite(p.X)||!double.IsFinite(p.Y)||Math.Abs(p.X)>1e9||Math.Abs(p.Y)>1e9))
            throw new CadValidationException("Invalid drawing polyline.");
        if(Kind==DrawingLineKind.SectionHatch&&(Hidden||Closed||Points.Length!=2))
            throw new CadValidationException("A section hatch must be one visible straight segment.");
    }
}

public sealed record DrawingSource(OccurrencePath Path,BodyId Body,FeatureId? Feature,
    GeometryRevisionId Revision,AssetId Asset,RigidTransform3d WorldTransform)
{
    public void Validate(DocumentId document)
    {
        if(Path is null||Path.DocumentId!=document||Path.Slots.Length is <1 or >128)
            throw new CadValidationException("Invalid drawing occurrence path.");
        CadGuard.Id(Body);CadGuard.Id(Revision);Asset.Validate();WorldTransform.Validate();
        if(Feature is {} feature)CadGuard.Id(feature);
    }
}

public sealed record TechnicalDrawingView(Guid Id,string Name,DrawingViewKind Kind,Guid? ParentView,
    Point2d CenterMm,double Scale,ImmutableArray<DrawingSource> Sources,
    ImmutableArray<DrawingStroke> Strokes,string? StaleReason=null,
    Vector3d? SectionNormal=null,double SectionOffsetMm=0)
{
    /// <summary>Detail coordinates and radius are in the parent projection's model units.</summary>
    public Point2d? DetailCenter {get;init;}
    public double DetailRadius {get;init;}
    public DrawingSectionHatching? Hatching {get;init;}
    public void Validate(DocumentId document)
    {
        if(Id==Guid.Empty||!Enum.IsDefined(Kind)||ParentView==Id||
           !double.IsFinite(CenterMm.X)||!double.IsFinite(CenterMm.Y)||
           !double.IsFinite(Scale)||Scale is <0.0001 or >10000||
           Sources.IsDefaultOrEmpty||Sources.Length>256||Strokes.IsDefault||Strokes.Length>100000||
           !double.IsFinite(SectionOffsetMm)||Math.Abs(SectionOffsetMm)>1e9||
           !double.IsFinite(DetailRadius))
            throw new CadValidationException("Invalid drawing view.");
        if(StaleReason is {Length:>2048})throw new CadValidationException("Drawing diagnostic is too long.");
        CadGuard.Name(Name);
        if(Hatching is not null)
        {
            if(Kind!=DrawingViewKind.Section)throw new CadValidationException("Only a base section view owns hatch settings.");
            Hatching.Validate();
        }
        if(Kind==DrawingViewKind.Section)
        {
            if(SectionNormal is not {} normal||Math.Abs(normal.Length-1)>1e-8)
                throw new CadValidationException("A section view needs a unit cutting normal.");
            normal.Validate();
        }
        else if(SectionNormal is not null)throw new CadValidationException("Only section views may have a cutting plane.");
        if(Kind==DrawingViewKind.Detail)
        {
            if(ParentView is null||DetailCenter is not {} focus||
               !double.IsFinite(focus.X)||!double.IsFinite(focus.Y)||
               Math.Abs(focus.X)>1e9||Math.Abs(focus.Y)>1e9||DetailRadius is <0.001 or >1e9)
                throw new CadValidationException("A detail view needs a parent and a finite crop circle.");
        }
        else if(DetailCenter is not null||DetailRadius!=0)
            throw new CadValidationException("Only detail views may have a crop circle.");
        foreach(var source in Sources)
        {
            if(source is null)throw new CadValidationException("Null drawing source.");
            source.Validate(document);
        }
        if(Sources.Select(s=>(s.Path,s.Body)).Distinct().Count()!=Sources.Length)
            throw new CadValidationException("Duplicate drawing source.");
        foreach(var line in Strokes)line.Validate();
        if(Kind is not (DrawingViewKind.Section or DrawingViewKind.Detail)&&
            Strokes.Any(line=>line.Kind==DrawingLineKind.SectionHatch))
            throw new CadValidationException("Section hatching needs a section view or its detail view.");
    }
}

/// <summary>Dimensions use exact analytic BRep evidence; cached values are checked on refresh.</summary>
public sealed record TechnicalDrawingDimension(Guid Id,Guid ViewId,DrawingMeasureKind Kind,
    AssemblyDatumReference First,AssemblyDatumReference? Second,Point2d TextPositionMm,
    double Value,uint Argb=0xFF202020,string? StaleReason=null)
{
    public double UpperTolerance {get;init;}
    public double LowerTolerance {get;init;}
    public void Validate(DocumentId document)
    {
        if(Id==Guid.Empty||ViewId==Guid.Empty||!Enum.IsDefined(Kind)||
           !double.IsFinite(TextPositionMm.X)||!double.IsFinite(TextPositionMm.Y)||
           !double.IsFinite(Value)||Value<0||Value>1e9||
           !double.IsFinite(UpperTolerance)||!double.IsFinite(LowerTolerance)||
           UpperTolerance is <0 or >1e6||LowerTolerance is <0 or >1e6||
           Kind is DrawingMeasureKind.Length or DrawingMeasureKind.Angle && Second is null||
           First is null||Kind is DrawingMeasureKind.Radius or DrawingMeasureKind.Diameter && First.RadiusMm<=0)
            throw new CadValidationException("Invalid drawing dimension.");
        if(StaleReason is {Length:>2048})throw new CadValidationException("Dimension diagnostic is too long.");
        First.Validate(document);Second?.Validate(document);
    }
}

public sealed record TechnicalDrawingSheet(Guid Id,string Name,double WidthMm,double HeightMm,
    LengthUnit Unit,ImmutableArray<TechnicalDrawingView> Views,
    ImmutableArray<TechnicalDrawingDimension> Dimensions,string Title="",string? Author=null)
{
    public DrawingStandard Standard {get;init;}=DrawingStandard.ISO;
    public DrawingSheetTemplate? Template {get;init;}
    public ImmutableArray<DrawingDatumSymbol> DatumSymbols {get;init;}=[];
    public ImmutableArray<DrawingFeatureControlFrame> ControlFrames {get;init;}=[];
    public ImmutableArray<DrawingAssemblyBalloon> Balloons {get;init;}=[];
    public ImmutableArray<DrawingHoleTable> HoleTables {get;init;}=[];
    public Point2d? PartsTablePositionMm {get;init;}
    public static TechnicalDrawingSheet A4Landscape(string name)=>
        new(Guid.NewGuid(),name,297,210,LengthUnit.Millimeter,[],[],name);

    public void Validate(DocumentId document)
    {
        if(Id==Guid.Empty||!double.IsFinite(WidthMm)||!double.IsFinite(HeightMm)||
           WidthMm is <50 or >2000||HeightMm is <50 or >2000||!Enum.IsDefined(Unit)||
           Views.IsDefault||Views.Length>128||Dimensions.IsDefault||Dimensions.Length>10000||
           !Enum.IsDefined(Standard))
            throw new CadValidationException("Invalid drawing sheet.");
        CadGuard.Name(Name);CadGuard.Name(Title);
        Template?.Validate(WidthMm,HeightMm);
        if(Template is not null&&(Title.Length>96||Title.Any(char.IsControl)||Author is {Length:>28}))
            throw new CadValidationException("Manufacturing title is limited to 96 printable characters and author to 28.");
        if(Author is {Length:>256})throw new CadValidationException("Drawing author is too long.");
        var ids=new HashSet<Guid>();
        foreach(var view in Views)
        {view.Validate(document);if(!ids.Add(view.Id))throw new CadValidationException("Duplicate drawing view ID.");}
        foreach(var view in Views)if(view.ParentView is {} parent&&(!ids.Contains(parent)||parent==view.Id))
            throw new CadValidationException("Drawing parent view is missing.");
        foreach(var view in Views.Where(v=>v.Kind==DrawingViewKind.Detail))
        {
            var parent=Views.Single(v=>v.Id==view.ParentView);
            if(parent.Kind!=DrawingViewKind.Section&&view.Strokes.Any(s=>s.Kind==DrawingLineKind.SectionHatch))
                throw new CadValidationException("A hatched detail must belong to a section view.");
            if(parent.Kind==DrawingViewKind.Detail||parent.Sources.Length!=view.Sources.Length||
               parent.Sources.Where((source,index)=>
                   !source.Path.Equals(view.Sources[index].Path)||source.Body!=view.Sources[index].Body||
                   source.Feature!=view.Sources[index].Feature||source.Revision!=view.Sources[index].Revision||
                   source.Asset!=view.Sources[index].Asset||source.WorldTransform!=view.Sources[index].WorldTransform).Any())
                throw new CadValidationException("A detail view must share its parent projection sources.");
        }
        if(Views.Any(view=>ParentCycle(view.Id)))throw new CadValidationException("Drawing view hierarchy is cyclic.");
        var dimensions=new HashSet<Guid>();
        foreach(var dimension in Dimensions)
        {
            dimension.Validate(document);
            if(!dimensions.Add(dimension.Id)||!ids.Contains(dimension.ViewId))
                throw new CadValidationException("Invalid drawing dimension owner.");
        }
        if(DatumSymbols.IsDefault||ControlFrames.IsDefault||Balloons.IsDefault||HoleTables.IsDefault||
           DatumSymbols.Length>256||ControlFrames.Length>512||Balloons.Length>512||HoleTables.Length>16)
            throw new CadValidationException("Invalid manufacturing annotation arrays.");
        var annotationIds=new HashSet<Guid>();
        void Owner(Guid id,Guid view)
        {
            if(!annotationIds.Add(id)||!ids.Contains(view))throw new CadValidationException("Invalid manufacturing annotation owner or duplicate ID.");
        }
        foreach(var datum in DatumSymbols){if(datum is null)throw new CadValidationException("Missing datum symbol.");datum.Validate(document);Owner(datum.Id,datum.ViewId);}
        if(DatumSymbols.Select(d=>d.Name).Distinct(StringComparer.Ordinal).Count()!=DatumSymbols.Length)
            throw new CadValidationException("Datum labels must be unique on a sheet.");
        foreach(var frame in ControlFrames)
        {
            if(frame is null)throw new CadValidationException("Missing control frame.");frame.Validate(document);Owner(frame.Id,frame.ViewId);
            DrawingManufacturingRules.Validate(this,frame);
        }
        foreach(var balloon in Balloons){if(balloon is null)throw new CadValidationException("Missing balloon.");balloon.Validate(document);Owner(balloon.Id,balloon.ViewId);}
        foreach(var holeTable in HoleTables)
        {
            if(holeTable is null)throw new CadValidationException("Missing hole table.");holeTable.Validate(document);Owner(holeTable.Id,holeTable.ViewId);
            foreach(var row in holeTable.Rows)if(!annotationIds.Add(row.Id))throw new CadValidationException("Duplicate manufacturing hole row ID.");
            var p=holeTable.PositionMm;double height=7*(holeTable.Rows.Length+2);
            var m=Template?.Margins??new DrawingFrameMargins(0,0,0,0);
            if(p.X<m.Left||p.Y<m.Top||p.X+150>WidthMm-m.Right||p.Y+height>HeightMm-m.Bottom||
                Template is not null&&p.X+150>WidthMm-m.Right-180&&p.Y+height>HeightMm-m.Bottom-72)
                throw new CadValidationException("Hole table must fit inside the frame and outside the reserved title block.");
            bool Overlaps(Point2d other,double w,double h)=>p.X<other.X+w-1e-8&&p.X+150>other.X+1e-8&&p.Y<other.Y+h-1e-8&&p.Y+height>other.Y+1e-8;
            if(HoleTables.Any(t=>t.Id!=holeTable.Id&&Overlaps(t.PositionMm,150,7*(t.Rows.Length+2)))||
                Template is {} content&&(content.CustomFields.Any(f=>Overlaps(f.PositionMm,f.WidthMm,f.HeightMm))||
                    content.Notes.Any(n=>Overlaps(n.PositionMm,n.WidthMm,Math.Max(8,n.Text.Split('\n').Length*4+2))))||
                PartsTablePositionMm is {} parts&&Overlaps(parts,70,7*(Balloons.Select(b=>b.ItemNumber).Distinct().Count()+1)))
                throw new CadValidationException("Hole tables must not overlap other tables, custom fields or manufacturing notes.");
        }
        if(Balloons.GroupBy(b=>b.ItemNumber).Any(g=>g.Select(b=>b.Source.DefinitionId).Distinct().Count()!=1)||
           Balloons.GroupBy(b=>b.Source.DefinitionId).Any(g=>g.Select(b=>b.ItemNumber).Distinct().Count()!=1)||
           Balloons.Select(b=>(b.ViewId,b.Source.Path)).Distinct().Count()!=Balloons.Length)
            throw new CadValidationException("Assembly item numbers must identify one explicit definition; one balloon per occurrence per view.");
        if(PartsTablePositionMm is {} table&&(!double.IsFinite(table.X)||!double.IsFinite(table.Y)||table.X<0||table.Y<0||table.X+70>WidthMm||table.Y+7*(Balloons.Select(b=>b.ItemNumber).Distinct().Count()+1)>HeightMm))
            throw new CadValidationException("Parts table must fit on the physical sheet.");
        bool ParentCycle(Guid id)
        {
            var seen=new HashSet<Guid>();
            while(id!=Guid.Empty)
            {
                if(!seen.Add(id))return true;
                id=Views.First(v=>v.Id==id).ParentView??Guid.Empty;
            }
            return false;
        }
    }
}
