using System.Collections.Immutable;

namespace Cadoryx.Db;

internal static class DrawingManufacturingLayout
{
    internal static void Add(TechnicalDrawingSheet sheet,DrawingSheetTemplate template,int number,int count,
        ImmutableArray<DrawingPageLine>.Builder lines,ImmutableArray<DrawingPageLabel>.Builder labels)
    {
        template.Validate(sheet.WidthMm,sheet.HeightMm);
        var m=template.Margins;double right=sheet.WidthMm-m.Right,bottom=sheet.HeightMm-m.Bottom;
        void Line(double x1,double y1,double x2,double y2,double width=.18)=>
            lines.Add(new(new(x1,y1),new(x2,y2),false,width));
        Line(m.Left,m.Top,right,m.Top,.35);Line(right,m.Top,right,bottom,.35);
        Line(right,bottom,m.Left,bottom,.35);Line(m.Left,bottom,m.Left,m.Top,.35);
        double left=right-180,top=bottom-60;
        Line(left,top,right,top,.35);Line(left,top,left,bottom,.35);
        for(int i=1;i<5;i++)Line(left,top+i*12,right,top+i*12);
        string Caption(string en,string zh,string ja)=>template.Language switch
            {DrawingCaptionLanguage.Chinese=>zh,DrawingCaptionLanguage.Japanese=>ja,_=>en};
        void Cell(int row,double start,double width,string caption,string text,double size=2.8)
        {
            double x=left+start,y=top+row*12;
            if(start!=0)Line(x,y,x,y+12);
            labels.Add(new(new(x+2,y+3.2),caption,1.8,MaxWidthMm:width-4));
            labels.Add(new(new(x+2,y+9),text,size,MaxWidthMm:width-4));
        }
        var title=template.TitleBlock;
        Cell(0,0,90,Caption("Organization","单位","組織"),title.Organization);
        Cell(0,90,60,Caption("Drawing number","图号","図番"),title.DrawingNumber);
        Cell(0,150,30,Caption("Revision","版本","改訂"),title.Revision);
        Cell(1,0,180,Caption("Title","名称","名称"),sheet.Title,3.5);
        Cell(2,0,60,Caption("Material","材料","材質"),title.Material);
        Cell(2,60,60,Caption("Surface finish","表面要求","表面仕上げ"),title.SurfaceFinish);
        Cell(2,120,60,Caption("General tolerance","一般公差","一般公差"),title.GeneralTolerance);
        Cell(3,0,60,Caption("Drawn by","设计","設計"),title.DrawnBy.Length==0?sheet.Author??"":title.DrawnBy);
        Cell(3,60,60,Caption("Checked by","审核","照査"),title.CheckedBy);
        Cell(3,120,60,Caption("Approved by","批准","承認"),title.ApprovedBy);
        Cell(4,0,38,Caption("Projection","投影法","投影法"),
            template.Projection==DrawingProjectionMethod.FirstAngle?Caption("First angle","第一角","第一角法"):
                Caption("Third angle","第三角","第三角法"),2.4);
        var scales=sheet.Views.Select(v=>v.Scale).Distinct().ToArray();
        Cell(4,38,28,Caption("Scale","比例","尺度"),scales.Length==1?DrawingScale.Format(scales[0]):
            Caption("By view","见视图","図示"),2.4);
        Cell(4,66,22,Caption("Unit","单位","単位"),sheet.Unit switch
            {LengthUnit.Centimeter=>"cm",LengthUnit.Meter=>"m",LengthUnit.Inch=>"in",_=>"mm"},2.4);
        Cell(4,88,22,Caption("Format","图幅","用紙"),template.Paper.ToString(),2.4);
        Cell(4,110,30,Caption("Sheet","张数","枚数"),$"{number}/{count}",2.4);
        Cell(4,140,40,Caption("Issue date","日期","日付"),title.IssueDate,2.4);
        // Vector projection symbol, above the title block. A visible method label
        // is always present; the symbol does not depend on a font glyph.
        double x=right-30,y=top-8,radius=3;
        double circleX=template.Projection==DrawingProjectionMethod.FirstAngle?x+12:x-12;
        Line(x-4,y-3,x+4,y-2);Line(x+4,y-2,x+4,y+2);
        Line(x+4,y+2,x-4,y+3);Line(x-4,y+3,x-4,y-3);
        for(int i=0;i<64;i++)
        {
            double a=i*Math.Tau/64,b=(i+1)*Math.Tau/64;
            Line(circleX+radius*Math.Cos(a),y+radius*Math.Sin(a),
                circleX+radius*Math.Cos(b),y+radius*Math.Sin(b));
            Line(circleX+radius*.5*Math.Cos(a),y+radius*.5*Math.Sin(a),
                circleX+radius*.5*Math.Cos(b),y+radius*.5*Math.Sin(b));
        }
        Line(circleX-4,y,circleX+4,y,.1);Line(circleX,y-4,circleX,y+4,.1);
        labels.Add(new(new(m.Left+2,m.Top+5),sheet.Standard.ToString(),2.2));
        DrawingTemplateContentLayout.Add(sheet,template,lines,labels);
    }
}
