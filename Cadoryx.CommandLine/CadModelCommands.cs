using System.Text.Json.Nodes;

namespace Cadoryx.CommandLine;

public sealed partial class CadCommandLineService
{
    private void RegisterModelCommands()
    {
        Register("MODEL","","MODEL {action,partId/name or id/revision,author JSON} | CONTRACT recipe-type [offset limit]",
            "Create/edit exact common modeling recipes. Read MODEL CONTRACT, RECIPES and SKETCH_GEOMETRY. Raw author JSON is parsed without geometry caches; mm/rad in part-local coordinates.",async(args,context,token)=>
            {
                if(args.Length>=2&&args[0].Equals("CONTRACT",StringComparison.OrdinalIgnoreCase)&&TryPage(args[2..],out var offset,out var limit))
                    return context!.InspectModelContract(args[1],offset,args.Length==2?1:limit);
                if(args.Length!=1||!args[0].StartsWith('{'))throw new ArgumentException("Usage: MODEL {action-specific JSON} | CONTRACT recipe-type");
                // author is a terminal convenience. The shared API uses a bounded authorJson string,
                // so provider schemas remain small and detailed recipe contracts are discovered on demand.
                var node=JsonNode.Parse(args[0]) as JsonObject??throw new ArgumentException("MODEL requires an object.");
                if(node.TryGetPropertyValue("author",out var author))
                {
                    if(node.ContainsKey("authorJson"))throw new ArgumentException("Provide exactly one author representation.");
                    node.Remove("author");node["authorJson"]=author?.ToJsonString()??throw new ArgumentException("Missing author input.");
                }
                return await context!.EditModelAsync(node.ToJsonString(),token);
            });
    }
}
