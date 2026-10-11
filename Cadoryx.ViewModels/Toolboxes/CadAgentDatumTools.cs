using System.Text.Json;
using Cadoryx.AI.Contracts;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadAgentDatumTools
{
    internal static readonly string Schema=BuildSchema(true);
    internal static string Contract()=>JsonSerializer.Serialize(new{success=true,schema=JsonDocument.Parse(BuildSchema(false)).RootElement});
    internal static async Task<string> ExecuteAsync(AiToolCall call,CadDocumentViewModel? document,CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();if(call.ArgumentsJson.Length>16384)throw new ArgumentException("Datum request exceeds its input budget.");
            using var parsed=JsonDocument.Parse(call.ArgumentsJson);var args=parsed.RootElement;
            if(RequiredString(args,"action",32)=="contract"){RequireFields(args,["action"]);return Contract();}
            var expected=new DocumentCommandExpectation(new(RequiredGuid(args,"documentId")),new(RequiredGuid(args,"stateId")));
            if(document is null)throw new InvalidOperationException("No active document.");
            return await CadDatumAuthoring.ApplyAsync(document,expected,args,true,token);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception error) when(error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or SketchSolveException or NotSupportedException)
        {return JsonSerializer.Serialize(new{success=false,message=error.Message});}
    }

    private static string BuildSchema(bool compact)
    {
        object Id()=>Ref("id");
        object Text(int max)=>new{type="string",minLength=1,maxLength=max};
        object Number()=>Ref("number");
        object EnumField<T>() where T:struct,Enum=>new Dictionary<string,object>{["type"]="string",["enum"]=Enum.GetNames<T>()};
        object Shape(Dictionary<string,object> properties,params string[] required)=>new{type="object",properties,required,additionalProperties=false};
        object Ref(string name)=>new Dictionary<string,object>{["$ref"]="#/$defs/"+name};
        object Vector()=>Ref("vector");
        object frame=Shape(new(){["origin"]=Vector(),["normal"]=Vector(),["u"]=Vector()},"origin","normal","u");
        object selection=Shape(new(){["featureId"]=Id(),["revision"]=Id(),["asset"]=new{type="string",pattern="^[0-9a-f]{64}$"},
            ["fingerprint"]=new{type="string",pattern="^[0-9a-fA-F]{64}$"},["index"]=new{type="integer",minimum=0,maximum=100000},
            ["kind"]=new Dictionary<string,object>{["type"]="string",["enum"]=new[]{"Face","Edge"}},["adapterVersion"]=Text(128)},
            "featureId","revision","asset","fingerprint","index","kind","adapterVersion");
        var sources=Enum.GetValues<DatumSourceKind>().Select(kind=>
        {
            var p=new Dictionary<string,object>{["kind"]=new{@const=kind.ToString()}};var required=new List<string>{"kind"};
            void Add(string name,object type,bool must=true){p[name]=type;if(must)required.Add(name);}
            switch(kind)
            {
                case DatumSourceKind.Basis:Add("basis",new Dictionary<string,object>{["type"]="string",["enum"]=new[]{"XY","XZ","YZ"}});break;
                case DatumSourceKind.Datum:Add("datum",Id());break;
                case DatumSourceKind.BoxFace:Add("feature",Id());Add("boundary",EnumField<BoxBoundary>());break;
                case DatumSourceKind.ExactFace:Add("selection",Ref("selection"));Add("frame",Ref("frame"));break;
                case DatumSourceKind.Independent:Add("frame",Ref("frame"));break;
                case DatumSourceKind.AxisNormal:Add("datum",Id());Add("point",Id(),false);break;
            }
            return Shape(p,required.ToArray());
        }).ToArray();
        object source=new{type="object",oneOf=sources};
        if(compact)source=Shape(new(){["kind"]=EnumField<DatumSourceKind>(),["basis"]=new{@enum=new[]{"XY","XZ","YZ"}},["datum"]=Id(),["feature"]=Id(),["boundary"]=EnumField<BoxBoundary>(),
            ["selection"]=Ref("selection"),["frame"]=Ref("frame"),["point"]=Id()},"kind");
        object planeAuthor=new{type="object",oneOf=Enum.GetValues<DatumPlaneKind>().Select(kind=>
        {
            var p=new Dictionary<string,object>{["kind"]=new{@const=kind.ToString()},["primary"]=Ref("source")};var r=new List<string>{"kind","primary"};
            if(kind==DatumPlaneKind.Midplane){p["secondary"]=Ref("source");r.Add("secondary");}else p["value"]=Number();
            if(kind==DatumPlaneKind.Angle){p["hinge"]=EnumField<DatumHinge>();p["hingeAxis"]=Id();}
            return Shape(p,r.ToArray());
        }).ToArray()};
        object axisAuthor=new{type="object",properties=new{origin=Vector(),offset=Number(),reverse=new{type="boolean"}},oneOf=Enum.GetValues<DatumAxisKind>().Select(kind=>
        {
            var p=new Dictionary<string,object>{["kind"]=new{@const=kind.ToString()},["origin"]=new{},["offset"]=new{},["reverse"]=new{}};
            var r=new List<string>{"kind"};
            if(kind==DatumAxisKind.Basis)p["basis"]=EnumField<DatumBasisAxis>();
            if(kind is DatumAxisKind.TwoPoints or DatumAxisKind.PlaneIntersection or DatumAxisKind.PlaneNormal){p["first"]=Id();r.Add("first");}
            if(kind is DatumAxisKind.TwoPoints or DatumAxisKind.PlaneIntersection){p["second"]=Id();r.Add("second");}
            if(kind==DatumAxisKind.Analytic){p["selection"]=Ref("selection");r.Add("selection");}
            if(kind==DatumAxisKind.Independent){p["direction"]=Vector();r.Add("direction");}
            return Shape(p,r.ToArray());
        }).ToArray()};
        object pointAuthor=new{type="object",oneOf=Enum.GetValues<DatumPointKind>().Select(kind=>
        {
            var p=new Dictionary<string,object>{["kind"]=new{@const=kind.ToString()}};var r=new List<string>{"kind"};
            if(kind is DatumPointKind.Independent or DatumPointKind.Offset){p["value"]=Vector();r.Add("value");}
            int count=kind switch{DatumPointKind.Offset=>1,DatumPointKind.Midpoint or DatumPointKind.AxisPlane=>2,DatumPointKind.ThreePlanes=>3,_=>0};
            foreach(var field in new[]{"first","second","third"}.Take(count)){p[field]=Id();r.Add(field);}
            if(kind==DatumPointKind.CircleCenter){p["selection"]=Ref("selection");r.Add("selection");}
            return Shape(p,r.ToArray());
        }).ToArray()};
        var properties = new Dictionary<string, object> { ["documentId"] = Id(), ["stateId"] = Id(),
            ["action"] = new { type = "string", @enum = new[] { "contract", "set_plane", "set_axis", "set_point", "delete", "detach", "bind_sketch", "unbind_sketch", "bind_workplane", "unbind_workplane", "inspect_sources" } },
            ["partId"] = Id(), ["name"] = Text(64), ["author"] = new { anyOf = new[] { Ref("planeAuthor"), Ref("axisAuthor"), Ref("pointAuthor") } },
            ["id"] = Id(), ["revision"] = Id() };
        foreach(var action in new[]{"set_plane","set_axis","set_point","delete","detach","bind_sketch","unbind_sketch","bind_workplane","unbind_workplane","inspect_sources"})
        {
            if (action.StartsWith("set_", StringComparison.Ordinal)) continue;
            foreach (var field in CadDatumAuthoring.Fields(action)) properties.TryAdd(field,
                field switch { "path" => Text(8192), "offset" => new { type = "integer", minimum = 0, maximum = 100000 }, "limit" => new { type = "integer", minimum = 1, maximum = 50 }, _ => Id() });
        }
        // Compact field union keeps the entire toolbox in the default context window.
        // CadDatumAuthoring still enforces each action's required/forbidden fields and exact author kind.
        var definitions=new Dictionary<string,object>{["id"]=new{type="string",format="uuid",minLength=36,maxLength=36},
                ["number"]=new{type="number",minimum=-1000000,maximum=1000000},["vector"]=new{type="array",items=Number(),minItems=3,maxItems=3},
                ["frame"]=frame,["selection"]=selection,["source"]=source};
        if(compact)
        {
            properties["author"]=Shape(new(){["kind"]=new{type="string",@enum=Enum.GetNames<DatumPlaneKind>().Concat(Enum.GetNames<DatumAxisKind>()).Concat(Enum.GetNames<DatumPointKind>()).Distinct().ToArray()},
                ["primary"]=Ref("source"),["secondary"]=Ref("source"),["value"]=new{anyOf=new[]{Number(),Vector()}},["hinge"]=EnumField<DatumHinge>(),["hingeAxis"]=Id(),
                ["origin"]=Vector(),["offset"]=Number(),["reverse"]=new{type="boolean"},["basis"]=EnumField<DatumBasisAxis>(),["first"]=Id(),["second"]=Id(),["third"]=Id(),
                ["selection"]=Ref("selection"),["direction"]=Vector()},"kind");
        }
        else{definitions["planeAuthor"]=planeAuthor;definitions["axisAuthor"]=axisAuthor;definitions["pointAuthor"]=pointAuthor;}
        return JsonSerializer.Serialize(new Dictionary<string,object>{["type"]="object",["properties"]=properties,
            ["required"]=new[]{"action"},["additionalProperties"]=false,["$defs"]=definitions});
    }
}
