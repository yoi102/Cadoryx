using Cadoryx.Db;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Cadoryx.IO;

/// <summary>Vector PDF export; each persisted drawing sheet becomes one physical page.</summary>
public static class TechnicalDrawingPdf
{
    public static void Write(DocumentSnapshot snapshot,string path)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if(snapshot.DrawingSheets.Count==0)throw new CadValidationException("There are no drawing sheets to export.");
        snapshot.Validate();
        if(snapshot.DrawingSheets.Values.SelectMany(s=>s.Views).Any(v=>v.StaleReason is not null)||
           snapshot.DrawingSheets.Values.SelectMany(s=>s.Dimensions).Any(d=>d.StaleReason is not null))
            throw new CadValidationException("Refresh or reselect stale drawing references before PDF export.");
        if(GlobalFontSettings.FontResolver is null)
            GlobalFontSettings.FontResolver=new DrawingFontResolver();
        using var pdf=new PdfDocument();
        pdf.Info.Title=snapshot.Name;
        var sheets=snapshot.DrawingSheets.Values.OrderBy(s=>s.Name,StringComparer.Ordinal).ThenBy(s=>s.Id).ToArray();
        for(int index=0;index<sheets.Length;index++)
        {
            var layout=DrawingPageLayout.Create(sheets[index],index+1,sheets.Length);
            var page=pdf.AddPage();page.Width=XUnit.FromMillimeter(layout.WidthMm);
            page.Height=XUnit.FromMillimeter(layout.HeightMm);
            using var graphics=XGraphics.FromPdfPage(page);
            XPoint Point(Point2d point)=>new(XUnit.FromMillimeter(point.X).Point,XUnit.FromMillimeter(point.Y).Point);
            foreach(var line in layout.Lines)
            {
                var pen=new XPen(XColors.Black,XUnit.FromMillimeter(line.WidthMm).Point);
                if(line.Hidden)pen.DashStyle=XDashStyle.Dash;
                graphics.DrawLine(pen,Point(line.A),Point(line.B));
            }
            foreach(var label in layout.Labels)
            {
                var point=Point(label.Position);
                var color=XColor.FromArgb((int)(label.Argb>>24),(int)((label.Argb>>16)&255),
                    (int)((label.Argb>>8)&255),(int)(label.Argb&255));
                var brush=new XSolidBrush(color);
                foreach(var part in Runs(label.Text))
                {
                    var font=new XFont(part.Cjk?"Drawing CJK":"Drawing Latin",
                        XUnit.FromMillimeter(label.SizeMm).Point,XFontStyleEx.Regular);
                    graphics.DrawString(part.Text,font,brush,point,XStringFormats.BaseLineLeft);
                    point=new(point.X+graphics.MeasureString(part.Text,font).Width,point.Y);
                }
            }
        }
        // Never replace an existing file until the whole PDF has been generated successfully.
        var absolute=Path.GetFullPath(path);var temp=absolute+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            pdf.Save(temp);
            File.Move(temp,absolute,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }

    private static IEnumerable<(string Text,bool Cjk)> Runs(string value)
    {
        if(value.Length==0)yield break;
        int start=0;bool cjk=value[0]>127;
        for(int i=1;i<value.Length;i++)
            if((value[i]>127)!=cjk)
            {yield return(value[start..i],cjk);start=i;cjk=!cjk;}
        yield return(value[start..],cjk);
    }

    private sealed class DrawingFontResolver : IFontResolver
    {
        private static readonly string fontFolder=Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Fonts");
        public FontResolverInfo ResolveTypeface(string familyName,bool isBold,bool isItalic)=>
            new(familyName=="Drawing CJK"?"drawing-cjk":"drawing-latin");
        public byte[] GetFont(string faceName)
        {
            var candidates=faceName=="drawing-cjk"?
                new[]{"simhei.ttf","simsunb.ttf","segoeui.ttf"}:new[]{"segoeui.ttf"};
            var path=candidates.Select(file=>Path.Combine(fontFolder,file)).FirstOrDefault(File.Exists)??
                throw new FileNotFoundException("No compatible system font is available for drawing PDF export.");
            return File.ReadAllBytes(path);
        }
    }
}
