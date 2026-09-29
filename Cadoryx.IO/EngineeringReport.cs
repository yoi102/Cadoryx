using System.Globalization;
using System.Net;
using System.Text;
using Cadoryx.Db;

namespace Cadoryx.IO;

public static class EngineeringReport
{
    private static string N(double? value)=>value?.ToString("G17",CultureInfo.InvariantCulture)??"";
    // Quote all fields, and neutralize spreadsheet formulas even after leading whitespace.
    private static string Cell(string text)
    {
        var trimmed=text.TrimStart();
        if(trimmed.Length>0&&"=+-@".Contains(trimmed[0])||text.StartsWith('\t')||text.StartsWith('\r'))text="'"+text;
        return "\""+text.Replace("\"","\"\"")+"\"";
    }
    public static string Csv(BillOfMaterials bom)
    {
        var b=new StringBuilder("DefinitionId,Name,Quantity,BodiesPerInstance,UnitVolumeMm3,UnitMassKg,TotalMassKg,VisibleOnly,DocumentId,StateId\r\n");
        foreach(var p in bom.Parts)b.AppendLine(string.Join(",",new[]{p.DefinitionId.ToString(),p.Name,p.Quantity.ToString(CultureInfo.InvariantCulture),
            p.BodiesPerInstance.ToString(CultureInfo.InvariantCulture),N(p.UnitVolumeMm3),N(p.UnitMassKg),N(p.TotalMassKg),bom.VisibleOnly.ToString(),bom.DocumentId.ToString(),bom.StateId.ToString()}.Select(Cell)));
        return b.ToString();
    }
    public static string Html(DocumentSnapshot doc,BillOfMaterials bom,CancellationToken token=default)
    {
        string E(object? value)=>WebUtility.HtmlEncode(Convert.ToString(value,CultureInfo.InvariantCulture)??"");
        string Value(double? value)=>value.HasValue?N(value):"未知 / Unknown";
        var b=new StringBuilder("<!doctype html><html lang=\"zh\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\"><title>Cadoryx — ");
        b.Append(E(doc.Name)).Append("</title><style>body{font:14px system-ui,sans-serif;max-width:1200px;margin:36px auto;padding:0 24px;color:#263445;background:#f5f8fb}h1,h2{color:#174d72}table{border-collapse:collapse;width:100%;background:white;margin:16px 0}td,th{border:1px solid #c5d3df;padding:8px;text-align:left;overflow-wrap:anywhere}th{background:#e8f0f6}.note{padding:16px;border-left:4px solid #ca8a25;background:#fff7e6}small{overflow-wrap:anywhere}@media print{body{background:white;margin:0;max-width:none}thead{display:table-header-group}tr{break-inside:avoid}}</style><h1>").Append(E(doc.Name)).Append(" — 工程审阅 / Engineering review</h1>");
        b.Append("<small>Document: ").Append(E(doc.Id)).Append("<br>State: ").Append(E(doc.StateId)).Append("</small><p>对象范围 / Scope: ").Append(bom.VisibleOnly?"文档可见对象 / Document visible":"全部对象 / All objects").Append("</p>");
        b.Append("<p class=\"note\">体积与质量来自当前实体的缓存元数据，未重新执行几何测量；不扣除实例间重叠。缺少密度、非实体或失效特征显示未知。临时隔离/隐藏不改变本报告范围。标注为固定世界坐标，不是关联尺寸。原生文档始终包含完整模型与历史，STEP/IGES/STL 不包含这些审阅信息。<br>Cached geometry values; unknown is not zero. Overlaps are counted per instance. Native document retains the complete model and history. Exchange files omit review annotations.</p>");
        void Table(string title,params string[] headings){b.Append("<h2>").Append(E(title)).Append("</h2><table><thead><tr>");foreach(var h in headings)b.Append("<th>").Append(E(h)).Append("</th>");b.Append("</tr></thead><tbody>");}
        void Row(params object?[] values){token.ThrowIfCancellationRequested();b.Append("<tr>");foreach(var v in values)b.Append("<td>").Append(E(v)).Append("</td>");b.Append("</tr>");}
        void End()=>b.Append("</tbody></table>");
        Table("零件清单 / Parts","Definition ID","名称 / Name","数量 / Qty","Bodies","Volume / mm³","Unit mass / kg","Total mass / kg");
        foreach(var p in bom.Parts)Row(p.DefinitionId,p.Name,p.Quantity,p.BodiesPerInstance,Value(p.UnitVolumeMm3),Value(p.UnitMassKg),Value(p.TotalMassKg));End();
        Table("装配层级 / Occurrences","Depth","Name","Definition","Path","Visible");
        foreach(var o in bom.Occurrences)Row(o.Depth,o.Name,o.DefinitionName,o.Path,o.IsVisible);End();
        Table("工程标注 / Dimensions","Name","Kind","Value","Unit","Visible");
        foreach(var d in doc.Dimensions.Values.OrderBy(d=>d.Name,StringComparer.Ordinal))
        {
            var a=d.First-d.Second;var c=d.Third-d.Second;
            var value=d.Kind==EngineeringDimensionKind.Length?a.Length:Math.Acos(Math.Clamp(a.Dot(c)/(a.Length*c.Length),-1,1))*180/Math.PI;
            Row(d.Name,d.Kind,N(value),d.Kind==EngineeringDimensionKind.Length?"mm":"deg",d.IsVisible);
        }End();
        Table("关联截面 / Sections","Name","Output","Plane normal","Offset / mm","Sources","Status");
        foreach(var s in doc.AssociatedSections.Values.OrderBy(s=>s.FeatureId.ToString(),StringComparer.Ordinal))Row(doc.Features[s.FeatureId].Name,s.Output,s.Normal,N(s.OffsetMm),s.Sources.Length,s.StaleReason??"Current");End();
        Table("装配关系 / Constraints","Name","Kind","Status");
        var occurrences=doc.AssemblyConstraints.Count>0?doc.EnumerateOccurrences().ToDictionary(o=>o.Path):null;
        foreach(var c in doc.AssemblyConstraints.Values.OrderBy(c=>c.Name,StringComparer.Ordinal))Row(c.Name,c.Kind,c.Evaluate(doc,occurrences!).Status);End();
        Table("审阅视图 / Bookmarks","Name","Split","Section","Hidden","Isolated");
        foreach(var v in doc.ReviewBookmarks.Values.OrderBy(v=>v.Name,StringComparer.Ordinal))Row(v.Name,v.SplitView,v.SectionEnabled,v.Hidden.Length,v.Isolated?.Length);End();
        return b.Append("<p>Cadoryx · Report schema 1 · 工程记录；不是二维工程图 / Review record, not a 2D drawing.</p></html>").ToString();
    }
}
