using System.Text.Json;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;
using static Cadoryx.ViewModels.Toolboxes.CadDatumAuthoring;

namespace Cadoryx.ViewModels.Toolboxes;

internal static partial class CadDrawingAuthoring
{
    private static readonly JsonSerializerOptions AuthorOptions=new(CadModelJson.Options){DefaultIgnoreCondition=System.Text.Json.Serialization.JsonIgnoreCondition.Never};
    private static JsonElement WriteAuthor(object value)=>JsonSerializer.SerializeToElement(value,AuthorOptions);
    private static readonly Dictionary<string,Type> AuthorTypes=new()
    {
        ["sheet"]=typeof(DrawingSheetAuthor),["view"]=typeof(DrawingViewAuthor),["dimension"]=typeof(DrawingDimensionAuthor),
        ["datum"]=typeof(DrawingDatumAuthor),["control_frame"]=typeof(DrawingControlAuthor),["balloon"]=typeof(DrawingBalloonAuthor),
        ["hole_table"]=typeof(DrawingHoleTableAuthor),["field"]=typeof(DrawingFieldAuthor),["note"]=typeof(DrawingNoteAuthor),
        ["parts_table"]=typeof(DrawingPartsAuthor),["zones"]=typeof(DrawingZonesAuthor)
    };
    internal static string Contract(string? type)
    {
        if(type is null)return JsonSerializer.Serialize(new{success=true,types=AuthorTypes.Keys.Concat(new[]{"template"}),
            actions=new[]{"contract","inspect","inspect_sources","create","edit","reselect","delete","set_parts_table","set_zones","refresh","save_template","apply_template","import_template","detach_coordinates"},
            identity="Copy exact documentId/stateId. create omits id; server assigns every new object/row ID. edit preserves owner/type/sources; attachment changes require reselect. Terminal captures current identity.",
            inspection="inspect requires type; sheetId filters sheets or owns child types. Pagination offset 0..100000, limit 1..50 (default 8). Returned edit is reusable. Templates use save_template/apply_template/import_template; cad_file.template_export writes the native detached format.",
            selection="inspect_sources requires full path/bodyId/revision; includes imported bodies without a feature. Copy selection, never synthesize cached anchors or geometry. History origins remain stored and reverified.",
            units="Sheet positions in mm; dimension angles in degrees; selected body geometry in occurrence-local mm. Hatching angles in degrees. Explicit frame origin/normal/u is part-local.",
            limits="Request 131072 chars, authorJson 65536, terminal 4096. No native evidence/stroke/value/quantity/cache input. PDF via cad_file.pdf requires every view/dimension/annotation/hole row current.",
            behavior="Changes use production history; cancellation/old state/closing before commit retain authors. Read-only and rollback reject writes. Refresh never guesses a missing history. PDF creation does not prove external standard compliance."},CadAuthoringInspection.JsonOptions);
        if(type=="template")return JsonSerializer.Serialize(new{success=true,type,author="Capture a manufacturing sheet using save_template(sheetId,name,id optional exact existing ID); apply_template(sheetId,id) copies content with new IDs; import_template(path) reassigns template ID.",
            file="cad_file action=template_export, exact id, path ending .json; import uses the existing native template file format, not authorJson."});
        var authorType=AuthorTypes.GetValueOrDefault(type)??throw new ArgumentException("Unknown drawing contract type.");
        var definitions=new Dictionary<string,object>();var schema=CadModelJson.TypeSchema(authorType,definitions);
        return JsonSerializer.Serialize(new Dictionary<string,object>{["success"]=true,["type"]=type,["authorSchema"]=schema,["$defs"]=definitions,
            ["notes"]="Pure author input only. Omitted optional nullable fields clear them. For control frames datumSystem is ordered; for planar features opposite and internalWidth are explicit; hole coordinates use frame OR coordinates(binding,revisions). New hole rows omit id; existing rows copy their exact id. View SourcePath is creation/reselect only."},CadAuthoringInspection.JsonOptions);
    }
    private static object Object(DocumentSnapshot doc,TechnicalDrawingSheet? sheet,string type,Guid id)=>type switch
    {
        "sheet"=>doc.DrawingSheets.GetValueOrDefault(id)??throw Missing(),"template"=>doc.DrawingTemplates.GetValueOrDefault(id)??throw Missing(),
        _=>Objects(sheet??throw new ArgumentException("Exact sheet owner is required."),type).SingleOrDefault(o=>Id(o)==id)??throw Missing()
    };
    private static ArgumentException Missing()=>new("Exact drawing object is missing or has a different type/owner.");
    private static IEnumerable<object> Objects(TechnicalDrawingSheet sheet,string type)=>type switch
    {
        "view"=>sheet.Views,"dimension"=>sheet.Dimensions,"datum"=>sheet.DatumSymbols,"control_frame"=>sheet.ControlFrames,
        "balloon"=>sheet.Balloons,"hole_table"=>sheet.HoleTables,"field"=>sheet.Template?.CustomFields??[],"note"=>sheet.Template?.Notes??[],
        _=>throw new ArgumentException("Unknown drawing inspection type.")
    };
    private static Guid Id(object value)=>value switch
    {
        TechnicalDrawingSheet s=>s.Id,TechnicalDrawingView v=>v.Id,TechnicalDrawingDimension d=>d.Id,
        DrawingDatumSymbol d=>d.Id,DrawingFeatureControlFrame f=>f.Id,DrawingAssemblyBalloon b=>b.Id,DrawingHoleTable t=>t.Id,
        DrawingCustomField f=>f.Id,DrawingManufacturingNote n=>n.Id,DrawingTemplateDefinition t=>t.Id,_=>throw Missing()
    };
    private static DrawingSelectionAuthor Selection(DrawingTopologyReference s)=>new(s.Path.ToString(),s.BodyId.Value,s.Revision.Value,s.Asset.ToString(),s.Fingerprint,s.FullTopologyIndex);
    private static DrawingSelectionAuthor Selection(AssemblyDatumReference s)=>new(s.Path.ToString(),s.BodyId.Value,s.Revision.Value,s.Asset.ToString(),s.Fingerprint,s.FullTopologyIndex);
    private static object Author(object value)=>value switch
    {
        TechnicalDrawingSheet s=>new DrawingSheetAuthor(s.Name,s.WidthMm,s.HeightMm,s.Unit,s.Title,s.Standard,s.Author,s.Template),
        DrawingTemplateDefinition t=>new DrawingSheetAuthor(t.Name,t.WidthMm,t.HeightMm,t.Unit,t.Title,t.Standard,t.Author,t.Template),
        TechnicalDrawingView v=>new DrawingViewAuthor(v.Name,v.Kind,v.CenterMm,v.Scale,v.ParentView,null,v.SectionNormal,v.SectionOffsetMm,v.DetailCenter,v.DetailRadius,v.Hatching),
        TechnicalDrawingDimension d=>new DrawingDimensionAuthor(d.ViewId,d.Kind,Selection(d.First),d.TextPositionMm,d.Second is {} second?Selection(second):null,d.Argb,d.UpperTolerance,d.LowerTolerance),
        DrawingDatumSymbol d=>new DrawingDatumAuthor(d.ViewId,d.Name,Selection(d.Source),d.PositionMm,d.FeatureType,d.PlanarSizeEvidence is {} p?Selection(p.OppositeSource):null,d.PlanarSizeEvidence?.Internal??false),
        DrawingFeatureControlFrame f=>new DrawingControlAuthor(f.ViewId,Selection(f.Source),f.PositionMm,f.Characteristic,f.ToleranceMm,f.Target,f.DiameterZone,f.MaterialCondition,
            f.OrderedDatumGroups,f.PlanarSizeEvidence is {} p?Selection(p.OppositeSource):null,f.PlanarSizeEvidence?.Internal??false),
        DrawingAssemblyBalloon b=>new DrawingBalloonAuthor(b.ViewId,Selection(b.Source),b.PositionMm,b.ItemNumber),
        DrawingHoleTable t=>new DrawingHoleTableAuthor(t.ViewId,t.Name,t.CoordinatePath.ToString(),t.CoordinateBinding is null?Frame(t.LocalFrame):null,t.PositionMm,
            [..t.Rows.Select(r=>new DrawingHoleRowAuthor(r.Number,Selection(r.Evidence.Source),r.Id))],t.CoordinateBinding is {} binding?new(binding,t.CoordinateRevisions):null),
        DrawingCustomField f=>new DrawingFieldAuthor(f.Caption,f.Value,f.PositionMm,f.WidthMm,f.HeightMm),
        DrawingManufacturingNote n=>new DrawingNoteAuthor(n.Text,n.PositionMm,n.WidthMm),_=>throw Missing()
    };
    private static string? Stale(object value)=>value switch
    {TechnicalDrawingView v=>v.StaleReason,TechnicalDrawingDimension d=>d.StaleReason,DrawingDatumSymbol d=>d.StaleReason,
        DrawingFeatureControlFrame f=>f.StaleReason,DrawingAssemblyBalloon b=>b.StaleReason,DrawingHoleTable t=>t.StaleReason,_=>null};
    private static string Inspect(DocumentSnapshot doc,JsonElement args)
    {
        // ApplyAsync has already checked the exact document/state before this read-only guide.
        string[] allowedTypes=["sheet","template","view","dimension","datum","control_frame","balloon","hole_table","field","note"];
        if(!args.TryGetProperty("type",out var requested)||requested.ValueKind!=JsonValueKind.String||
            !allowedTypes.Contains(requested.GetString(),StringComparer.Ordinal))
            return JsonSerializer.Serialize(new{success=false,
                message="Missing or invalid drawing inspection type. inspect requires type, documentId and stateId together; choose the exact object kind from allowedTypes. For sheet/view authors use type=sheet/type=view. contract accepts only action/type, without IDs. No type or identity was supplied automatically.",
                missingOrInvalid=new[]{"type"},allowedTypes,requiredArguments=new[]{"action","type","documentId","stateId"},
                contractRequest=new{action="contract"}},CadAuthoringInspection.JsonOptions);
        var type=requested.GetString()!;var offset=Integer(args,"offset",0,0,100000);var limit=Integer(args,"limit",8,1,50);
        Guid? sheetId=args.TryGetProperty("sheetId",out _)?RequiredGuid(args,"sheetId"):null;
        Guid? id=args.TryGetProperty("id",out _)?RequiredGuid(args,"id"):null;
        var sheets=doc.DrawingSheets.Values.Where(s=>sheetId is null||s.Id==sheetId).OrderBy(s=>s.Id).ToArray();
        if(sheetId is not null&&sheets.Length==0)throw Missing();
        (Guid? Owner,object Value)[] objects;
        if(type=="sheet")objects=sheets.Select(s=>((Guid?)null,(object)s)).ToArray();
        else if(type=="template")
        {if(sheetId is not null)throw new ArgumentException("Library templates have no sheet owner.");objects=doc.DrawingTemplates.Values.OrderBy(t=>t.Id).Select(t=>((Guid?)null,(object)t)).ToArray();}
        else objects=sheets.SelectMany(s=>Objects(s,type).Select(v=>((Guid?)s.Id,v))).ToArray();
        objects=objects.Where(o=>id is null||Id(o.Value)==id).ToArray();
        var page=objects.Skip(offset).Take(limit).Select(o=>
        {
            var authored=WriteAuthor(Author(o.Value));
            object? edit=type=="template"?null:new{action="edit",type,documentId=doc.Id.ToString(),stateId=doc.StateId.ToString(),sheetId=o.Owner,id=Id(o.Value),authorJson=authored.GetRawText()};
            object? observations=o.Value switch
            {
                TechnicalDrawingView v=>new{sources=v.Sources.Select(s=>new{path=s.Path.ToString(),bodyId=s.Body.ToString(),featureId=s.Feature?.ToString(),revision=s.Revision.ToString(),asset=s.Asset.ToString()}),strokeCount=v.Strokes.Length},
                TechnicalDrawingDimension d=>new{measuredValue=d.Value,unit=d.Kind==DrawingMeasureKind.Angle?"degree":"mm"},
                DrawingDatumSymbol d=>new{historyOrigin=d.Source.HistoryOrigin is {} origin?Selection(origin):null,oppositeHistoryOrigin=d.PlanarSizeEvidence?.OppositeSource.HistoryOrigin is {} opposite?Selection(opposite):null},
                DrawingFeatureControlFrame f=>new{historyOrigin=f.Source.HistoryOrigin is {} origin?Selection(origin):null,datumBindings=f.DatumBindings.Select(b=>new{b.Id,b.FeatureType,source=Selection(b.Source),opposite=b.OppositeSource is {} p?Selection(p):null})},
                DrawingAssemblyBalloon b=>new{b.Quantity,b.PartName,historyOrigin=b.Source.HistoryOrigin is {} origin?Selection(origin):null},
                DrawingHoleTable t=>new{rows=t.Rows.Select(r=>new{r.Id,r.Number,r.CoordinatesMm,r.StaleReason,r.Evidence.DiameterMm,r.Evidence.DepthMm,r.Evidence.Through,r.Evidence.Head,historyOrigin=r.Evidence.Source.HistoryOrigin is {} origin?Selection(origin):null})},
                _=>null
            };
            return new{id=Id(o.Value),sheetId=o.Owner,type,author=authored,staleReason=Stale(o.Value),observations,edit};
        }).ToArray();
        return JsonSerializer.Serialize(new{success=true,action="inspect",type,documentId=doc.Id.ToString(),stateId=doc.StateId.ToString(),offset,limit,total=objects.Length,
            hasMore=(long)offset+limit<objects.Length,authorState=true,items=page},CadAuthoringInspection.JsonOptions);
    }
    private static async Task<string> InspectSourcesAsync(CadDocumentViewModel document,DocumentCommandExpectation expected,JsonElement args,CancellationToken token)
    {
        using var capture=document.Session.Capture();var doc=capture.Snapshot;expected.Validate(doc);
        var pathText=RequiredString(args,"path",8192);
        OccurrencePath path;
        try { path=Path(doc,pathText); }
        catch(ArgumentException error)
        {
            // The exact document/state guard precedes this guide. These are
            // read-only discovery requests, never replacement source identities.
            return JsonSerializer.Serialize(new{success=false,action="inspect_sources",
                message=error.Message,rejection="drawing_source_path_invalid",
                sourceReads=new[]{new{toolName="cad_inspect",arguments=new{area="occurrences",offset=0,limit=8}},
                    new{toolName="cad_inspect",arguments=new{area="bodies",offset=0,limit=8}}},
                pathRole="Copy one complete occurrences.items[].path including the document root and every instance slot. A slot, body, feature or definition ID alone is not a path.",
                bodyRole="Copy bodyId/revision from actual bodies.items[].id/revision for the selected occurrence's definition. Native inspection still validates membership and revision.",
                nextStep="Read actual sources and fresh cad_status, then explicitly resubmit inspect_sources with exact documentId/stateId/path/bodyId/revision. No source was inferred, repaired, rebound or inspected automatically."},CadAuthoringInspection.JsonOptions);
        }
        var body=new BodyId(RequiredGuid(args,"bodyId"));var revision=new GeometryRevisionId(RequiredGuid(args,"revision"));
        var offset=Integer(args,"offset",0,0,100000);var limit=Integer(args,"limit",8,1,50);
        var resolver=document.GeometryKernel as IDrawingTopologyResolver??throw new NotSupportedException("Original drawing inspector unavailable.");
        var page=await resolver.InspectDrawingTopologyAsync(doc,path,body,revision,document.Session.Assets,offset,limit,token);
        token.ThrowIfCancellationRequested();Guard(document,expected);
        return JsonSerializer.Serialize(new{success=true,action="inspect_sources",documentId=doc.Id.ToString(),stateId=doc.StateId.ToString(),offset,limit,total=page.Total,
            hasMore=(long)offset+limit<page.Total,items=page.Items.Select(s=>new{s.Index,s.Kind,selection=s.Reference is {} r?Selection(r):null,
                geometry=s.Reference?.Geometry.ToString(),s.Diagnostic})},CadAuthoringInspection.JsonOptions);
    }
}
