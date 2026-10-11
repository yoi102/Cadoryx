using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cadoryx.AI.LmStudio;

/// <summary>
/// LM Studio's tool formatter reads property tables but can omit fields present only in
/// combinators or local references. Present their field union; the CAD parser remains the
/// authority for action-specific required fields and unsupported combinations.
/// </summary>
public static class ToolSchemaPresentation
{
    public static JsonElement Create(JsonElement schema)
    {
        var root=JsonNode.Parse(schema.GetRawText()) as JsonObject??throw new ArgumentException("Tool schema must be an object.");
        var expanded=Expand(root,root,0,new HashSet<string>(StringComparer.Ordinal));
        if(expanded["type"]?.GetValue<string>()=="object"&&!expanded.ContainsKey("properties"))expanded["properties"]=new JsonObject();
        return JsonSerializer.SerializeToElement(expanded);
    }
    private static JsonObject Expand(JsonObject node,JsonObject root,int depth,HashSet<string> path)
    {
        if(depth>48)throw new ArgumentException("Tool schema exceeds presentation depth.");
        if(node["$ref"] is JsonValue reference)
        {
            var name=reference.GetValue<string>();
            if(!name.StartsWith("#/$defs/",StringComparison.Ordinal)||root["$defs"]?[name[8..]] is not JsonObject target||!path.Add(name))
                throw new ArgumentException("Unsupported or recursive tool schema reference: "+name);
            try{return Expand(target,root,depth+1,path);}finally{path.Remove(name);}
        }
        var own=new JsonObject();
        foreach(var property in node)
        {
            if(property.Key is "$defs" or "oneOf" or "anyOf" or "allOf" or "not" or "if" or "then" or "else")continue;
            if(property.Key=="properties"&&property.Value is JsonObject properties)
            {
                var fields=new JsonObject();foreach(var field in properties)
                    fields[field.Key]=field.Value is JsonObject value?Expand(value,root,depth+1,path):field.Value?.DeepClone();
                own[property.Key]=fields;
            }
            else if(property.Key is "items" or "additionalProperties"&&property.Value is JsonObject nested)
                own[property.Key]=Expand(nested,root,depth+1,path);
            else own[property.Key]=property.Value?.DeepClone();
        }
        foreach(var keyword in new[]{"oneOf","anyOf","allOf"})
        {
            if(node[keyword] is not JsonArray variants)continue;
            var children=variants.OfType<JsonObject>().Select(v=>Expand(v,root,depth+1,path)).ToArray();
            var variant=Combine(children,keyword!="allOf");own=Combine([own,variant],false);
        }
        return own;
    }
    private static JsonObject Combine(IEnumerable<JsonObject> values,bool alternatives)
    {
        var nodes=values.Where(v=>v.Count>0).ToArray();if(nodes.Length==0)return new();if(nodes.Length==1)return (JsonObject)nodes[0].DeepClone();
        var result=new JsonObject();
        string[] Types(JsonObject n)
        {
            if(n["type"] is JsonArray array)return array.Select(t=>t!.GetValue<string>()).ToArray();
            if(n["type"] is JsonValue type)return [type.GetValue<string>()];
            if(n["properties"] is JsonObject)return ["object"];
            if(n["const"] is JsonValue constant)return [constant.TryGetValue<string>(out _)?"string":constant.TryGetValue<bool>(out _)?"boolean":"number"];
            return [];
        }
        var types=nodes.SelectMany(Types).Distinct(StringComparer.Ordinal).ToArray();
        if(types.Length==1)result["type"]=types[0];else if(types.Length>1)result["type"]=new JsonArray(types.Select(t=>(JsonNode?)JsonValue.Create(t)).ToArray());
        if(nodes.Any(n=>n["properties"] is JsonObject))
        {
            var fields=new JsonObject();var names=nodes.SelectMany(n=>(n["properties"] as JsonObject)?.Select(p=>p.Key)??[]).Distinct(StringComparer.Ordinal);
            foreach(var name in names)fields[name]=Combine(nodes.Select(n=>n["properties"]?[name]).OfType<JsonObject>(),alternatives);
            result["properties"]=fields;result["additionalProperties"]=false;
            var sets=nodes.Select(n=>(n["required"] as JsonArray)?.Select(x=>x!.GetValue<string>()).ToHashSet(StringComparer.Ordinal)??new HashSet<string>(StringComparer.Ordinal)).ToArray();
            var required=new HashSet<string>(sets[0],StringComparer.Ordinal);
            foreach(var set in sets.Skip(1)){if(alternatives)required.IntersectWith(set);else required.UnionWith(set);}
            if(required.Count>0)result["required"]=new JsonArray(required.Order(StringComparer.Ordinal).Select(x=>(JsonNode?)JsonValue.Create(x)).ToArray());
        }
        if(nodes.Any(n=>n["items"] is JsonObject))result["items"]=Combine(nodes.Select(n=>n["items"]).OfType<JsonObject>(),alternatives);
        var enums=nodes.Where(n=>n["const"] is not null||n["enum"] is JsonArray).ToArray();
        if(enums.Length>0&&(!alternatives||enums.Length==nodes.Length))
        {
            var sets=enums.Select(n=>n["enum"] is JsonArray items?items.Select(x=>x?.ToJsonString()??"null").ToHashSet(StringComparer.Ordinal):new HashSet<string>(StringComparer.Ordinal){n["const"]!.ToJsonString()}).ToArray();
            var valid=new HashSet<string>(sets[0],StringComparer.Ordinal);foreach(var set in sets.Skip(1)){if(alternatives)valid.UnionWith(set);else valid.IntersectWith(set);}
            if(valid.Count>0)result["enum"]=new JsonArray(valid.Order(StringComparer.Ordinal).Select(text=>JsonNode.Parse(text)).ToArray());
        }
        foreach(var name in new[]{"minimum","maximum","minLength","maxLength","minItems","maxItems"})
        {
            var numbers=nodes.Select(n=>n[name]).OfType<JsonValue>().Select(v=>v.GetValue<double>()).ToArray();if(numbers.Length==0||alternatives&&numbers.Length!=nodes.Length)continue;
            bool lower=name.StartsWith("min",StringComparison.Ordinal);result[name]=(alternatives==lower)?numbers.Min():numbers.Max();
        }
        foreach(var name in new[]{"format","pattern","description"})
        {
            var metadata=nodes.Select(n=>n[name]).Where(n=>n is not null).ToArray();
            if(metadata.Length>0&&(!alternatives||metadata.Length==nodes.Length)&&metadata.Select(n=>n!.ToJsonString()).Distinct().Count()==1)result[name]=metadata[0]!.DeepClone();
        }
        return result;
    }
}
