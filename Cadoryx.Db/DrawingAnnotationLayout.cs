using System.Collections.Immutable;
using System.Globalization;

namespace Cadoryx.Db;

internal static class DrawingAnnotationLayout
{
    internal static void Add(TechnicalDrawingSheet sheet,IReadOnlyDictionary<Guid,Point2d> origins,
        ImmutableArray<DrawingPageLine>.Builder lines,ImmutableArray<DrawingPageLabel>.Builder labels)
    {
        void Line(double x1,double y1,double x2,double y2,double width=.18)=>lines.Add(new(new(x1,y1),new(x2,y2),false,width));
        void Rect(double x,double y,double w,double h){Line(x,y,x+w,y);Line(x+w,y,x+w,y+h);Line(x+w,y+h,x,y+h);Line(x,y+h,x,y);}
        void Circle(double x,double y,double r,double begin=0,double end=Math.Tau)
        {
            for(int i=0;i<48;i++){double a=begin+(end-begin)*i/48,b=begin+(end-begin)*(i+1)/48;Line(x+r*Math.Cos(a),y+r*Math.Sin(a),x+r*Math.Cos(b),y+r*Math.Sin(b));}
        }
        Point2d? Anchor(Guid viewId,DrawingTopologyReference reference,Vector3d? localPoint=null)
        {
            var view=sheet.Views.Single(v=>v.Id==viewId);
            if(!origins.TryGetValue(viewId,out var origin))return null;
            var source=view.Sources.FirstOrDefault(s=>s.Path.Equals(reference.Path)&&s.Body==reference.BodyId);
            if(source is null)return null;
            var projection=view.Kind==DrawingViewKind.Detail?sheet.Views.Single(v=>v.Id==view.ParentView):view;
            var point=DrawingPageLayout.ProjectDatum(projection,source.WorldTransform.Apply(localPoint??reference.LocalPoint));
            return new(view.CenterMm.X+(point.X-origin.X)*view.Scale,view.CenterMm.Y-(point.Y-origin.Y)*view.Scale);
        }
        void Leader(Guid view,DrawingTopologyReference reference,Point2d end,bool datum=false,Vector3d? localPoint=null)
        {
            if(Anchor(view,reference,localPoint) is not {} anchor)return;
            double dx=end.X-anchor.X,dy=end.Y-anchor.Y,length=Math.Sqrt(dx*dx+dy*dy);
            if(length<1e-6)return;dx/=length;dy/=length;
            double baseX=anchor.X+dx*2,baseY=anchor.Y+dy*2;
            Line(anchor.X,anchor.Y,end.X,end.Y);
            Line(anchor.X,anchor.Y,baseX-dy,baseY+dx);Line(anchor.X,anchor.Y,baseX+dy,baseY-dx);
            if(datum)Line(baseX-dy,baseY+dx,baseX+dy,baseY-dx);
        }
        Point2d BoxConnection(Guid view,DrawingTopologyReference reference,Point2d topLeft,double width,Vector3d? localPoint=null)
        {
            var center=new Point2d(topLeft.X+width/2,topLeft.Y+3.5);
            if(Anchor(view,reference,localPoint) is not {} anchor)return center;
            double dx=anchor.X-center.X,dy=anchor.Y-center.Y;
            double ratio=Math.Min(Math.Abs(dx)<1e-9?double.PositiveInfinity:width/2/Math.Abs(dx),
                Math.Abs(dy)<1e-9?double.PositiveInfinity:3.5/Math.Abs(dy));
            return !double.IsFinite(ratio)?center:new(center.X+dx*ratio,center.Y+dy*ratio);
        }
        foreach(var datum in sheet.DatumSymbols)
        {
            var p=datum.PositionMm;double width=Math.Max(8,datum.Name.Length*3+3);
            Vector3d? center=datum.PlanarSizeEvidence is {} planar?planar.Origin:
                datum.SizeEvidence is {} size?size.Origin+size.Direction*(size.LengthMm/2):null;
            Rect(p.X,p.Y,width,7);Leader(datum.ViewId,datum.Source,BoxConnection(datum.ViewId,datum.Source,p,width,center),true,center);
            labels.Add(new(new(p.X+2,p.Y+5),datum.Name,3));
            if(datum.StaleReason is not null)labels.Add(new(new(p.X,p.Y-1),"[STALE]",2));
        }
        void Arrow(double x,double y)
        {Line(x-2,y+2,x+2,y-2);Line(x+2,y-2,x-.2,y-2);Line(x+2,y-2,x+2,y+.2);}
        void Symbol(DrawingGeometricCharacteristic kind,double x,double y)
        {
            switch(kind)
            {
                case DrawingGeometricCharacteristic.Straightness:Line(x-2.4,y,x+2.4,y);break;
                case DrawingGeometricCharacteristic.Flatness:
                    Line(x-2.4,y+1.3,x+1.2,y+1.3);Line(x+1.2,y+1.3,x+2.4,y-1.3);Line(x+2.4,y-1.3,x-1.2,y-1.3);Line(x-1.2,y-1.3,x-2.4,y+1.3);break;
                case DrawingGeometricCharacteristic.Circularity:Circle(x,y,2);break;
                case DrawingGeometricCharacteristic.Cylindricity:
                    Circle(x,y,1.6);Line(x-3,y+2,x-1.8,y-2);Line(x+1.8,y+2,x+3,y-2);break;
                case DrawingGeometricCharacteristic.ProfileLine:Circle(x,y+1.5,2.4,Math.PI,Math.Tau);break;
                case DrawingGeometricCharacteristic.ProfileSurface:Circle(x,y+1.5,2.4,Math.PI,Math.Tau);Line(x-2.4,y+1.5,x+2.4,y+1.5);break;
                case DrawingGeometricCharacteristic.Parallelism:Line(x-2,y+2,x,y-2);Line(x,y+2,x+2,y-2);break;
                case DrawingGeometricCharacteristic.Perpendicularity:Line(x-2.4,y+1.6,x+2.4,y+1.6);Line(x,y+1.6,x,y-2);break;
                case DrawingGeometricCharacteristic.Angularity:Line(x-2,y+1.8,x+2.4,y+1.8);Line(x-2,y+1.8,x+.6,y-2);break;
                case DrawingGeometricCharacteristic.Position:Circle(x,y,1.8);Line(x-2.5,y,x+2.5,y);Line(x,y-2.5,x,y+2.5);break;
                case DrawingGeometricCharacteristic.CircularRunout:Arrow(x,y);break;
                case DrawingGeometricCharacteristic.TotalRunout:Arrow(x-1,y-1);Arrow(x+1,y+1);Line(x-3,y+1,x-1,y+3);break;
            }
        }
        foreach(var frame in sheet.ControlFrames)
        {
            var p=frame.PositionMm;double x=p.X;
            Rect(x,p.Y,8,7);Symbol(frame.Characteristic,x+4,p.Y+3.5);x+=8;
            double conversion=sheet.Unit switch{LengthUnit.Centimeter=>.1,LengthUnit.Meter=>.001,LengthUnit.Inch=>1/25.4,_=>1};
            string tolerance=(frame.ToleranceMm*conversion).ToString("0.#########",CultureInfo.InvariantCulture);
            double width=Math.Max(12,tolerance.Length*2.5+4)+(frame.DiameterZone?5:0)+(frame.MaterialCondition!=DrawingMaterialCondition.None?6:0);
            Rect(x,p.Y,width,7);double textX=x+2;
            if(frame.DiameterZone){Circle(textX+1.5,p.Y+3.5,1.5);Line(textX,p.Y+5.5,textX+3,p.Y+1.5);textX+=5;}
            labels.Add(new(new(textX,p.Y+5),tolerance,2.8));
            if(frame.MaterialCondition!=DrawingMaterialCondition.None)
            {Circle(x+width-3,p.Y+3.5,2.1);labels.Add(new(new(x+width-4.2,p.Y+4.5),frame.MaterialCondition==DrawingMaterialCondition.Maximum?"M":"L",2.3));}
            x+=width;
            foreach(var group in frame.OrderedDatumGroups)
            {
                var cells=group.References.Select(r=>(Reference:r,Name:sheet.DatumSymbols.Single(d=>d.Id==r.Id).Name)).ToArray();
                width=cells.Sum(c=>Math.Max(8,c.Name.Length*3+3)+(c.Reference.BoundaryCondition!=DrawingMaterialCondition.None?6:0))+(cells.Length-1)*3;
                Rect(x,p.Y,width,7);double cursor=x;
                for(int i=0;i<cells.Length;i++)
                {
                    var cell=cells[i];double cellWidth=Math.Max(8,cell.Name.Length*3+3)+(cell.Reference.BoundaryCondition!=DrawingMaterialCondition.None?6:0);
                    labels.Add(new(new(cursor+2,p.Y+5),cell.Name,3));
                    if(cell.Reference.BoundaryCondition!=DrawingMaterialCondition.None)
                    {Circle(cursor+cellWidth-3,p.Y+3.5,2.1);labels.Add(new(new(cursor+cellWidth-4.2,p.Y+4.5),cell.Reference.BoundaryCondition==DrawingMaterialCondition.Maximum?"M":"L",2.3));}
                    cursor+=cellWidth;
                    if(i<cells.Length-1){labels.Add(new(new(cursor,p.Y+5),"-",3));cursor+=3;}
                }
                x+=width;
            }
            Vector3d? axis=frame.PlanarSizeEvidence is {} planar?planar.Origin:
                frame.SizeEvidence is {} proof?proof.Origin+proof.Direction*(proof.LengthMm/2):null;
            Leader(frame.ViewId,frame.Source,BoxConnection(frame.ViewId,frame.Source,p,x-p.X,axis),localPoint:axis);
            if(frame.StaleReason is not null)labels.Add(new(new(p.X,p.Y-1),"[STALE]",2));
        }
        foreach(var balloon in sheet.Balloons)
        {
            var p=balloon.PositionMm;Circle(p.X,p.Y,4);
            var anchor=Anchor(balloon.ViewId,balloon.Source)??new Point2d(p.X-4,p.Y);
            double dx=anchor.X-p.X,dy=anchor.Y-p.Y,length=Math.Sqrt(dx*dx+dy*dy);
            if(length>4)Leader(balloon.ViewId,balloon.Source,new(p.X+4*dx/length,p.Y+4*dy/length));
            string number=balloon.ItemNumber.ToString(CultureInfo.InvariantCulture);
            labels.Add(new(new(p.X-number.Length*.85,p.Y+1),number,2.8));
            if(balloon.StaleReason is not null)labels.Add(new(new(p.X-4,p.Y-5),"[STALE]",2));
        }
        if(sheet.PartsTablePositionMm is {} table)
        {
            string Caption(string en,string zh,string ja)=>sheet.Template?.Language switch
                {DrawingCaptionLanguage.Chinese=>zh,DrawingCaptionLanguage.Japanese=>ja,_=>en};
            var rows=sheet.Balloons.GroupBy(b=>b.ItemNumber).OrderBy(g=>g.Key).Select(g=>g.First()).ToArray();
            Rect(table.X,table.Y,70,7*(rows.Length+1));Line(table.X+10,table.Y,table.X+10,table.Y+7*(rows.Length+1));Line(table.X+58,table.Y,table.X+58,table.Y+7*(rows.Length+1));
            labels.Add(new(new(table.X+1,table.Y+5),Caption("Item","序号","番号"),2.4,MaxWidthMm:8));
            labels.Add(new(new(table.X+12,table.Y+5),Caption("Part","零件","部品"),2.4,MaxWidthMm:44));
            labels.Add(new(new(table.X+60,table.Y+5),Caption("Qty","数量","数量"),2.4,MaxWidthMm:8));
            for(int i=0;i<rows.Length;i++)
            {
                var row=rows[i];double y=table.Y+(i+1)*7;Line(table.X,y,table.X+70,y);
                labels.Add(new(new(table.X+1,y+5),row.ItemNumber.ToString(CultureInfo.InvariantCulture),2.6,MaxWidthMm:8));
                labels.Add(new(new(table.X+12,y+5),row.PartName,2.6,MaxWidthMm:44));
                labels.Add(new(new(table.X+60,y+5),row.Quantity.ToString(CultureInfo.InvariantCulture),2.6,MaxWidthMm:8));
            }
        }
    }
}
