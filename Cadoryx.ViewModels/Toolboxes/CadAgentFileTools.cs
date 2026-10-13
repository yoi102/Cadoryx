using System.Text.Json;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadAgentFileTools
{
    internal static readonly string Schema = BuildSchema();
    internal static async Task<string> ExecuteAsync(AiToolCall call, CadDocumentViewModel? document, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (call.ArgumentsJson.Length > 8192) throw new ArgumentException("File request exceeds its input budget.");
            using var parsed = JsonDocument.Parse(call.ArgumentsJson); var args = parsed.RootElement;
            if (RequiredString(args,"action",16)=="contract")
            { RequireFields(args,["action"]); return Contract(); }
            var missing = new[] { "documentId", "stateId" }.Where(name =>
                !args.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
                !Guid.TryParseExact(value.GetString(), "D", out var id) || id == Guid.Empty).ToArray();
            if (missing.Length > 0)
                return JsonSerializer.Serialize(new { success = false, message = "Missing or invalid " + missing[0] + ".",
                    missingOrInvalid = missing, identitySource = new { toolName = "cad_status", arguments = new { } },
                    nextStep = "Read cad_status and copy its exact documentId and stateId into your original file request. No file write was attempted; never guess or reuse an older state." });
            var expected = new DocumentCommandExpectation(new(RequiredGuid(args,"documentId")),new(RequiredGuid(args,"stateId")));
            return await ApplyAsync(document,expected,args,true,token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or IOException or NotSupportedException or UnauthorizedAccessException)
        { return JsonSerializer.Serialize(new { success=false,message=e.Message }); }
    }
    internal static async Task<string> ApplyAsync(CadDocumentViewModel? document, DocumentCommandExpectation expected,
        JsonElement args, bool tool, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var action=RequiredString(args,"action",16);
        var allowed=action switch { "save" or "pdf"=>new[]{"action","path","overwrite"},
            "template_export"=>["action","path","overwrite","id"],
            "export" or "report"=>["action","path","overwrite","optionsJson"], _=>throw new ArgumentException("Unknown file action.") };
        RequireFields(args,tool?[..allowed,"documentId","stateId"]:allowed);
        var path=RequiredString(args,"path",4096);var overwrite=Bool(args,"overwrite",false);
        CadExportOptions? export=null;DeliveryOptions? report=null;
        if(args.TryGetProperty("optionsJson",out var options))
        {
            if(options.ValueKind!=JsonValueKind.String||options.GetString() is not {Length:>=2 and <=2048} json)
                throw new ArgumentException("Invalid optionsJson.");
            using var settings=JsonDocument.Parse(json);var value=settings.RootElement;
            if(action=="export")
            {
                RequireFields(value,["linearDeflectionMm","angularDeflectionRad","binaryStl","visibleOnly"]);
                export=new(Number(value,"linearDeflectionMm",.1),Number(value,"angularDeflectionRad",.5),Bool(value,"binaryStl",true),Bool(value,"visibleOnly",true));
                export.Validate();
            }
            else
            {
                RequireFields(value,["output","visibleOnly","includeStep","includeIges","includeStl","linearDeflectionMm","angularDeflectionRad"]);
                var output=RequiredString(value,"output",24);
                if(!Enum.TryParse<DeliveryOutput>(output,false,out var kind)||!Enum.IsDefined(kind)||kind.ToString()!=output)
                    throw new ArgumentException("Unknown report output.");
                report=new(kind,Bool(value,"visibleOnly",true),Bool(value,"includeStep",true),Bool(value,"includeIges",false),Bool(value,"includeStl",false),Number(value,"linearDeflectionMm",.1),Number(value,"angularDeflectionRad",.5));
                report.Validate();
            }
        }
        if(action=="report"&&report is null)throw new ArgumentException("report requires optionsJson.output.");
        if(document is null||document.IsDetached||document.IsClosingRequested)throw new InvalidOperationException("No active document, or it is closing.");
        if(action=="pdf"&&document.IsReadOnly)throw new InvalidOperationException("Drawing PDF requires an editable author document at the history end.");
        var service=document.FileService??throw new NotSupportedException("File output is unavailable in this context.");
        var receipt=await document.Session.WriteFileAsync(service,new(action,path,overwrite,export,report,action=="template_export"?RequiredGuid(args,"id"):null),expected,token);
        var diagnostics=receipt.Diagnostics.Take(8).Select(d=>new{code=Clip(d.Code,100),message=Clip(d.Message,400),d.IsError}).ToArray();
        return JsonSerializer.Serialize(new{success=true,changed=false,published=true,
            receipt=new{receipt.Kind,receipt.Path,receipt.Format,receipt.Bytes,receipt.Sha256,
                documentId=receipt.DocumentId.ToString(),stateId=receipt.StateId.ToString(),diagnostics,
                diagnosticCount=receipt.Diagnostics.Length,diagnosticsTruncated=receipt.Diagnostics.Length>8||receipt.Diagnostics.Any(d=>d.Code.Length>100||d.Message.Length>400)},
            authorState=true,currentStateId=document.Session.Snapshot.StateId.ToString(),
            savedCurrentState=action=="save"&&!document.IsDetached&&!document.Session.IsClosing&&!document.Session.IsDirty,
            scope=action=="save"?"Complete author document, including history; rollback preview is not saved.":
                "Captured author snapshot. Document visibility is used; temporary viewport filters are excluded. Diagnostics and measured report boundaries apply."},new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    internal static string Contract()=>JsonSerializer.Serialize(new { success=true,actions=new[]{"save","export","report","pdf","template_export"},
        identity="Every write requires actual documentId/stateId; terminal captures them. Absolute path; overwrite defaults false. File operations add no undo entry.",
        save=new{extension=".cadoryx",scope="Complete author snapshot; allowed during rollback. Retains last captured saved state when edits occur during writing."},
        pdf=new{extension=".pdf",scope="All drawing sheets, one physical page each, vectors. Every view/dimension/manufacturing annotation/hole row must be current; history end and editable author document required."},
        templateExport=new{extension=".json",id="Exact template ID from cad_drawing.inspect type=template",format="Detached native v1 author format. Import using cad_drawing.import_template."},
        export=new{extensions=new[]{".step",".stp",".iges",".igs",".stl"},options=new{linearDeflectionMm=.1,angularDeflectionRad=.5,binaryStl=true,visibleOnly=true}},
        report=new{outputs=new[]{"Package","BomCsv","ReviewHtml","ExchangeAudit"},extensions=new[]{".zip",".csv",".html",".zip"},options=new{output="ExchangeAudit",visibleOnly=true,includeStep=true,includeIges=false,includeStl=false,linearDeflectionMm=.1,angularDeflectionRad=.5}},
        optionsInput="Strict optionsJson string; terminal accepts options object. Unknown/duplicate fields and numeric enums rejected.",
        result="Receipt includes captured document/state, actual path/format/bytes/SHA256 and diagnostics. Publication is atomic; cancellation after publication still returns success.",
        limits="Request 8192 chars, optionsJson 2048, terminal 4096. Export/report require history end. Compatibility and standards compliance are not inferred from file creation."});
    private static string Clip(string text,int max)=>text.Length<=max?text:text[..max]+" [truncated]";
    private static string BuildSchema()
    {
        object Id()=>new{type="string",format="uuid",minLength=36,maxLength=36};
        var contract=new{type="object",properties=new{action=new{@const="contract"}},required=new[]{"action"},additionalProperties=false};
        var write=new{type="object",properties=new{action=new{type="string",@enum=new[]{"save","export","report","pdf","template_export"}},documentId=Id(),stateId=Id(),id=Id(),
            path=new{type="string",minLength=1,maxLength=4096},overwrite=new{type="boolean"},optionsJson=new{type="string",minLength=2,maxLength=2048}},
            required=new[]{"action","documentId","stateId","path"},additionalProperties=false,
            allOf=new object[]{new{@if=new{properties=new{action=new{@enum=new[]{"save","pdf","template_export"}}}},then=new{@not=new{required=new[]{"optionsJson"}}}},
                new{@if=new{properties=new{action=new{@const="report"}}},then=new{required=new[]{"optionsJson"}}},
                new{@if=new{properties=new{action=new{@const="template_export"}}},then=new{required=new[]{"id"}},@else=new{@not=new{required=new[]{"id"}}}}}};
        return JsonSerializer.Serialize(new{type="object",properties=new{},oneOf=new object[]{contract,write}});
    }
    private static bool Bool(JsonElement value,string field,bool fallback)=>!value.TryGetProperty(field,out var found)?fallback:
        found.ValueKind is JsonValueKind.True or JsonValueKind.False?found.GetBoolean():throw new ArgumentException("Invalid "+field+".");
    private static double Number(JsonElement value,string field,double fallback)=>!value.TryGetProperty(field,out var found)?fallback:
        found.ValueKind==JsonValueKind.Number&&found.TryGetDouble(out var n)&&double.IsFinite(n)?n:throw new ArgumentException("Invalid "+field+".");
}

internal sealed partial class CadCommandContext
{
    public CadCommandResult InspectFileContract()=>new(true,CadAgentFileTools.Contract());
    public async Task<CadCommandResult> WriteFileAsync(string requestJson,CancellationToken cancellationToken=default)
    {
        ValidateInvocation();using var parsed=JsonDocument.Parse(requestJson);
        return new(true,await CadAgentFileTools.ApplyAsync(document,expectation,parsed.RootElement,false,cancellationToken));
    }
}
