using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cadoryx.CommandLine;

public sealed partial class CadCommandLineService
{
    private void RegisterFileCommands()
    {
        Register("FILE", "", "FILE CONTRACT | {action:save/export/report/pdf/template_export,path,overwrite?,options?,id?}",
            "Write the exact author snapshot to an absolute path. Read FILE CONTRACT; replacement is explicit. Receipts contain actual bytes, SHA256, source state and diagnostics.", async (args, context, token) =>
        {
            if (args.Length == 1 && args[0].Equals("CONTRACT", StringComparison.OrdinalIgnoreCase)) return context!.InspectFileContract();
            if (args.Length != 1 || !args[0].StartsWith('{')) throw new ArgumentException("Usage: FILE CONTRACT | {action-specific JSON}");
            using (var parsed = JsonDocument.Parse(args[0]))
            {
                if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("FILE requires an object.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in parsed.RootElement.EnumerateObject())
                    if (!seen.Add(field.Name)) throw new ArgumentException("Duplicate argument: " + field.Name);
            }
            var node = JsonNode.Parse(args[0])!.AsObject();
            if (node.TryGetPropertyValue("options", out var options))
            {
                if (node.ContainsKey("optionsJson")) throw new ArgumentException("Provide one options representation.");
                node.Remove("options"); node["optionsJson"] = options?.ToJsonString() ?? throw new ArgumentException("Missing options.");
            }
            return await context!.WriteFileAsync(node.ToJsonString(), token);
        });
    }
}
