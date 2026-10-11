using System.Text.Json.Nodes;

namespace Cadoryx.CommandLine;

public sealed partial class CadCommandLineService
{
    private void RegisterDrawingCommands()
    {
        Register("DRAWING","","DRAWING CONTRACT [type] | DRAWING {action,type,sheetId,id,author}",
            "Inspect and author exact complete engineering drawings. Read CONTRACT; use full selections from inspect_sources; cached strokes/measurements are never input.",async(args,context,token)=>
            {
                if(args.Length is 1 or 2&&args[0].Equals("CONTRACT",StringComparison.OrdinalIgnoreCase))
                {
                    var contract=new JsonObject{["action"]="contract"};if(args.Length==2)contract["type"]=args[1];
                    return await context!.EditDrawingAsync(contract.ToJsonString(),token);
                }
                if(args.Length!=1||!args[0].StartsWith('{'))throw new ArgumentException("Usage: DRAWING CONTRACT [type] | DRAWING {action-specific JSON}");
                var node=JsonNode.Parse(args[0]) as JsonObject??throw new ArgumentException("DRAWING requires an object.");
                if(node.TryGetPropertyValue("author",out var author))
                {
                    if(node.ContainsKey("authorJson"))throw new ArgumentException("Provide exactly one author representation.");
                    node.Remove("author");node["authorJson"]=author?.ToJsonString()??throw new ArgumentException("Missing author input.");
                }
                return await context!.EditDrawingAsync(node.ToJsonString(),token);
            });
    }
}
