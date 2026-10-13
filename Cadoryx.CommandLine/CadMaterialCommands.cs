namespace Cadoryx.CommandLine;

public sealed partial class CadCommandLineService
{
    private void RegisterMaterialCommands()
    {
        Register("MATERIAL", "", "MATERIAL {action-specific JSON}",
            "Inspect/create/edit/delete material appearances, assign exact bodies, or import bounded RGBA pixels. Read MATERIAL {\"action\":\"contract\"}. Captures document/state; no topology or image paths are inferred.",
            async (args, context, token) =>
            {
                if (args.Length != 1 || !args[0].StartsWith('{') || args[0].Length > 16384)
                    throw new ArgumentException("Usage: MATERIAL {action-specific JSON}");
                return await context!.EditMaterialAsync(args[0], token);
            });
    }
}
