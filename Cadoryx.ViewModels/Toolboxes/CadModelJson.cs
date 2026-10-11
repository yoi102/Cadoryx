using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cadoryx.Db;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;
using static Cadoryx.ViewModels.Toolboxes.CadDatumAuthoring;

namespace Cadoryx.ViewModels.Toolboxes;

/// <summary>Only pure authored profile/reference types use the shared codec. Recipes and native caches never do.</summary>
internal static class CadModelJson
{
    internal static readonly JsonSerializerOptions Options=CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var resolver=new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info=>
        {
            if(info.Kind!=JsonTypeInfoKind.Object)return;
            foreach(var property in info.Properties.Where(p=>p.Set is null).ToArray())info.Properties.Remove(property);
        });
        return new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull,TypeInfoResolver=resolver,
            Converters={new JsonStringEnumConverter(allowIntegerValues:false),new IdConverterFactory()}};
    }
    internal static T Read<T>(JsonElement value)
    {
        if(value.ValueKind==JsonValueKind.Null)throw new ArgumentException("Missing author input.");
        Validate(value,typeof(T),0);
        return JsonSerializer.Deserialize<T>(value,Options)??throw new ArgumentException("Missing author input.");
    }
    internal static JsonElement Write<T>(T value)=>JsonSerializer.SerializeToElement(value,Options);
    private static Type? Element(Type type)=>type.IsGenericType&&type.GetGenericTypeDefinition()==typeof(ImmutableArray<>)?type.GetGenericArguments()[0]:null;
    private static (PropertyInfo Property,bool Required)[] Properties(Type type)
    {
        var constructor=type.GetConstructors().OrderByDescending(c=>c.GetParameters().Length).FirstOrDefault();
        var required=constructor?.GetParameters().Where(p=>!p.HasDefaultValue).Select(p=>p.Name!).ToHashSet(StringComparer.OrdinalIgnoreCase)??[];
        return type.GetProperties().Where(p=>p.SetMethod is not null&&p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(p=>(p,required.Contains(p.Name))).ToArray();
    }
    private static void Validate(JsonElement value,Type type,int depth)
    {
        if(depth>24)throw new ArgumentException("Author region nesting exceeds its budget.");
        var underlying=Nullable.GetUnderlyingType(type);
        if(value.ValueKind==JsonValueKind.Null)
        {
            if(underlying is not null||!type.IsValueType)return;
            throw new ArgumentException("Null author value.");
        }
        type=underlying??type;
        if(type==typeof(string))
        {if(value.ValueKind!=JsonValueKind.String||value.GetString()!.Length>8192)throw new ArgumentException("Invalid author text.");return;}
        if(type==typeof(uint))
        {if(value.ValueKind!=JsonValueKind.Number||!value.TryGetUInt32(out _))throw new ArgumentException("Invalid author color.");return;}
        if(type==typeof(Guid)||typeof(ICadId).IsAssignableFrom(type))
        {
            if(value.ValueKind!=JsonValueKind.String||!Guid.TryParseExact(value.GetString(),"D",out var id)||id==Guid.Empty)
                throw new ArgumentException("Author identity requires a nonempty exact GUID.");return;
        }
        if(type==typeof(double))
        {if(value.ValueKind!=JsonValueKind.Number||!value.TryGetDouble(out var n)||!double.IsFinite(n)||Math.Abs(n)>1000000)throw new ArgumentException("Invalid author coordinate.");return;}
        if(type==typeof(int))
        {if(value.ValueKind!=JsonValueKind.Number||!value.TryGetInt32(out var n)||Math.Abs((long)n)>1000000)throw new ArgumentException("Invalid author integer.");return;}
        if(type==typeof(bool))
        {if(value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("Invalid author boolean.");return;}
        if(type.IsEnum)
        {if(value.ValueKind!=JsonValueKind.String||!Enum.GetNames(type).Contains(value.GetString(),StringComparer.Ordinal))throw new ArgumentException("Unknown author enum.");return;}
        if(Element(type) is {} item)
        {
            if(value.ValueKind!=JsonValueKind.Array||value.GetArrayLength()>1050)throw new ArgumentException("Author array exceeds its budget.");
            foreach(var element in value.EnumerateArray())Validate(element,item,depth+1);return;
        }
        var properties=Properties(type);var names=properties.Select(p=>JsonNamingPolicy.CamelCase.ConvertName(p.Property.Name)).ToArray();
        RequireFields(value,names);
        foreach(var (property,required) in properties)
        {
            var name=JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            if(value.TryGetProperty(name,out var field))
            {
                if(field.ValueKind==JsonValueKind.Null&&new NullabilityInfoContext().Create(property).ReadState==NullabilityState.NotNull)
                    throw new ArgumentException("Null author field: "+name+".");
                Validate(field,property.PropertyType,depth+1);
            }
            else if(required)throw new ArgumentException("Missing author field: "+name+".");
        }
    }
    internal static object TypeSchema(Type type,Dictionary<string,object> definitions)
    {
        var nullable=Nullable.GetUnderlyingType(type);if(nullable is not null)return new{anyOf=new[]{TypeSchema(nullable,definitions),new{type="null"}}};
        if(type==typeof(string))return new{type="string",maxLength=8192};
        if(type==typeof(uint))return new{type="integer",minimum=0L,maximum=(long)uint.MaxValue};
        if(type==typeof(Guid)||typeof(ICadId).IsAssignableFrom(type))return new{type="string",format="uuid",minLength=36,maxLength=36};
        if(type==typeof(double))return new{type="number",minimum=-1000000,maximum=1000000};
        if(type==typeof(int))return new{type="integer",minimum=-1000000,maximum=1000000};
        if(type==typeof(bool))return new{type="boolean"};
        if(type.IsEnum)return new Dictionary<string,object>{["type"]="string",["enum"]=Enum.GetNames(type)};
        if(Element(type) is {} item)return new{type="array",items=TypeSchema(item,definitions),maxItems=1050};
        var name=type.Name;
        if(!definitions.ContainsKey(name))
        {
            definitions[name]=new{};var properties=Properties(type);
            definitions[name]=new{type="object",properties=properties.ToDictionary(p=>JsonNamingPolicy.CamelCase.ConvertName(p.Property.Name),p=>
                !p.Property.PropertyType.IsValueType&&new NullabilityInfoContext().Create(p.Property).ReadState==NullabilityState.Nullable?
                    new{anyOf=new[]{TypeSchema(p.Property.PropertyType,definitions),new{type="null"}}}:TypeSchema(p.Property.PropertyType,definitions)),
                required=properties.Where(p=>p.Required).Select(p=>JsonNamingPolicy.CamelCase.ConvertName(p.Property.Name)).ToArray(),additionalProperties=false};
        }
        return new Dictionary<string,object>{["$ref"]="#/$defs/"+name};
    }
    internal static FeatureDefinition Input(DocumentSnapshot doc,DefinitionId part,JsonElement args)
    {
        RequireFields(args,["featureId","revision"]);var id=new FeatureId(RequiredGuid(args,"featureId"));
        var feature=doc.Features.GetValueOrDefault(id)??throw new ArgumentException("Exact input feature is missing.");
        if(feature.PartId!=part||feature.IsStale||feature.IsSuppressed||feature.Result.Revision.Value!=RequiredGuid(args,"revision"))
            throw new ArgumentException("Input feature is foreign, blocked or has an old result revision.");
        return feature;
    }
    internal static object Input(FeatureDefinition feature)=>new{featureId=feature.Id.ToString(),revision=feature.Result.Revision.ToString()};
    internal static ExactTopologySelection Selection(DocumentSnapshot doc,DefinitionId part,JsonElement args,HistoryShapeKind kind=HistoryShapeKind.Face)
    {
        RequireFields(args,["featureId","revision","asset","fingerprint","index","kind","adapterVersion"]);
        var feature=doc.Features.GetValueOrDefault(new(RequiredGuid(args,"featureId")))??throw new ArgumentException("Exact face source is missing.");
        var selection=new ExactTopologySelection(doc.Id,feature.Id,new(RequiredGuid(args,"revision")),new(RequiredString(args,"asset",64)),
            RequiredString(args,"fingerprint",64),Integer(args,"index",null,0,100000),EnumValue<HistoryShapeKind>(args,"kind"),RequiredString(args,"adapterVersion",128));
        selection.Validate();
        if(feature.PartId!=part||feature.IsStale||feature.IsSuppressed||feature.Result.Revision!=selection.Revision||feature.Result.AssetId!=selection.Asset||selection.Kind!=kind)
            throw new ArgumentException("Exact face identity is foreign, blocked or changed.");
        return selection;
    }
    internal static bool Bool(JsonElement args,string name,bool fallback=false)=>args.TryGetProperty(name,out var value)?
        value.ValueKind is JsonValueKind.True or JsonValueKind.False?value.GetBoolean():throw new ArgumentException("Invalid "+name+" boolean."):fallback;
    internal static JsonElement[] Array(JsonElement args,string name,int minimum=0,int maximum=256)
    {
        if(!args.TryGetProperty(name,out var value)||value.ValueKind!=JsonValueKind.Array||value.GetArrayLength()<minimum||value.GetArrayLength()>maximum)
            throw new ArgumentException("Invalid "+name+" array budget.");return value.EnumerateArray().ToArray();
    }
    private sealed class IdConverterFactory:JsonConverterFactory
    {
        public override bool CanConvert(Type type)=>typeof(ICadId).IsAssignableFrom(type);
        public override JsonConverter CreateConverter(Type type,JsonSerializerOptions options)=>(JsonConverter)Activator.CreateInstance(typeof(IdConverter<>).MakeGenericType(type))!;
    }
    private sealed class IdConverter<T>:JsonConverter<T> where T:struct,ICadId
    {
        public override T Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)
        {var id=reader.GetGuid();if(id==Guid.Empty)throw new JsonException("Empty identity.");return (T)Activator.CreateInstance(typeof(T),id)!;}
        public override void Write(Utf8JsonWriter writer,T value,JsonSerializerOptions options)=>writer.WriteStringValue(value.Value);
    }
}
