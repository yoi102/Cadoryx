using System.Collections.Immutable;

namespace Cadoryx.Db;

public sealed record DrawingPageLine(Point2d A,Point2d B,bool Hidden,double WidthMm);
public sealed record DrawingPageLabel(Point2d Position,string Text,double SizeMm,uint Argb=0xFF202020);
public sealed record DrawingPageLayout(double WidthMm,double HeightMm,
    ImmutableArray<DrawingPageLine> Lines,ImmutableArray<DrawingPageLabel> Labels)
{
    public static DrawingPageLayout Create(TechnicalDrawingSheet sheet,int pageNumber,int pageCount)
    {
        var lines=ImmutableArray.CreateBuilder<DrawingPageLine>();
        var labels=ImmutableArray.CreateBuilder<DrawingPageLabel>();
        var viewOrigins=new Dictionary<Guid,Point2d>();
        void Add(double x1,double y1,double x2,double y2,double width=.18)=>
            lines.Add(new(new(x1,y1),new(x2,y2),false,width));
        Add(8,8,sheet.WidthMm-8,8,.35);Add(sheet.WidthMm-8,8,sheet.WidthMm-8,sheet.HeightMm-8,.35);
        Add(sheet.WidthMm-8,sheet.HeightMm-8,8,sheet.HeightMm-8,.35);Add(8,sheet.HeightMm-8,8,8,.35);
        double titleTop=sheet.HeightMm-29,titleLeft=Math.Max(10,sheet.WidthMm-105);
        Add(titleLeft,titleTop,sheet.WidthMm-8,titleTop);Add(titleLeft,titleTop,titleLeft,sheet.HeightMm-8);
        Add(titleLeft,sheet.HeightMm-17,sheet.WidthMm-8,sheet.HeightMm-17);
        labels.Add(new(new(titleLeft+3,titleTop+6),sheet.Title,3.3));
        labels.Add(new(new(titleLeft+3,sheet.HeightMm-12),
            $"{sheet.Name}  ·  {pageNumber}/{pageCount}  ·  {sheet.Unit}",2.5));
        labels.Add(new(new(titleLeft+3,titleTop+11),
            $"{sheet.Standard} · {(sheet.Standard==DrawingStandard.ASME?"third-angle":"first-angle")}",2.2));
        if(!string.IsNullOrWhiteSpace(sheet.Author))labels.Add(new(new(10,sheet.HeightMm-11),sheet.Author,2.4));
        foreach(var view in sheet.Views)
        {
            if(view.Strokes.IsDefaultOrEmpty)continue;
            var all=view.Strokes.SelectMany(s=>s.Points);
            var minX=all.Min(p=>p.X);var maxX=all.Max(p=>p.X);
            var minY=all.Min(p=>p.Y);var maxY=all.Max(p=>p.Y);
            var origin=view.Kind==DrawingViewKind.Detail?view.DetailCenter!.Value:
                new Point2d((minX+maxX)/2,(minY+maxY)/2);
            viewOrigins[view.Id]=origin;
            Point2d Map(Point2d point)=>new(view.CenterMm.X+(point.X-origin.X)*view.Scale,
                view.CenterMm.Y-(point.Y-origin.Y)*view.Scale);
            foreach(var stroke in view.Strokes)
            {
                var previous=stroke.Points[0];
                for(int i=1;i<stroke.Points.Length;i++)
                {
                    var next=stroke.Points[i];
                    if(ClipDetail(view,previous,next) is {} segment)
                        lines.Add(new(Map(segment.A),Map(segment.B),stroke.Hidden,StrokeWidth(stroke)));
                    previous=next;
                }
                if(stroke.Closed&&ClipDetail(view,previous,stroke.Points[0]) is {} closing)
                    lines.Add(new(Map(closing.A),Map(closing.B),stroke.Hidden,StrokeWidth(stroke)));
            }
            if(view.Kind==DrawingViewKind.Detail)
            {
                const int circleSegments=64;
                for(int i=0;i<circleSegments;i++)
                {
                    double a=i*Math.Tau/circleSegments,b=(i+1)*Math.Tau/circleSegments;
                    var radius=view.DetailRadius;
                    lines.Add(new(Map(new(origin.X+radius*Math.Cos(a),origin.Y+radius*Math.Sin(a))),
                        Map(new(origin.X+radius*Math.Cos(b),origin.Y+radius*Math.Sin(b))),false,.13));
                }
            }
            labels.Add(new(new(view.CenterMm.X,Math.Max(10,view.CenterMm.Y+
                (view.Kind==DrawingViewKind.Detail?view.DetailRadius*2:maxY-minY)*view.Scale/2+4)),
                $"{view.Name}  1:{1/view.Scale:G4}"+(view.StaleReason is null?"":"  [STALE]"),2.6));
        }
        foreach(var dimension in sheet.Dimensions)
        {
            var view=sheet.Views.First(v=>v.Id==dimension.ViewId);
            if(viewOrigins.TryGetValue(view.Id,out var middle))
            {
                void Leader(AssemblyDatumReference datum)
                {
                    var source=view.Sources.FirstOrDefault(s=>s.Path.Equals(datum.Path)&&s.Body==datum.BodyId);
                    if(source is null)return;
                    var projection=view.Kind==DrawingViewKind.Detail?
                        sheet.Views.Single(v=>v.Id==view.ParentView):view;
                    var point=ProjectDatum(projection,source.WorldTransform.Apply(datum.LocalPoint));
                    var position=new Point2d(view.CenterMm.X+(point.X-middle.X)*view.Scale,
                        view.CenterMm.Y-(point.Y-middle.Y)*view.Scale);
                    lines.Add(new(position,dimension.TextPositionMm,false,.13));
                }
                Leader(dimension.First);if(dimension.Second is {} other)Leader(other);
            }
            var value=dimension.Kind==DrawingMeasureKind.Angle?"°":sheet.Unit switch
            {LengthUnit.Centimeter=>" cm",LengthUnit.Meter=>" m",LengthUnit.Inch=>" in",_=>" mm"};
            var amount=dimension.Kind==DrawingMeasureKind.Angle?dimension.Value:sheet.Unit switch
            {LengthUnit.Centimeter=>dimension.Value/10,LengthUnit.Meter=>dimension.Value/1000,
             LengthUnit.Inch=>dimension.Value/25.4,_=>dimension.Value};
            var prefix=dimension.Kind switch{DrawingMeasureKind.Radius=>"R",DrawingMeasureKind.Diameter=>"⌀",_=>""};
            var conversion=dimension.Kind==DrawingMeasureKind.Angle?1:sheet.Unit switch
            {LengthUnit.Centimeter=>.1,LengthUnit.Meter=>.001,LengthUnit.Inch=>1/25.4,_=>1};
            var tolerance=dimension.UpperTolerance==0&&dimension.LowerTolerance==0?"":
                $" +{dimension.UpperTolerance*conversion:0.###}/-{dimension.LowerTolerance*conversion:0.###}";
            labels.Add(new(dimension.TextPositionMm,prefix+amount.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+tolerance+value+
                (dimension.StaleReason is null?"":" [STALE]"),3,dimension.Argb));
        }
        return new(sheet.WidthMm,sheet.HeightMm,lines.ToImmutable(),labels.ToImmutable());
    }
    private static (Point2d A,Point2d B)? ClipDetail(TechnicalDrawingView view,Point2d a,Point2d b)
    {
        if(view.Kind!=DrawingViewKind.Detail)return(a,b);
        var c=view.DetailCenter!.Value;double radius=view.DetailRadius;
        double dx=b.X-a.X,dy=b.Y-a.Y,x=a.X-c.X,y=a.Y-c.Y;
        double aa=dx*dx+dy*dy;if(aa<=1e-24)return null;
        double bb=2*(x*dx+y*dy),cc=x*x+y*y-radius*radius;
        double discriminant=bb*bb-4*aa*cc;
        var insideA=cc<=0;var insideB=(b.X-c.X)*(b.X-c.X)+(b.Y-c.Y)*(b.Y-c.Y)<=radius*radius;
        if(discriminant<0)return insideA&&insideB?(a,b):null;
        var root=Math.Sqrt(discriminant);
        double begin=Math.Max(0,(-bb-root)/(2*aa)),end=Math.Min(1,(-bb+root)/(2*aa));
        if(end<=begin)return null;
        return(new(a.X+begin*dx,a.Y+begin*dy),new(a.X+end*dx,a.Y+end*dy));
    }
    private static double StrokeWidth(DrawingStroke stroke)=>stroke.Hidden?.13:stroke.Kind switch
    {
        DrawingLineKind.Outline=>.32,
        DrawingLineKind.Sharp=>.23,
        DrawingLineKind.Smooth=>.16,
        DrawingLineKind.Sewn=>.13,
        _=>.1
    };

    private static Point2d ProjectDatum(TechnicalDrawingView view,Vector3d point)
    {
        var direction=view.Kind switch
        {
            DrawingViewKind.Front=>new Vector3d(0,-1,0),
            DrawingViewKind.Top=>Vector3d.UnitZ,
            DrawingViewKind.Right=>new Vector3d(1,0,0),
            DrawingViewKind.Isometric=>new Vector3d(1,-1,1).Normalized(),
            DrawingViewKind.Section=>view.SectionNormal!.Value,
            _=>Vector3d.UnitZ
        };
        var up=view.Kind==DrawingViewKind.Top||view.Kind==DrawingViewKind.Section&&Math.Abs(direction.Z)>=.9?
            new Vector3d(0,1,0):Vector3d.UnitZ;
        var right=up.Cross(direction).Normalized();var vertical=direction.Cross(right).Normalized();
        return new(point.Dot(right),point.Dot(vertical));
    }
}
