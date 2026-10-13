using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;
using static Cadoryx.ViewModels.Toolboxes.CadDatumAuthoring;

namespace Cadoryx.ViewModels.Toolboxes;

internal static partial class CadDrawingAuthoring
{
    private static readonly string[] ObjectTypes=["sheet","template","view","dimension","datum","control_frame","balloon","hole_table","field","note"];
    internal static async Task<string> ApplyAsync(CadDocumentViewModel? document,DocumentCommandExpectation? expected,
        JsonElement args,bool identity,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();var action=RequiredString(args,"action",32);
        if(action=="contract")
        {RequireFields(args,["action","type"]);return Contract(args.TryGetProperty("type",out _)?RequiredString(args,"type",32):null);}
        RequireFields(args,AllowedRequestFields(action,identity));
        Guard(document,expected!);var doc=document!;
        if(action=="inspect")return Inspect(doc.Session.Snapshot,args);
        if(action=="inspect_sources")return await InspectSourcesAsync(doc,expected!,args,token);
        if(doc.IsReadOnly)throw new InvalidOperationException("Drawing authoring requires an editable document at the history end.");
        if(action is "create" or "edit" or "reselect" or "delete" && ObjectTypeGuide(args,action,identity) is {} guide)return guide;
        var before=doc.Session.Snapshot;var command=new DrawingCommand(args.Clone(),action,doc.FileService);
        await doc.Session.ExecuteAsync(command,token,expected);
        var after=doc.Session.Snapshot;
        return JsonSerializer.Serialize(new{success=true,action,changed=after.StateId!=before.StateId,
            id=command.ResultId,documentId=after.Id.ToString(),stateId=after.StateId.ToString(),
            createdIds=Identities(after).Except(Identities(before)).Order().ToArray()},CadAuthoringInspection.JsonOptions);
    }
    // Fixed type guidance follows document/state and read-only guards; it never chooses an object.
    private static string? ObjectTypeGuide(JsonElement args,string action,bool identity)
    {
        var allowedTypes=action switch
        {
            "delete"=>ObjectTypes,
            "reselect"=>ObjectTypes.Where(type=>type is "view" or "dimension" or "datum" or "control_frame" or "balloon" or "hole_table").ToArray(),
            _=>ObjectTypes.Where(type=>type!="template").ToArray()
        };
        if(args.TryGetProperty("type",out var type)&&type.ValueKind==JsonValueKind.String&&
            allowedTypes.Contains(type.GetString(),StringComparer.Ordinal))return null;
        return JsonSerializer.Serialize(new{success=false,action,rejection="invalid_drawing_object_type",
            message="Missing or invalid drawing object type. Choose an exact drawing kind from allowedTypes before supplying its owner/object ID. Modeling feature/body/part IDs are not drawing object IDs. Templates use save_template/import_template; parts_table/zones use their dedicated actions. No type, owner or identity was supplied automatically.",
            missingOrInvalid=new[]{"type"},allowedTypes,allowedArguments=AllowedRequestFields(action,identity),
            contractRequest=new{action="contract"}},CadAuthoringInspection.JsonOptions);
    }
    // The same action field catalog governs production parsing and error guidance.
    internal static string[] AllowedRequestFields(string action,bool identity)
    {
        if(action=="contract")return ["action","type"];
        string[] fields=action switch
        {
            "inspect"=>["type","sheetId","id","offset","limit"],
            "inspect_sources"=>["path","bodyId","revision","offset","limit"],
            "create" or "edit" or "reselect"=>["type","sheetId","id","authorJson"],
            "delete"=>["type","sheetId","id"],"set_parts_table" or "set_zones"=>["sheetId","authorJson"],
            "save_template"=>["sheetId","id","name"],"apply_template"=>["sheetId","id"],
            "import_template"=>["path"],"detach_coordinates"=>["sheetId","id"],"refresh"=>[],
            _=>throw new ArgumentException("Unknown drawing action.")
        };
        return ["action",..identity?new[]{"documentId","stateId"}:[],..fields];
    }
    private static void Guard(CadDocumentViewModel? document,DocumentCommandExpectation expected)
    {
        if(document is null||document.IsDetached||document.IsClosingRequested||document.Session.IsClosing)
            throw new InvalidOperationException("The active document is missing, closing or detached.");
        expected.Validate(document.Session.Snapshot);
    }
    private static IEnumerable<Guid> Identities(DocumentSnapshot doc)=>doc.DrawingTemplates.Keys.Concat(doc.DrawingSheets.Values.SelectMany(s=>
        new[]{s.Id}.Concat(s.Views.Select(v=>v.Id)).Concat(s.Dimensions.Select(d=>d.Id)).Concat(s.DatumSymbols.Select(d=>d.Id))
        .Concat(s.ControlFrames.Select(f=>f.Id)).Concat(s.Balloons.Select(b=>b.Id)).Concat(s.HoleTables.Select(t=>t.Id))
        .Concat(s.HoleTables.SelectMany(t=>t.Rows.Select(r=>r.Id))).Concat(s.Template?.CustomFields.Select(f=>f.Id)??[])
        .Concat(s.Template?.Notes.Select(n=>n.Id)??[])));
    private sealed class DrawingCommand(JsonElement args,string action,IDocumentFileService? files):ICadDocumentCommand
    {
        public string Name=>"Drawing "+action;
        public Guid? ResultId {get;private set;}
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        {
            var doc=context.Snapshot;var type=args.TryGetProperty("type",out _)?RequiredString(args,"type",32):null;
            var sheetId=args.TryGetProperty("sheetId",out _)?RequiredGuid(args,"sheetId"):(Guid?)null;
            TechnicalDrawingSheet? sheet=sheetId is {} sid?doc.DrawingSheets.GetValueOrDefault(sid)??throw new ArgumentException("Exact drawing sheet is missing."):null;
            Guid? id=args.TryGetProperty("id",out _)?RequiredGuid(args,"id"):null;
            bool replace=action is "edit" or "reselect";
            object? old=null;
            if(action is "create" or "edit" or "reselect" or "delete")
            {
                if(type is null)throw new ArgumentException("Missing drawing object type.");
                if(type=="sheet"&&sheet is not null||type=="template"&&sheet is not null)
                    throw new ArgumentException("A sheet or library template has no sheetId owner.");
                if(type is not ("sheet" or "template")&&sheet is null)throw new ArgumentException("Exact sheetId owner is required.");
                if(action=="create")
                {if(id is not null)throw new ArgumentException("Creation identities are assigned by the server.");id=Guid.NewGuid();}
                else
                {if(id is null)throw new ArgumentException("Exact object ID is required.");old=Object(doc,sheet,type,id.Value);}
                ResultId=id;
            }
            if(action=="delete")return await Delete(type!,sheetId,id!.Value).PrepareAsync(context,token);
            if(action=="refresh")return await TechnicalDrawingCommands.Refresh().PrepareAsync(context,token);
            if(action=="save_template")
            {
                if(sheet is null)throw new ArgumentException("Exact source sheet is required.");ResultId=id??Guid.NewGuid();
                return await DrawingTemplateCommands.Save(sheet.Id,ResultId.Value,RequiredString(args,"name",64),id is not null).PrepareAsync(context,token);
            }
            if(action=="apply_template")
                return await DrawingTemplateCommands.Apply(sheet?.Id??throw new ArgumentException("Missing sheetId."),id??throw new ArgumentException("Missing template id.")).PrepareAsync(context,token);
            if(action=="import_template")
            {
                var path=RequiredString(args,"path",4096);if(!System.IO.Path.IsPathFullyQualified(path))throw new ArgumentException("An absolute template path is required.");
                var entry=await (files??throw new NotSupportedException("Drawing template import is unavailable.")).ReadDrawingTemplateAsync(path,token);token.ThrowIfCancellationRequested();
                var prepared=await DrawingTemplateCommands.Import(entry).PrepareAsync(context,token);
                ResultId=prepared.Snapshot.DrawingTemplates.Keys.Except(doc.DrawingTemplates.Keys).Single();return prepared;
            }
            if(action=="detach_coordinates")
            {
                var table=sheet?.HoleTables.SingleOrDefault(t=>t.Id==id)??throw new ArgumentException("Exact hole table is missing.");
                if(table.CoordinateBinding is null)return new(doc);
                if(table.StaleReason is not null)throw new ArgumentException("Cannot detach a stale coordinate cache. Explicitly reselect its frame.");
                return await DrawingHoleTableCommands.Put(sheet!.Id,table with{CoordinateBinding=null,CoordinateRevisions=[]},true).PrepareAsync(context,token);
            }
            using var parsed=JsonDocument.Parse(RequiredString(args,"authorJson",65536));var author=parsed.RootElement;
            if(action=="set_parts_table")
            {
                if(sheet is null)throw new ArgumentException("Missing sheetId.");var position=CadModelJson.Read<DrawingPartsAuthor>(author).PositionMm;
                return position==sheet.PartsTablePositionMm?new(doc):await DrawingAnnotationCommands.SetPartsTable(sheet.Id,position).PrepareAsync(context,token);
            }
            if(action=="set_zones")
            {
                if(sheet is null)throw new ArgumentException("Missing sheetId.");var zones=CadModelJson.Read<DrawingZonesAuthor>(author).Zones;
                if(sheet.Template is null)throw new ArgumentException("Zones require a manufacturing template.");
                return zones==sheet.Template.Zones?new(doc):await DrawingTemplateCommands.SetZones(sheet.Id,zones).PrepareAsync(context,token);
            }
            if(type=="template")throw new ArgumentException("Use save_template or import_template for library entries.");
            var authored=Read(type!,author);
            if(replace)
            {
                GuardOwnerType(old!,authored,action);
                // Canonical author equality is independent of immutable-array backing stores and native caches.
                if(action=="edit"&&CadModelJson.Write(authored).GetRawText()==CadModelJson.Write(Author(old!)).GetRawText())return new(doc);
            }
            var production=await BuildAsync(context,sheet,id!.Value,authored,old,action,token);
            return await production.PrepareAsync(context,token);
        }
    }
    private static object Read(string type,JsonElement author)=>type switch
    {
        "sheet"=>CadModelJson.Read<DrawingSheetAuthor>(author),"view"=>CadModelJson.Read<DrawingViewAuthor>(author),
        "dimension"=>CadModelJson.Read<DrawingDimensionAuthor>(author),"datum"=>CadModelJson.Read<DrawingDatumAuthor>(author),
        "control_frame"=>Normalize(CadModelJson.Read<DrawingControlAuthor>(author)),"balloon"=>CadModelJson.Read<DrawingBalloonAuthor>(author),
        "hole_table"=>CadModelJson.Read<DrawingHoleTableAuthor>(author),"field"=>CadModelJson.Read<DrawingFieldAuthor>(author),
        "note"=>CadModelJson.Read<DrawingNoteAuthor>(author),_=>throw new ArgumentException("Unknown drawing author type.")
    };
    private static DrawingControlAuthor Normalize(DrawingControlAuthor value)=>value.DatumSystem.IsDefault?value with{DatumSystem=[]}:value;
    private static ICadDocumentCommand Delete(string type,Guid? sheet,Guid id)=>type switch
    {
        "sheet"=>TechnicalDrawingCommands.DeleteSheet(id),"view"=>TechnicalDrawingCommands.DeleteView(sheet!.Value,id),
        "dimension"=>TechnicalDrawingCommands.DeleteDimension(sheet!.Value,id),"datum" or "control_frame" or "balloon"=>DrawingAnnotationCommands.Delete(sheet!.Value,id),
        "hole_table"=>DrawingHoleTableCommands.Delete(sheet!.Value,id),"field" or "note"=>DrawingTemplateCommands.DeleteContent(sheet!.Value,id),
        "template"=>DrawingTemplateCommands.Delete(id),_=>throw new ArgumentException("Unknown drawing object type.")
    };
}
