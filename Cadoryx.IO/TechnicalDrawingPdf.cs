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
           snapshot.DrawingSheets.Values.SelectMany(s=>s.Dimensions).Any(d=>d.StaleReason is not null)||
           snapshot.DrawingSheets.Values.Any(s=>s.DatumSymbols.Any(d=>d.StaleReason is not null)||
               s.ControlFrames.Any(f=>f.StaleReason is not null)||s.Balloons.Any(b=>b.StaleReason is not null)||
               s.HoleTables.Any(t=>t.StaleReason is not null||t.Rows.Any(r=>r.StaleReason is not null))))
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
                var runs=Runs(label.Text).ToArray();double sizeMm=label.SizeMm;
                if(label.MaxWidthMm is {} maxWidth)
                {
                    double width=runs.Sum(part=>graphics.MeasureString(part.Text,new XFont(
                        part.Family,XUnit.FromMillimeter(sizeMm).Point,XFontStyleEx.Regular)).Width);
                    double available=XUnit.FromMillimeter(maxWidth).Point;
                    if(width>available)sizeMm*=available/width;
                    if(sizeMm<1.5)throw new CadValidationException("A title block field is too long to print legibly. Shorten the field.");
                }
                foreach(var part in runs)
                {
                    var font=new XFont(part.Family,
                        XUnit.FromMillimeter(sizeMm).Point,XFontStyleEx.Regular);
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

    private static IEnumerable<(string Text,string Family)> Runs(string value)
    {
        if(value.Length==0)yield break;
        // Technical symbols such as U+2300 are absent from the Chinese font. Keep
        // their actual Unicode characters, with a symbol face, in the vector PDF.
        static string Family(char ch)=>ch is >= '\u2190' and <= '\u2BFF'?"Drawing Symbols":
            ch>='\u2E80'?"Drawing CJK":"Drawing Latin";
        int start=0;var family=Family(value[0]);
        for(int i=1;i<value.Length;i++)
            if(Family(value[i])!=family)
            {yield return(value[start..i],family);start=i;family=Family(value[i]);}
        yield return(value[start..],family);
    }

    private sealed class DrawingFontResolver : IFontResolver
    {
        private static readonly string fontFolder=Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),"Fonts");
        public FontResolverInfo ResolveTypeface(string familyName,bool isBold,bool isItalic)=>
            new(familyName switch{"Drawing CJK"=>"drawing-cjk","Drawing Symbols"=>"drawing-symbols",_=>"drawing-latin"});
        public byte[] GetFont(string faceName)
        {
            var candidates=faceName switch{"drawing-cjk"=>new[]{"simhei.ttf","simsunb.ttf","segoeui.ttf"},
                "drawing-symbols"=>new[]{"seguisym.ttf"},_=>new[]{"segoeui.ttf"}};
            var path=candidates.Select(file=>Path.Combine(fontFolder,file)).FirstOrDefault(File.Exists)??
                throw new FileNotFoundException("No compatible system font is available for drawing PDF export.");
            return File.ReadAllBytes(path);
        }
    }
}
