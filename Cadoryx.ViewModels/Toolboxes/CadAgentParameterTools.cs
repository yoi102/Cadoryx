using System.Text.Json;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadAgentParameterTools
{
    internal static readonly string Schema = BuildSchema();

    internal static async Task<string> ExecuteAsync(AiToolCall call, CadDocumentViewModel? document, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (call.ArgumentsJson.Length > 16384) throw new ArgumentException("Parameter request exceeds its input budget.");
            using var parsed = JsonDocument.Parse(call.ArgumentsJson);
            var args = parsed.RootElement;
            var action = RequiredString(args, "action", 32);
            string[] fields = action switch
            {
                "set_expression" => ["id", "name", "dimension", "expression"],
                "set_reference" => ["id", "name", "dimension", "target"],
                "bind" => ["parameterId", "target"],
                "delete" or "unbind" => ["id"],
                "recompute" => [],
                _ => throw new ArgumentException("Unknown parameter action.")
            };
            RequireFields(args, ["action", "documentId", "stateId", .. fields]);
            var expectation = new DocumentCommandExpectation(new(RequiredGuid(args, "documentId")), new(RequiredGuid(args, "stateId")));
            var id = OptionalGuid(args, "id");
            if (action is "delete" or "unbind") id = RequiredGuid(args, "id");
            var request = new CadParameterEditRequest(action, id,
                action is "set_expression" or "set_reference" ? RequiredString(args, "name", 64) : null,
                action is "set_expression" or "set_reference" ? RequiredString(args, "dimension", 16) : null,
                action == "set_expression" ? RequiredString(args, "expression", 2048) : null,
                action == "bind" ? RequiredGuid(args, "parameterId") : null,
                action is "bind" or "set_reference" ? ReadTarget(args.GetProperty("target")) : null);
            if (document is null) throw new InvalidOperationException("No active document.");
            return await CadParameterAuthoring.ApplyAsync(document, expectation, request, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or
            CadValidationException or KeyNotFoundException or StaleDocumentException)
        {
            return JsonSerializer.Serialize(new { success = false, message = error.Message });
        }
    }

    internal static void RequireFields(JsonElement args, string[] allowed)
    {
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in args.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new ArgumentException("Unknown or duplicate argument: " + property.Name + ".");
    }

    private static CadParameterTarget ReadTarget(JsonElement args)
    {
        var kind = RequiredString(args, "kind", 32);
        string[] extras = kind switch
        {
            "Feature" or "Datum" or "AssemblyPattern" => [],
            "SketchConstraint" => ["constraint"],
            "ExactSketchCurve" => ["curve", "index"],
            "SketchRegion" => ["curve"],
            _ => throw new ArgumentException("Unknown target kind.")
        };
        RequireFields(args, ["kind", "owner", "property", .. extras]);
        int? index = null;
        if (args.TryGetProperty("index", out var value))
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number is < 0 or > 100000)
                throw new ArgumentException("Invalid target index.");
            index = number;
        }
        return new(kind, RequiredGuid(args, "owner"), RequiredString(args, "property", 64),
            kind == "SketchConstraint" ? RequiredGuid(args, "constraint") : null,
            kind is "ExactSketchCurve" or "SketchRegion" ? RequiredGuid(args, "curve") : null, index);
    }

    internal static string RequiredString(JsonElement args, string name, int max) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } text && text.Length <= max && !string.IsNullOrWhiteSpace(text) ? text :
            throw new ArgumentException("Missing or invalid " + name + ".");
    internal static Guid RequiredGuid(JsonElement args, string name) =>
        Guid.TryParseExact(RequiredString(args, name, 36), "D", out var id) && id != Guid.Empty ? id : throw new ArgumentException("Invalid " + name + ".");
    private static Guid? OptionalGuid(JsonElement args, string name) => args.TryGetProperty(name, out _) ? RequiredGuid(args, name) : null;

    private static string BuildSchema()
    {
        object Ref(string name) => new Dictionary<string,object>{["$ref"]="#/$defs/"+name};
        object GuidField() => Ref("id");
        object Text(int max) => new { type = "string", minLength = 1, maxLength = max };
        object EnumField(params string[] values) => new Dictionary<string, object> { ["type"] = "string", ["enum"] = values };
        // Declare field types once; branches retain each kind's exact companion fields.
        object target = new
        {
            type = "object",
            properties = new { kind = EnumField("Feature", "Datum", "AssemblyPattern", "SketchConstraint", "ExactSketchCurve", "SketchRegion"),
                owner = GuidField(), property = Text(64), constraint = GuidField(), curve = GuidField(),
                index = new { type = "integer", minimum = 0, maximum = 100000 } },
            required = new[] { "kind", "owner", "property" }, additionalProperties = false,
            oneOf = new object[]
            {
                new { properties = new { kind = EnumField("Feature", "Datum", "AssemblyPattern"), property = new { type = "string" }, constraint = false, curve = false, index = false } },
                new { properties = new { kind = new { @const = "SketchConstraint" }, property = new { type = "string" }, curve = false, index = false }, required = new[] { "constraint" } },
                new { properties = new { kind = new { @const = "ExactSketchCurve" }, property = new { type = "string" }, constraint = false }, required = new[] { "curve" } },
                new { properties = new { kind = new { @const = "SketchRegion" }, property = new { @const = "Distance" }, constraint = false, index = false }, required = new[] { "curve" } }
            }
        };
        var actions = new[] { "set_expression", "set_reference", "delete", "bind", "unbind", "recompute" };
        var shapes = actions.Select(action =>
        {
            var properties = new Dictionary<string, object> { ["action"] = new { @const = action }, ["documentId"] = new{}, ["stateId"] = new{} };
            var required = new List<string> { "action" };
            if (action is "set_expression" or "set_reference")
            {
                properties["id"] = GuidField(); properties["name"] = Text(64); properties["dimension"] = EnumField("Scalar", "Length", "Angle");
                required.AddRange(["name", "dimension"]);
                var field = action == "set_expression" ? "expression" : "target";
                properties[field] = field == "expression" ? Text(2048) : Ref("target"); required.Add(field);
            }
            if (action is "delete" or "unbind") { properties["id"] = GuidField(); required.Add("id"); }
            if (action == "bind") { properties["parameterId"] = GuidField(); properties["target"] = Ref("target"); required.AddRange(["parameterId", "target"]); }
            return (object)new { type = "object", properties, required, additionalProperties = false };
        }).ToArray();
        return JsonSerializer.Serialize(new Dictionary<string,object>{["type"]="object",["properties"]=new{documentId=GuidField(),stateId=GuidField()},["required"]=new[]{"documentId","stateId"},["oneOf"]=shapes,
            ["$defs"]=new Dictionary<string,object>{["id"]=new{type="string",format="uuid",minLength=36,maxLength=36},["target"]=target}});
    }
}
