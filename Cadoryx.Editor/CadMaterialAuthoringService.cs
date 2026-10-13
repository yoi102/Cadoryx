using System.Text.Json;
using System.Text.Json.Serialization;
using Cadoryx.Commands;
using Cadoryx.Db;

namespace Cadoryx.Editor;

/// <summary>Shared headless material authoring; explicit identity and atomic session commands are common to GUI, Agent and CLI.</summary>
public static class CadMaterialAuthoringService
{
    private static readonly JsonSerializerOptions AppearanceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public static readonly string Schema = """
        {"type":"object","properties":{"action":{"type":"string","enum":["contract","inspect","set","delete","assign","texture_pixels"]},"documentId":{"type":"string","format":"uuid"},"stateId":{"type":"string","format":"uuid"},"id":{"type":"string","format":"uuid"},"bodyId":{"type":"string","format":"uuid"},"revision":{"type":"string","format":"uuid"},"materialId":{"type":["string","null"],"format":"uuid"},"name":{"type":"string","minLength":1,"maxLength":200},"densityKgPerM3":{"type":"number","exclusiveMinimum":0},"appearance":{"type":["object","null"]},"width":{"type":"integer","minimum":1,"maximum":16384},"height":{"type":"integer","minimum":1,"maximum":16384},"rgbaBase64":{"type":"string","maxLength":12000},"collection":{"type":"string","enum":["materials","bodies"]},"offset":{"type":"integer","minimum":0,"maximum":100000},"limit":{"type":"integer","minimum":1,"maximum":50}},"required":["action"],"additionalProperties":false}
        """;
    public static JsonDocument ParseRequest(string requestJson)
    {
        ArgumentNullException.ThrowIfNull(requestJson);
        if (requestJson.Length > 16384) throw new ArgumentException("Material request exceeds its input budget.");
        var parsed = JsonDocument.Parse(requestJson, new JsonDocumentOptions { MaxDepth = 32 });
        try
        {
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an object.");
            RejectDuplicateFields(parsed.RootElement); _ = Action(parsed.RootElement); return parsed;
        }
        catch { parsed.Dispose(); throw; }
    }
    public static string Action(JsonElement args) => RequiredString(args,"action",24);
    public static DocumentCommandExpectation Expectation(JsonElement args) =>
        new(new(RequiredGuid(args,"documentId")),new(RequiredGuid(args,"stateId")));
    public static void ValidateContract(JsonElement args) => RequireFields(args,["action"]);
    private static void RequireFields(JsonElement args,string[] allowed)
    {
        if(args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an object.");
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in args.EnumerateObject())
            if(!allowed.Contains(p.Name,StringComparer.Ordinal)||!seen.Add(p.Name)) throw new ArgumentException("Unknown or duplicate argument: "+p.Name+".");
    }
    private static string RequiredString(JsonElement args,string name,int max) =>
        args.ValueKind==JsonValueKind.Object && args.TryGetProperty(name,out var value) && value.ValueKind==JsonValueKind.String &&
        value.GetString() is {Length:>0} text && text.Length<=max && !string.IsNullOrWhiteSpace(text) ? text :
        throw new ArgumentException("Missing or invalid "+name+".");
    private static Guid RequiredGuid(JsonElement args,string name) =>
        Guid.TryParseExact(RequiredString(args,name,36),"D",out var id) && id!=Guid.Empty ? id : throw new ArgumentException("Invalid "+name+".");
    public static async Task<string> ApplyAsync(CadDocumentSession session, DocumentCommandExpectation expected,
        JsonElement args, bool explicitIdentity, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session);ArgumentNullException.ThrowIfNull(expected);
        token.ThrowIfCancellationRequested();
        if (args.ValueKind != JsonValueKind.Object || args.GetRawText().Length > 16384)
            throw new ArgumentException("Expected a material request object within its input budget.");
        RejectDuplicateFields(args); var action = RequiredString(args, "action", 24);
        string[] authorFields = action switch
        {
            "inspect" => ["id", "collection", "offset", "limit"], "set" => ["id", "name", "densityKgPerM3", "appearance"],
            "delete" => ["id"], "assign" => ["bodyId", "revision", "materialId"],
            "texture_pixels" => ["id", "width", "height", "rgbaBase64"], _ => throw new ArgumentException("Unknown material action.")
        };
        RequireFields(args, explicitIdentity ? ["action", "documentId", "stateId", .. authorFields] : ["action", .. authorFields]);
        if(explicitIdentity&&Expectation(args)!=expected)throw new StaleDocumentException();
        var before = session.Snapshot; expected.Validate(before);
        if (session.IsClosing) throw new InvalidOperationException("Document is closing.");
        if (action == "inspect")
        {
            var collection = args.TryGetProperty("collection", out _) ? RequiredString(args, "collection", 24) : "materials";
            if (collection is not ("materials" or "bodies")) throw new ArgumentException("Unknown material inspection collection.");
            Guid? id = args.TryGetProperty("id", out _) ? RequiredGuid(args, "id") : null;
            int offset = Integer(args, "offset", 0, 100000, 0), limit = Integer(args, "limit", 1, 50, 20);
            if (collection == "bodies")
            {
                var bodies = before.Bodies.Values.Where(b => id is null || b.Id.Value == id).OrderBy(b => b.Name).ThenBy(b => b.Id.Value).ToArray();
                return JsonSerializer.Serialize(new { success = true, documentId = before.Id.ToString(), stateId = before.StateId.ToString(),
                    collection, total = bodies.Length, offset, bodies = bodies.Skip(offset).Take(limit).Select(b => new { id = b.Id.ToString(),
                        b.Name, revision = b.Geometry.Revision.ToString(), materialId = b.MaterialId?.ToString(), b.IsVisible }) }, AppearanceJson);
            }
            var all = before.Materials.Values.Where(m => id is null || m.Id.Value == id).OrderBy(m => m.Name).ThenBy(m => m.Id.Value).ToArray();
            return JsonSerializer.Serialize(new { success = true, documentId = before.Id.ToString(), stateId = before.StateId.ToString(),
                collection, total = all.Length, offset, materials = all.Skip(offset).Take(limit).Select(m => new { id = m.Id.ToString(), m.Name,
                    densityKgPerM3 = m.DensityKgPerMm3 * 1e9, appearance = m.Appearance }) }, AppearanceJson);
        }
        if (!before.Extensions.IsDefaultOrEmpty || session.IsRolledBack) throw new InvalidOperationException("The document cannot be edited.");
        ICadDocumentCommand command; MaterialId? resultId = null; bool created = false;
        switch (action)
        {
            case "set":
                created = !args.TryGetProperty("id", out _); resultId = created ? MaterialId.New() : new(RequiredGuid(args, "id"));
                if (!args.TryGetProperty("densityKgPerM3", out var densityValue) || !densityValue.TryGetDouble(out var density))
                    throw new ArgumentException("Missing or invalid densityKgPerM3.");
                CadGuard.Positive(density); if (density / 1e9 <= 0) throw new CadValidationException("Material density underflow.");
                // Full replacement is explicit. Missing appearance preserves an existing appearance; null clears it.
                var appearance = created ? null : before.Materials[resultId.Value].Appearance;
                if (args.TryGetProperty("appearance", out var value)) appearance = value.ValueKind == JsonValueKind.Null ? null :
                    value.Deserialize<CadMaterialAppearance>(AppearanceJson) ?? throw new ArgumentException("Missing material appearance.");
                appearance?.Validate();
                if (appearance?.Texture is { } texture && !before.MaterialTextures().Contains(texture))
                    throw new CadValidationException("Copy a complete texture descriptor from this document, or import pixels explicitly.");
                var material = new CadMaterial(resultId.Value, RequiredString(args, "name", 200), density / 1e9, appearance);
                command = created ? ResourceCommands.AddMaterial(material) : ResourceCommands.UpdateMaterial(material); break;
            case "delete":
                resultId = new(RequiredGuid(args, "id")); command = ResourceCommands.DeleteMaterial(resultId.Value); break;
            case "assign":
                var bodyId = new BodyId(RequiredGuid(args, "bodyId")); var body = before.Bodies[bodyId];
                if (body.Geometry.Revision.Value != RequiredGuid(args, "revision")) throw new StaleDocumentException();
                if (!args.TryGetProperty("materialId", out var target)) throw new ArgumentException("Supply materialId explicitly; null clears it.");
                resultId = target.ValueKind == JsonValueKind.Null ? null : new MaterialId(RequiredGuid(args, "materialId"));
                command = DocumentEdits.SetBodyProperties(bodyId, body.Name, body.Appearance, body.IsVisible, body.LayerId, resultId); break;
            case "texture_pixels":
                resultId = new(RequiredGuid(args, "id")); var current = before.Materials[resultId.Value];
                var rgba = Convert.FromBase64String(RequiredString(args, "rgbaBase64", 12000));
                command = new SetMaterialTextureCommand(current, Integer(args, "width", 1, 16384), Integer(args, "height", 1, 16384), rgba); break;
            default: throw new ArgumentException("Unknown material action.");
        }
        await session.ExecuteAsync(command, token, expected);
        var after = session.Snapshot;
        return JsonSerializer.Serialize(new { success = true, action, id = resultId?.ToString(), documentId = after.Id.ToString(), stateId = after.StateId.ToString(),
            changed = before.StateId != after.StateId, createdIds = created ? new[] { resultId!.Value.ToString() } : Array.Empty<string>() });
    }
    private static int Integer(JsonElement args, string key, int minimum, int maximum, int? fallback = null)
    {
        if (!args.TryGetProperty(key, out var value) && fallback is { } defaultValue) return defaultValue;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= minimum && number <= maximum ? number :
            throw new ArgumentException("Missing or invalid " + key + ".");
    }
    private static void RejectDuplicateFields(JsonElement value, int depth = 0)
    {
        if (depth > 32) throw new ArgumentException("Material request exceeds its nesting budget.");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!seen.Add(property.Name)) throw new ArgumentException("Duplicate author field: " + property.Name); RejectDuplicateFields(property.Value, depth + 1); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateFields(item, depth + 1);
    }
    public static string Contract() => JsonSerializer.Serialize(new
    {
        success = true, actions = new[] { "inspect", "set", "delete", "assign", "texture_pixels" },
        identity = "Mutations require exact documentId/stateId. assign also requires bodyId/current revision. New set omits id; existing set keeps id.",
        inspection = "inspect defaults to collection materials. collection bodies supplies each body id, current revision and materialId. offset/limit page either collection; optional id filters it. Inspection never changes the document.",
        appearance = new CadMaterialAppearance(), densityUnit = "kg/m3", color = "Opaque sRGB uint ARGB; Alpha is separate. Named enum strings only.",
        texture = "Copy complete descriptors only from this document's inspect. texture_pixels uses top-down unpremultiplied RGBA8 and width/height; max 12000 base64 characters. The material appearance must already be enabled. GUI PNG/JPEG import supports the full pixel budget.",
        replacement = "Omitted appearance preserves existing values; explicit null clears. A supplied appearance replaces the complete profile. Imported face styles remain unless OverrideSourceStyles is true. Instance color overrides take priority."
    }, AppearanceJson);
}
