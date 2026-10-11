using System.Collections.Immutable;
using System.Globalization;

namespace Cadoryx.Db;

internal static class DrawingHoleTableLayout
{
    internal static void Add(TechnicalDrawingSheet sheet,IReadOnlyDictionary<Guid,Point2d> origins,
        ImmutableArray<DrawingPageLine>.Builder lines,ImmutableArray<DrawingPageLabel>.Builder labels)
    {
        string Caption(string en,string zh,string ja)=>sheet.Template?.Language switch
        {DrawingCaptionLanguage.Chinese=>zh,DrawingCaptionLanguage.Japanese=>ja,_=>en};
        double unit=sheet.Unit switch{LengthUnit.Centimeter=>.1,LengthUnit.Meter=>.001,LengthUnit.Inch=>1/25.4,_=>1};
        string Number(double value)=>(Math.Abs(value*unit)<.0000005?0:value*unit).ToString("0.######",CultureInfo.InvariantCulture);
        void Line(double x1,double y1,double x2,double y2)=>lines.Add(new(new(x1,y1),new(x2,y2),false,.18));
        foreach(var table in sheet.HoleTables)
        {
            double x=table.PositionMm.X,y=table.PositionMm.Y;var rows=table.Rows.OrderBy(r=>r.Number).ToArray();
            double height=7*(rows.Length+1);double[] widths=[8,16,16,16,20,44,30];
            string[] headings=[Caption("No.","孔号","穴番号"),"X","Y",Caption("Diameter","直径","直径"),
                Caption("Depth","深度","深さ"),Caption("Head / standard","孔头 / 规格","座ぐり / 規格"),Caption("Specification","规格来源","仕様の出典")];
            string units=sheet.Unit switch{LengthUnit.Centimeter=>"cm",LengthUnit.Meter=>"m",LengthUnit.Inch=>"in",_=>"mm"};
            labels.Add(new(new(x,y+4.5),table.Name+" · "+units,2.6,MaxWidthMm:150));y+=7;
            Line(x,y,x+150,y);Line(x,y+height,x+150,y+height);Line(x,y,x,y+height);double cell=x;
            for(int i=0;i<widths.Length;i++)
            {labels.Add(new(new(cell+1,y+5),headings[i],2.1,MaxWidthMm:widths[i]-2));cell+=widths[i];Line(cell,y,cell,y+height);}
            for(int i=0;i<rows.Length;i++)
            {
                var row=rows[i];var e=row.Evidence;double top=y+7*(i+1);Line(x,top,x+150,top);
                string head=e.Head switch{HoleHead.Counterbore=>"CB "+Number(e.HeadDiameterMm)+" x "+Number(e.HeadDepthMm),
                    HoleHead.Countersink=>"CS "+Number(e.HeadDiameterMm)+" x "+(e.HeadAngle*180/Math.PI).ToString("0.###",CultureInfo.InvariantCulture)+" deg",_=>"—"};
                if(e.Standard is {} standard)head+=" · M"+standard.NominalMm+" "+standard.Fit;
                if(e.Thread is {} thread)head=thread.Designation+" · "+(thread.Through?Caption("THRU","贯通","貫通"):"L "+Number(thread.DepthMm))+
                    " · "+(thread.Representation==ThreadHoleRepresentation.Modeled?Caption("modeled","实体","実体"):Caption("symbolic","符号","記号"));
                string[] values=[row.Number.ToString(CultureInfo.InvariantCulture),Number(row.CoordinatesMm.X),Number(row.CoordinatesMm.Y),
                    Number(e.DiameterMm),e.Through?Caption("THRU","通孔","貫通"):Number(e.DepthMm),head,
                    row.StaleReason is not null||table.StaleReason is not null?"[STALE]":e.SpecificationSource==DrawingHoleSpecificationSource.CurrentAuthoredHole?
                    Caption("Authored","当前作者孔","現在の作成穴"):Caption("Geometry","当前几何","現在の形状")];
                cell=x;for(int j=0;j<values.Length;j++)
                {labels.Add(new(new(cell+1,top+5),values[j],2.3,MaxWidthMm:widths[j]-2));cell+=widths[j];}
            }
            var view=sheet.Views.Single(v=>v.Id==table.ViewId);
            if(!origins.TryGetValue(view.Id,out var origin))continue;
            var projection=view.Kind==DrawingViewKind.Detail?sheet.Views.Single(v=>v.Id==view.ParentView):view;
            Point2d OnPage(Vector3d world)
            {var p=DrawingPageLayout.ProjectDatum(projection,world);return new(view.CenterMm.X+(p.X-origin.X)*view.Scale,view.CenterMm.Y-(p.Y-origin.Y)*view.Scale);}
            foreach(var row in rows)
            {
                var source=view.Sources.FirstOrDefault(s=>s.Path.Equals(row.Evidence.Source.Path)&&s.Body==row.Evidence.Source.BodyId);
                if(source is null)continue;var p=OnPage(source.WorldTransform.Apply(row.Evidence.Entry));
                Line(p.X-1,p.Y,p.X+1,p.Y);Line(p.X,p.Y-1,p.X,p.Y+1);
                labels.Add(new(new(p.X+2,p.Y-1),row.Number.ToString(CultureInfo.InvariantCulture),2.4));
            }
            var basis=view.Sources.FirstOrDefault(s=>s.Path.Equals(table.CoordinatePath));
            if(basis is null)continue;var frame=basis.WorldTransform*table.LocalFrame;var zero=OnPage(frame.Translation);
            labels.Add(new(new(zero.X-3,zero.Y+3),"O",2.4));
            foreach(var (axis,name) in new[]{(new Vector3d(1,0,0),"X"),(new Vector3d(0,1,0),"Y")})
            {
                var end=OnPage(frame.Apply(axis*8));Line(zero.X,zero.Y,end.X,end.Y);
                labels.Add(new(new(end.X+1,end.Y+1),name,2.4));
            }
        }
    }
}
