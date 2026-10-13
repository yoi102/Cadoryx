using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

if (args.Length != 1 || File.Exists(args[0])) throw new ArgumentException("Supply a new output receipt path.");
var started = DateTimeOffset.UtcNow;
var results = new List<object>();
int passed = 0, failed = 0;
foreach (var type in new[] { typeof(Cadoryx.Tests.InspectionDocumentIdentityFeedbackTests), typeof(Cadoryx.Tests.AgentCommandCatalogTests), typeof(Cadoryx.Tests.DrawingLegacyViewPathFeedbackTests), typeof(Cadoryx.Tests.DrawingInspectionTypeFeedbackTests) })
{
    foreach (var method in type.GetMethods().OrderBy(m => m.Name, StringComparer.Ordinal))
    {
        if (method.GetCustomAttribute<FactAttribute>() is null) continue;
        var data = method.GetCustomAttributes<DataAttribute>().SelectMany(a => a.GetData(method)).ToArray();
        if (data.Length == 0) data = [Array.Empty<object>()];
        foreach (var values in data)
        {
            var name = type.FullName + "." + method.Name + "(" + JsonSerializer.Serialize(values) + ")";
            Exception? failure = null;
            try
            {
                var value = method.Invoke(Activator.CreateInstance(type), values);
                if (value is Task task) await task;
                AssertNoNative(); passed++;
            }
            catch (Exception e) { failure = e is TargetInvocationException { InnerException: { } inner } ? inner : e; failed++; }
            results.Add(new { name, passed = failure is null, error = failure?.ToString() });
        }
    }
}
AssertNoNative();
var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)).Select(a => new { name = a.GetName().Name, path = a.Location }).ToArray();
var record = new { started, completed = DateTimeOffset.UtcNow, passed, failed, skipped = 0, results,
    assemblies, actualNativeModules = 0, directCompiledAssertions = true, fullXunitRunner = false, modelRetested = false, productionApplied = false, AFComplete = false };
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[0]))!);
await File.WriteAllTextAsync(args[0], JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(JsonSerializer.Serialize(new { passed, failed, skipped = 0, actualNativeModules = 0 }));
Environment.ExitCode = failed == 0 ? 0 : 1;

static void AssertNoNative()
{
    using var process = Process.GetCurrentProcess();
    if (process.Modules.Cast<ProcessModule>().Any(m => m.ModuleName is "OcctSharp.Native.dll" or "TKBRep.dll"))
        throw new InvalidOperationException("Managed-only feedback probe unexpectedly loaded OCCT.");
}
