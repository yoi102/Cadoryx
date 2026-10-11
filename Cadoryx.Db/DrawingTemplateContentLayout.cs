using System.Collections.Immutable;
using System.Globalization;

namespace Cadoryx.Db;

internal static class DrawingTemplateContentLayout
{
    internal static void Add(TechnicalDrawingSheet sheet,DrawingSheetTemplate template,
        ImmutableArray<DrawingPageLine>.Builder lines,ImmutableArray<DrawingPageLabel>.Builder labels)
    {
        void Line(double x,double y,double xx,double yy,double width=.18)=>lines.Add(new(new(x,y),new(xx,yy),false,width));
        var m=template.Margins;double right=sheet.WidthMm-m.Right,bottom=sheet.HeightMm-m.Bottom;
        if(template.Zones is {} zones)
        {
            double dx=(right-m.Left)/zones.Columns,dy=(bottom-m.Top)/zones.Rows;
            for(int i=1;i<zones.Columns;i++)
            {double x=m.Left+i*dx;Line(x,m.Top-3,x,m.Top);Line(x,bottom,x,bottom+3);}
            for(int i=1;i<zones.Rows;i++)
            {double y=m.Top+i*dy;Line(m.Left-3,y,m.Left,y);Line(right,y,right+3,y);}
            for(int i=0;i<zones.Columns;i++)
            {
                string text=(zones.ReverseColumns?zones.Columns-i:i+1).ToString(CultureInfo.InvariantCulture);
                double x=m.Left+(i+.5)*dx-1.5;
                labels.Add(new(new(x,m.Top-1.2),text,2.4));labels.Add(new(new(x,bottom+3.5),text,2.4));
            }
            for(int i=0;i<zones.Rows;i++)
            {
                string text=((char)('A'+(zones.ReverseRows?zones.Rows-i-1:i))).ToString();
                double y=m.Top+(i+.5)*dy+.8;
                labels.Add(new(new(m.Left-4,y),text,2.4));labels.Add(new(new(right+1.2,y),text,2.4));
            }
        }
        foreach(var field in template.CustomFields)
        {
            double x=field.PositionMm.X,y=field.PositionMm.Y,w=field.WidthMm,h=field.HeightMm;
            Line(x,y,x+w,y);Line(x+w,y,x+w,y+h);Line(x+w,y+h,x,y+h);Line(x,y+h,x,y);
            labels.Add(new(new(x+2,y+3),field.Caption,1.8,MaxWidthMm:w-4));
            labels.Add(new(new(x+2,y+h-2),field.Value,2.8,MaxWidthMm:w-4));
        }
        foreach(var note in template.Notes)
        {
            var rows=note.Text.Split('\n');
            for(int i=0;i<rows.Length;i++)
                labels.Add(new(new(note.PositionMm.X+1,note.PositionMm.Y+4+i*4),rows[i],2.8,MaxWidthMm:note.WidthMm-2));
        }
    }
}
