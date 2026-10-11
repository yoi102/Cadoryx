using System.Text.Json;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Editor;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadAgentDrawingTools
{
    private static readonly string[] ContractTypes = ["sheet","view","dimension","datum","control_frame","balloon","hole_table","field","note","template","parts_table","zones"];
    internal static readonly string Schema=JsonSerializer.Serialize(new{type="object",properties=new{
        action=new{type="string",description="contract accepts only action/type. All other actions require documentId/stateId.",@enum=new[]{"contract","inspect","inspect_sources","create","edit","reselect","delete","set_parts_table","set_zones","refresh","save_template","apply_template","import_template","detach_coordinates"}},
        type=new{type="string",@enum=ContractTypes},documentId=new{type="string",format="uuid"},stateId=new{type="string",format="uuid"},
        sheetId=new{type="string",format="uuid"},id=new{type="string",format="uuid"},name=new{type="string",maxLength=64,description="Only save_template. Create/edit names belong inside authorJson."},
        path=new{type="string",maxLength=8192},bodyId=new{type="string",format="uuid"},revision=new{type="string",format="uuid"},
        authorJson=new{type="string",minLength=2,maxLength=65536},offset=new{type="integer",minimum=0,maximum=100000},limit=new{type="integer",minimum=1,maximum=50}},
        required=new[]{"action"},additionalProperties=false});
    // Diagnostic only: never fill, remove or substitute caller fields or identities.
    private static string? ArgumentGuide(JsonElement args,string action)
    {
        var allowed=CadDrawingAuthoring.AllowedRequestFields(action,true);
        var seen=new HashSet<string>(StringComparer.Ordinal);var unexpected=new List<string>();
        foreach(var property in args.EnumerateObject())
            if(!allowed.Contains(property.Name,StringComparer.Ordinal)||!seen.Add(property.Name))
            {
                if(unexpected.Count<16)unexpected.Add(property.Name.Length<=80?property.Name:property.Name[..80]+"...");
            }
        var missing=action=="contract"?Array.Empty<string>():new[]{"documentId","stateId"}.Where(name=>
            !args.TryGetProperty(name,out var value)||value.ValueKind!=JsonValueKind.String||
            !Guid.TryParseExact(value.GetString(),"D",out var id)||id==Guid.Empty).ToArray();
        if(missing.Length==0&&unexpected.Count==0)return null;
        var result=new Dictionary<string,object>{["success"]=false,
            ["message"]="Invalid drawing arguments for action="+action+". "+
                (missing.Length==0?"":"Missing or invalid: "+string.Join(", ",missing)+". ")+
                (unexpected.Count==0?"":"Unknown or duplicate: "+string.Join(", ",unexpected)+". ")+
                "contract accepts only action/type; other actions require exact documentId/stateId from cad_status. Outer name is only save_template; create/edit names belong in authorJson.",
            ["missingOrInvalid"]=missing,["unexpectedArguments"]=unexpected,["allowedArguments"]=allowed};
        if(action=="contract")
        {
            // These are existing read-only request shapes, never a repaired or
            // executed caller request. An absent/ambiguous type remains a choice.
            var types=ContractTypes;
            if(!unexpected.Contains("type",StringComparer.Ordinal)&&args.TryGetProperty("type",out var value)&&
                value.ValueKind==JsonValueKind.String&&ContractTypes.Contains(value.GetString(),StringComparer.Ordinal))
                types=[value.GetString()!];
            result["validRequests"]=types.Select(type=>new{action="contract",type}).ToArray();
            result["requestNote"]="Read-only examples. Choose the requested type; contract omits every document, state, owner and source identity. The rejected request was not executed.";
        }
        return JsonSerializer.Serialize(result);
    }
    internal static async Task<string> ExecuteAsync(AiToolCall call,CadDocumentViewModel? document,CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();if(call.ArgumentsJson.Length>131072)throw new ArgumentException("Drawing request exceeds its input budget.");
            using var parsed=JsonDocument.Parse(call.ArgumentsJson);var args=parsed.RootElement;
            var action=RequiredString(args,"action",32);
            if(ArgumentGuide(args,action) is {} guide)return guide;
            DocumentCommandExpectation? expected=action=="contract"?null:new(new(RequiredGuid(args,"documentId")),new(RequiredGuid(args,"stateId")));
            return await CadDrawingAuthoring.ApplyAsync(document,expected,args,true,token);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException or IOException or UnauthorizedAccessException)
        {return JsonSerializer.Serialize(new{success=false,message=e.Message});}
    }
}

internal sealed partial class CadCommandContext
{
    public async Task<CadCommandResult> EditDrawingAsync(string requestJson,CancellationToken cancellationToken=default)
    {
        ValidateInvocation();using var parsed=JsonDocument.Parse(requestJson);
        return new(true,await CadDrawingAuthoring.ApplyAsync(document,expectation,parsed.RootElement,false,cancellationToken));
    }
}
