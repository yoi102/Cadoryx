using System.Globalization;

namespace Cadoryx.CommandLine;

public sealed record CadTimelineEditRequest(string Action, Guid? Id = null, Guid? Revision = null, bool? Suppressed = null,
    Guid? PartId = null, int? Position = null, IReadOnlyList<Guid>? Order = null);

public sealed partial class CadCommandLineService
{
    private void RegisterTimelineCommands() => Register("HISTORY", "", "HISTORY SUPPRESS|RESTORE feature-id revision | REORDER part-id feature-ids... | ROLLBACK part-id position | END",
        "Edit exact feature chronology or the session-only rollback preview; dependencies and current cursor are guarded.", async (args, context, token) =>
        {
            CadTimelineEditRequest request;
            if (args.Length == 1 && args[0].Equals("END", StringComparison.OrdinalIgnoreCase)) request = new("return_end");
            else if (args.Length == 3 && (args[0].Equals("SUPPRESS", StringComparison.OrdinalIgnoreCase) || args[0].Equals("RESTORE", StringComparison.OrdinalIgnoreCase)))
                request = new("set_suppressed", ExactId(args[1]), ExactId(args[2]), args[0].Equals("SUPPRESS", StringComparison.OrdinalIgnoreCase));
            else if (args.Length == 3 && args[0].Equals("ROLLBACK", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var position) && position is >= 0 and <= 1000000)
                request = new("rollback", PartId: ExactId(args[1]), Position: position);
            else if (args.Length is >= 3 and <= 1026 && args[0].Equals("REORDER", StringComparison.OrdinalIgnoreCase))
                request = new("reorder", PartId: ExactId(args[1]), Order: args[2..].Select(ExactId).ToArray());
            else throw new ArgumentException("Invalid HISTORY syntax. Type HELP HISTORY; feature IDs and revisions come from TIMELINE.");
            return await context!.EditTimelineAsync(request, token);
        });
}
