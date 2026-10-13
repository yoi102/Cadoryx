using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

const string root = "C:/Users/yoiri/source/repos/Cadoryx";
var draft = Path.Combine(root, "artifacts/af1077-drawing-action-1");
var resultPath = Path.Combine(draft, "final-app-managed-result.json");
if (File.Exists(resultPath)) throw new InvalidOperationException("Immutable result exists");
using var frozen = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "artifacts/af1076-root-full-1/freeze.json")));
var nativeNames = frozen.RootElement.GetProperty("native62").EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);
var rows = new List<object>();
var configurations = new List<object>();
foreach (var config in new[] { "Release", "Debug" })
{
    var directory = Path.Combine(draft, "workspace/Cadoryx.wpf/bin", config, "net10.0-windows/win-x64");
    var testsDirectory = Path.Combine(root, "Cadoryx.Tests/bin", config, "net10.0");
    var loader = new ProbeContext(directory, testsDirectory);
    var tests = loader.LoadFromAssemblyPath(Path.Combine(testsDirectory, "Cadoryx.Tests.dll"));
    var vm = loader.LoadFromAssemblyPath(Path.Combine(directory, "Cadoryx.ViewModels.dll"));
    var provider = loader.LoadFromAssemblyPath(Path.Combine(directory, "Cadoryx.AI.LmStudio.dll"));
    var toolset = vm.GetType("Cadoryx.ViewModels.Toolboxes.CadAgentToolset", true)!;
    var definitions = ((IEnumerable)toolset.GetField("Definitions", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!).Cast<object>().ToArray();
    var drawing = definitions.Single(t => (string)t.GetType().GetProperty("Name")!.GetValue(t)! == "cad_drawing");
    var raw = (JsonElement)drawing.GetType().GetProperty("Parameters")!.GetValue(drawing)!;
    var wire = (JsonElement)provider.GetType("Cadoryx.AI.LmStudio.ToolSchemaPresentation", true)!.GetMethod("Create")!.Invoke(null, [raw])!;
    const string explanation = "contract:action/type only. Else IDs required (inspect_sources too).";
    foreach (var schema in new[] { raw, wire })
        if (schema.GetProperty("properties").GetProperty("action").GetProperty("description").GetString() != explanation)
            throw new InvalidOperationException("Candidate action explanation missing on actual wire");
    async Task Check(string typeName, string methodName, params object?[] args)
    {
        var type = tests.GetType("Cadoryx.Tests." + typeName, true)!;
        var method = type.GetMethod(methodName)!;
        var started = Stopwatch.StartNew();
        try
        {
            var result = method.Invoke(Activator.CreateInstance(type), args);
            if (result is Task task) await task;
            rows.Add(new { config, typeName, methodName, args, outcome = "Passed", elapsedMs = started.ElapsedMilliseconds });
        }
        catch (Exception error)
        {
            var actual = error is TargetInvocationException { InnerException: { } inner } ? inner : error;
            rows.Add(new { config, typeName, methodName, args, outcome = "Failed", exception = actual.ToString() });
        }
    }
    await Check("AgentIdentityFieldPresentationTests", "DrawingIdentityRequirementSurvivesActualWirePresentation");
    await Check("ToolPresentationTests", "EveryProductionActionIsVisibleWithExpandedNativeIdentityFields");
    await Check("ToolPresentationTests", "OptionalAndNullableCursorPropertiesRemainVisible");
    await Check("DrawingAgentTests", "DiscoverableStrictContractsAndDefaultContextKeepAllTwelveTools");
    foreach (var aggressive in new[] { false, true })
    {
        await Check("DrawingContractRequestGuideTests", "LeadingContractGuideRetainsEveryDefaultToolAndOriginalOutputSafetyBudgets", aggressive);
        await Check("DrawingInspectionTypeFeedbackTests", "DefaultTwelveToolsAnd8192ContextBudgetKeepTheExplicitInspectionRequirement", aggressive);
        await Check("DrawingSourcePathFeedbackTests", "ExplicitPathSourceFitsOriginalTwelveToolsAndContextBudgets", aggressive);
        await Check("AgentIdentityFieldPresentationTests", "IdentityHintsRetainEveryToolAndOriginal8192OutputSafetyBudget", aggressive);
        await Check("AgentEmptyResponseRecoveryTests", "RecoveryDirectiveRetainsAllTwelveToolsWithTheOriginalDefaultBudgets", aggressive);
        await Check("AgentSuccessfulActionReceiptTests", "LongReadsRetainTheLatestSuccessfulExchangeAndEveryOriginalTool", aggressive);
    }
    foreach (var field in new[] { "documentId", "stateId", "sheetId", "name" })
        await Check("DrawingActionFeedbackTests", "ContractExplainsItsExactFieldSetWithoutRequiringDocumentIdentity", field);
    foreach (var action in new[] { "inspect", "inspect_sources", "create", "edit", "reselect", "delete", "set_parts_table", "set_zones", "save_template", "apply_template", "import_template", "detach_coordinates", "refresh" })
        await Check("DrawingActionFeedbackTests", "MissingIdentityPairIsReportedTogetherForEveryNonContractAction", action);
    foreach (var type in new[] { "sheet", "view", "dimension", "datum", "control_frame", "balloon", "hole_table", "field", "note", "template", "parts_table", "zones" })
        await Check("DrawingContractRequestGuideTests", "EveryExplicitDeclaredTypeProducesOneExactExecutableReadOnlyExample", type);
    var native = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Where(m => nativeNames.Contains(m.ModuleName)).Select(m => m.FileName).ToArray();
    if (native.Length != 0) throw new InvalidOperationException("Managed probe loaded OCCT native modules");
    configurations.Add(new { config, raw = raw.GetRawText(), wire = wire.GetRawText(), nativeModules = native,
        assemblies = loader.Assemblies.Where(a => a.GetName().Name!.StartsWith("Cadoryx", StringComparison.Ordinal)).Select(a => new {
            name = a.GetName().Name, informationalVersion = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, location = a.Location, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray() });
    loader.Unload();
}
File.WriteAllText(resultPath, JsonSerializer.Serialize(new { recordedAt = DateTimeOffset.UtcNow, rows, configurations,
    scope = "Original compiled assertions invoked directly, actual candidate ViewModels; not a full xunit run", productionApplied = false,
    modelWorkflow = "NOT RUN", nativeJobs = 0 }, new JsonSerializerOptions { WriteIndented = true }));
using var parsed = JsonDocument.Parse(File.ReadAllText(resultPath));
var failed = parsed.RootElement.GetProperty("rows").EnumerateArray().Count(r => r.GetProperty("outcome").GetString() != "Passed");
Console.WriteLine(JsonSerializer.Serialize(new { resultPath, cases = rows.Count, failed }));
return failed == 0 ? 0 : 1;

sealed class ProbeContext(string candidateDirectory, string testsDirectory) : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly? Load(AssemblyName name)
    {
        foreach (var directory in new[] { candidateDirectory, testsDirectory })
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(path)) return LoadFromAssemblyPath(path);
        }
        return null;
    }
}
