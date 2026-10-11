using System.Text.Json;
using Cadoryx.AI.Contracts;
using Cadoryx.Editor;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadAgentModelTools
{
    internal static readonly string Schema=BuildSchema();
    internal static async Task<string> ExecuteAsync(AiToolCall call,CadDocumentViewModel? document,CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();if(call.ArgumentsJson.Length>32768)throw new ArgumentException("Model request exceeds its input budget.");
            using var parsed=JsonDocument.Parse(call.ArgumentsJson);var args=parsed.RootElement;
            var expected=new DocumentCommandExpectation(new(RequiredGuid(args,"documentId")),new(RequiredGuid(args,"stateId")));
            if(document is null)throw new InvalidOperationException("No active document.");
            return await CadModelAuthoring.ApplyAsync(document,expected,args,true,token);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception e) when(e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or NotSupportedException)
        {return JsonSerializer.Serialize(new{success=false,message=e.Message});}
    }
    private static string BuildSchema()
    {
        object Ref(string name)=>new Dictionary<string,object>{["$ref"]="#/$defs/"+name};
        object Id()=>Ref("id");var actions=new List<object>();
        foreach(var action in new[]{"create","edit","reselect","inspect_contours","inspect_copy_history","detach_profile","detach_datum","set_assembly","detach_assembly"})
        {
            if(action=="reselect")continue;
            var p=new Dictionary<string,object>{["action"]=action=="edit"?new Dictionary<string,object>{["enum"]=new[]{"edit","reselect"}}:new{@const=action},["documentId"]=new{},["stateId"]=new{}};
            var required=new List<string>{"action"};
            void Add(string name,object type){p[name]=type;required.Add(name);}
            if(action=="create"){Add("partId",Id());Add("name",new{type="string",minLength=1,maxLength=64});}
            else if(action=="inspect_contours"){Add("partId",Id());p["offset"]=new{type="integer",minimum=0,maximum=100000};p["limit"]=new{type="integer",minimum=1,maximum=50};}
            else if(action=="set_assembly"){p["id"]=Id();Add("path",new{type="string",minLength=1,maxLength=8192});Add("name",new{type="string",minLength=1,maxLength=64});}
            else{Add("id",Id());if(action!="detach_assembly")Add("revision",Id());}
            if(action is "create" or "edit" or "reselect" or "inspect_contours" or "inspect_copy_history" or "set_assembly")Add("authorJson",Ref("authorJson"));
            actions.Add(new{type="object",properties=p,required,additionalProperties=false});
        }
        return JsonSerializer.Serialize(new Dictionary<string,object>{["type"]="object",["properties"]=new{documentId=Id(),stateId=Id()},["required"]=new[]{"documentId","stateId"},["oneOf"]=actions,["$defs"]=new Dictionary<string,object>{
            ["id"]=new{type="string",format="uuid",minLength=36,maxLength=36},["authorJson"]=new{type="string",minLength=2,maxLength=32768,
                description="Strict JSON author input. Query model_contract by type or copy recipes.items[].edit; linked sources accept identities, never caches."}}});
    }
}
