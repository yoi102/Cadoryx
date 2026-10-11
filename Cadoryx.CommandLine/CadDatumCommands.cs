using System.Text.Json;

namespace Cadoryx.CommandLine;

public sealed partial class CadCommandLineService
{
    private void RegisterDatumCommands()=>Register("DATUM","",
        "DATUM CONTRACT | {action-specific JSON} | DELETE|DETACH id revision | BIND_SKETCH|UNBIND_SKETCH sketch-id sketch-revision datum-id datum-revision | BIND_WORKPLANE|UNBIND_WORKPLANE datum-id datum-revision exact-path | SOURCES feature-id revision [offset limit]",
        "Create/edit exact planes, axes and points; bind/detach sketches and the workplane. JSON matches cad_datum author fields without documentId/stateId. mm/rad and part-local coordinates; source pages preserve original full-map indices.",
        async(args,context,token)=>
        {
            string json;
            if(args.Length==1&&args[0].Equals("CONTRACT",StringComparison.OrdinalIgnoreCase))json="{\"action\":\"contract\"}";
            else if(args.Length==1&&args[0].StartsWith('{'))json=args[0];
            else if(args.Length==3&&(args[0].Equals("DELETE",StringComparison.OrdinalIgnoreCase)||args[0].Equals("DETACH",StringComparison.OrdinalIgnoreCase)))
                json=JsonSerializer.Serialize(new{action=args[0].ToLowerInvariant(),id=ExactId(args[1]),revision=ExactId(args[2])});
            else if(args.Length==5&&(args[0].Equals("BIND_SKETCH",StringComparison.OrdinalIgnoreCase)||args[0].Equals("UNBIND_SKETCH",StringComparison.OrdinalIgnoreCase)))
                json=JsonSerializer.Serialize(new{action=args[0].ToLowerInvariant(),sketchId=ExactId(args[1]),sketchRevision=ExactId(args[2]),datumId=ExactId(args[3]),datumRevision=ExactId(args[4])});
            else if(args.Length==4&&(args[0].Equals("BIND_WORKPLANE",StringComparison.OrdinalIgnoreCase)||args[0].Equals("UNBIND_WORKPLANE",StringComparison.OrdinalIgnoreCase)))
                json=JsonSerializer.Serialize(new{action=args[0].ToLowerInvariant(),datumId=ExactId(args[1]),datumRevision=ExactId(args[2]),path=args[3]});
            else if(args.Length>=3&&args[0].Equals("SOURCES",StringComparison.OrdinalIgnoreCase)&&TryPage(args[3..],out var offset,out var limit))
                json=JsonSerializer.Serialize(new{action="inspect_sources",featureId=ExactId(args[1]),revision=ExactId(args[2]),offset,limit});
            else throw new ArgumentException("Invalid DATUM syntax. Type HELP DATUM. Read DATUMS and DATUM_BINDINGS for exact identities.");
            return await context!.EditDatumAsync(json,token);
        });
}
