using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

const string root = "C:/Users/yoiri/source/repos/Cadoryx";
var rows = new List<object>();
foreach (var config in new[] { "Release", "Debug" })
{
    var directory = Path.Combine(root, "Cadoryx.Tests/bin", config, "net10.0");
    var loader = new ProbeContext(directory);
    var contracts = loader.LoadFromAssemblyPath(Path.Combine(directory, "Cadoryx.AI.Contracts.dll"));
    var agent = loader.LoadFromAssemblyPath(Path.Combine(directory, "Cadoryx.Agent.dll"));
    var viewModels = loader.LoadFromAssemblyPath(Path.Combine(directory, "Cadoryx.ViewModels.dll"));
    var provider = loader.LoadFromAssemblyPath(Path.Combine(directory, "Cadoryx.AI.LmStudio.dll"));
    var toolType = contracts.GetType("Cadoryx.AI.Contracts.AiToolDefinition", true)!;
    var messageType = contracts.GetType("Cadoryx.AI.Contracts.AiChatMessage", true)!;
    var toolsType = viewModels.GetType("Cadoryx.ViewModels.Toolboxes.CadAgentToolset", true)!;
    var tools = RuntimeHelpers.GetUninitializedObject(toolsType);
    var builder = agent.GetType("Cadoryx.Agent.AgentRequestContextBuilder", true)!;
    var build = builder.GetMethod("Build", BindingFlags.Static | BindingFlags.NonPublic)!;
    var present = provider.GetType("Cadoryx.AI.LmStudio.ToolSchemaPresentation", true)!.GetMethod("Create")!;
    foreach (var aggressive in new[] { false, true })
    {
        const string request = "Use cad_drawing contract(view)";
        var selected = ((IEnumerable)toolsType.GetMethod("SelectTools")!.Invoke(tools, [request, aggressive])!).Cast<object>().ToArray();
        var drawing = selected.Single(t => (string)toolType.GetProperty("Name")!.GetValue(t)! == "cad_drawing");
        var original = (JsonElement)toolType.GetProperty("Parameters")!.GetValue(drawing)!;
        foreach (var variant in new[] { "original", "conditional", "action_hint" })
        {
            var schema = JsonNode.Parse(original.GetRawText())!.AsObject();
            if (variant == "conditional")
            {
                schema["if"] = JsonNode.Parse("{\"properties\":{\"action\":{\"const\":\"contract\"}}}");
                schema["then"] = JsonNode.Parse("{\"properties\":{\"documentId\":false,\"stateId\":false}}");
                schema["else"] = JsonNode.Parse("{\"required\":[\"documentId\",\"stateId\"]}");
            }
            if (variant == "action_hint")
                schema["properties"]!["action"]!["description"] = "contract: only action/type, no IDs. Else documentId/stateId required (inspect_sources too).";
            var candidateSchema = JsonSerializer.SerializeToElement(schema);
            var wire = (JsonElement)present.Invoke(null, [candidateSchema])!;
            var copy = Array.CreateInstance(toolType, selected.Length);
            for (int i = 0; i < selected.Length; i++)
                copy.SetValue(ReferenceEquals(selected[i], drawing) ? Activator.CreateInstance(toolType,
                    "cad_drawing", toolType.GetProperty("Description")!.GetValue(drawing), candidateSchema) : selected[i], i);
            var history = Array.CreateInstance(messageType, 1);
            history.SetValue(messageType.GetMethod("User")!.Invoke(null, [request, null]), 0);
            var context = build.Invoke(null, ["CAD assistant", history, copy, 8192, aggressive])!;
            var contextType = context.GetType();
            var accepted = ((IEnumerable)contextType.GetProperty("Tools")!.GetValue(context)!).Cast<object>().ToArray();
            rows.Add(new { config, aggressive, variant, rawCharacters = candidateSchema.GetRawText().Length,
                wireCharacters = wire.GetRawText().Length,
                conditionalSurvivesWire = wire.TryGetProperty("if", out _) || wire.TryGetProperty("then", out _) || wire.TryGetProperty("else", out _),
                actionDescription = wire.GetProperty("properties").GetProperty("action").GetProperty("description").GetString(),
                toolNames = accepted.Select(t => toolType.GetProperty("Name")!.GetValue(t)).ToArray(),
                promptTokens = contextType.GetProperty("EstimatedPromptTokens")!.GetValue(context),
                outputTokens = contextType.GetProperty("MaxOutputTokens")!.GetValue(context),
                payload = new[] { "Cadoryx.Agent.dll", "Cadoryx.ViewModels.dll", "Cadoryx.AI.LmStudio.dll" }.Select(name => new {
                    name, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))) }).ToArray() });
        }
    }
    loader.Unload();
}
var output = Path.Combine(root, "artifacts/drawing-action-schema-diagnostic-1/result.json");
if (File.Exists(output)) throw new InvalidOperationException("Immutable result already exists.");
File.WriteAllText(output, JsonSerializer.Serialize(new { recordedAt = DateTimeOffset.UtcNow, rows,
    nativeJobs = 0, productionChanged = false, modelWorkflow = "NOT RUN", scope = "Schema presentation and existing short-context admission only" }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(output);

sealed class ProbeContext(string directory) : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name)
    {
        var path = Path.Combine(directory, name.Name + ".dll");
        return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
    }
}
