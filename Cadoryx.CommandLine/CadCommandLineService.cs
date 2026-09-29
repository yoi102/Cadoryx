using System.Text;

namespace Cadoryx.CommandLine;

public sealed record CadCommandDescriptor(string Name, string Aliases, string Syntax, string Description);
public sealed record CadCommandResult(bool Success, string Message, bool ClearOutput = false);

public interface ICadCommandContext
{
    string DocumentName { get; }
    int BodyCount { get; }
    int SelectionCount { get; }
    bool CanUndo { get; }
    bool CanRedo { get; }
    IReadOnlyList<string> ListBodies(int limit);
    IReadOnlyList<string> FindBodies(string query, int limit);
    IReadOnlyList<string> ListSelection(int limit);
    CadCommandResult SelectBody(string exactName);
    Task UndoAsync();
    Task RedoAsync();
    void Fit();
    void SetView(string direction);
    void ClearSelection();
    void StartInteractiveTool(string kind);
    bool CancelInteractiveTool();
    Task CreateBoxAsync(string name, double width, double depth, double height, double x, double y, double z);
    Task CreateCylinderAsync(string name, double radius, double height, double x, double y, double z);
}

public interface ICadCommandLineService
{
    IReadOnlyList<CadCommandDescriptor> Commands { get; }
    IReadOnlyList<string> Complete(string prefix, int limit = 12);
    Task<CadCommandResult> ExecuteAsync(string input, ICadCommandContext? context);
}

public sealed class CadCommandLineService : ICadCommandLineService
{
    private readonly List<(CadCommandDescriptor Descriptor, Func<string[], ICadCommandContext?, Task<CadCommandResult>> Run)> commands = [];
    private readonly Dictionary<string, int> lookup = new(StringComparer.OrdinalIgnoreCase);

    public CadCommandLineService()
    {
        Register("HELP", "?", "HELP [command]", "List commands or show syntax.", (args, _) =>
        {
            if (args.Length > 1) return Done(false, "Usage: HELP [command]");
            if (args.Length == 1) return Task.FromResult(lookup.TryGetValue(args[0], out var index)
                ? new CadCommandResult(true, Describe(commands[index].Descriptor))
                : new CadCommandResult(false, $"Unknown command: {args[0]}"));
            return Done(true, string.Join(Environment.NewLine, Commands.Select(Describe)));
        });
        Register("CLEAR", "CLS", "CLEAR", "Clear terminal output.", (args, _) =>
            Task.FromResult(args.Length == 0 ? new CadCommandResult(true, "", true) : new CadCommandResult(false, "Usage: CLEAR")));
        Register("STATUS", "ST", "STATUS", "Show the active document and selection.", (args, context) =>
            args.Length != 0 ? Done(false, "Usage: STATUS") : Done(true,
                $"{context!.DocumentName} | bodies: {context.BodyCount} | selected: {context.SelectionCount}"));
        Register("LIST", "LS", "LIST", "List the first 50 bodies in the active document.", (args, context) =>
            args.Length != 0 ? Done(false, "Usage: LIST") : Done(true,
                string.Join(Environment.NewLine, context!.ListBodies(50)) is { Length: > 0 } listing ? listing : "No bodies."));
        Register("FIND", "", "FIND name", "Find up to 30 matching body instances.", (args, context) =>
            args.Length != 1 || args[0].Length > 200 ? Done(false, "Usage: FIND name") : Done(true,
                string.Join(Environment.NewLine, context!.FindBodies(args[0], 30)) is { Length: > 0 } found ? found : "No matching bodies."));
        Register("SELECTION", "SEL", "SELECTION", "List selected body instances.", (args, context) =>
            args.Length != 0 ? Done(false, "Usage: SELECTION") : Done(true,
                string.Join(Environment.NewLine, context!.ListSelection(50)) is { Length: > 0 } selected ? selected : "Nothing selected."));
        Register("SELECT", "", "SELECT exact-name", "Select a uniquely named body instance; ambiguous names are rejected.", (args, context) =>
        {
            if (args.Length != 1 || args[0].Length > 200) return Done(false, "Usage: SELECT exact-name");
            return Task.FromResult(context!.SelectBody(args[0]));
        });
        Register("UNDO", "U", "UNDO", "Undo the last document edit.", async (args, context) =>
        {
            if (args.Length != 0) return new(false, "Usage: UNDO");
            if (!context!.CanUndo) return new(false, "Nothing to undo.");
            await context.UndoAsync(); return new(true, "Undo completed.");
        });
        Register("REDO", "", "REDO", "Redo the last undone edit.", async (args, context) =>
        {
            if (args.Length != 0) return new(false, "Usage: REDO");
            if (!context!.CanRedo) return new(false, "Nothing to redo.");
            await context.RedoAsync(); return new(true, "Redo completed.");
        });
        Register("FIT", "ZE", "FIT", "Fit visible geometry to the viewport.", (args, context) =>
        {
            if (args.Length != 0) return Done(false, "Usage: FIT");
            context!.Fit(); return Done(true, "View fitted.");
        });
        Register("VIEW", "V", "VIEW TOP|FRONT|RIGHT|BACK|LEFT|BOTTOM|ISO", "Animate to a standard view.", (args, context) =>
        {
            if (args.Length != 1 || !Views.Contains(args[0], StringComparer.OrdinalIgnoreCase))
                return Done(false, "Usage: VIEW TOP|FRONT|RIGHT|BACK|LEFT|BOTTOM|ISO");
            context!.SetView(args[0]); return Done(true, $"View: {args[0].ToUpperInvariant()}");
        });
        Register("DESELECT", "DS", "DESELECT", "Clear the current selection.", (args, context) =>
        {
            if (args.Length != 0) return Done(false, "Usage: DESELECT");
            context!.ClearSelection(); return Done(true, "Selection cleared.");
        });
        Register("TOOL", "", "TOOL BOX|CYLINDER", "Start a mouse-driven solid construction in the viewport.", (args, context) =>
        {
            if (args.Length != 1 || !new[] { "BOX", "CYLINDER" }.Contains(args[0], StringComparer.OrdinalIgnoreCase))
                return Done(false, "Usage: TOOL BOX|CYLINDER");
            context!.StartInteractiveTool(args[0]);
            return Done(true, $"{args[0].ToUpperInvariant()} tool active. Set the shape with the mouse in the viewport.");
        });
        Register("CANCEL", "ESC", "CANCEL", "Cancel the active mouse-driven construction.", (args, context) =>
        {
            if (args.Length != 0) return Done(false, "Usage: CANCEL");
            return context!.CancelInteractiveTool()
                ? Done(true, "Construction cancelled.")
                : Done(false, "No interactive construction is active.");
        });
        Register("BOX", "B", "BOX name width depth height [x y z]", "Create a box in the selected part, dimensions in mm.", async (args, context) =>
        {
            if (!TryPrimitive(args, 3, out var name, out var values)) return new(false, "Usage: BOX name width depth height [x y z]");
            await context!.CreateBoxAsync(name, values[0], values[1], values[2], values[3], values[4], values[5]);
            return new(true, $"Box created: {name}");
        });
        Register("CYLINDER", "CYL", "CYLINDER name radius height [x y z]", "Create a cylinder in the selected part, dimensions in mm.", async (args, context) =>
        {
            if (!TryPrimitive(args, 2, out var name, out var values)) return new(false, "Usage: CYLINDER name radius height [x y z]");
            await context!.CreateCylinderAsync(name, values[0], values[1], values[3], values[4], values[5]);
            return new(true, $"Cylinder created: {name}");
        });
    }

    private static readonly string[] Views = ["TOP", "FRONT", "RIGHT", "BACK", "LEFT", "BOTTOM", "ISO"];
    public IReadOnlyList<CadCommandDescriptor> Commands => commands.Select(x => x.Descriptor).ToArray();
    public IReadOnlyList<string> Complete(string prefix, int limit = 12) => commands
        .Where(x => x.Descriptor.Name.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase))
        .Select(x => x.Descriptor.Name).Take(Math.Clamp(limit, 1, 50)).ToArray();

    public async Task<CadCommandResult> ExecuteAsync(string input, ICadCommandContext? context)
    {
        if (input is null || input.Length > 4096) return new(false, "Command is empty or too long.");
        string[] tokens;
        try { tokens = Tokenize(input); }
        catch (FormatException e) { return new(false, e.Message); }
        if (tokens.Length == 0) return new(false, "Enter a command. Type HELP for a list.");
        if (!lookup.TryGetValue(tokens[0], out var index)) return new(false, $"Unknown command: {tokens[0]}. Type HELP.");
        var command = commands[index];
        if (context is null && command.Descriptor.Name is not ("HELP" or "CLEAR")) return new(false, "No active document.");
        try { return await command.Run(tokens[1..], context); }
        catch (OperationCanceledException) { return new(false, "Command cancelled."); }
        catch (Exception e) { return new(false, e.Message); }
    }

    public static string[] Tokenize(string input)
    {
        var result = new List<string>(); var current = new StringBuilder(); bool quoted = false;
        foreach (char ch in input)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(ch);
        }
        if (quoted) throw new FormatException("Missing closing quote.");
        if (current.Length > 0) result.Add(current.ToString());
        return result.ToArray();
    }

    private static bool TryPrimitive(string[] args, int dimensions, out string name, out double[] values)
    {
        name = args.Length > 0 ? args[0] : ""; values = new double[6];
        if (string.IsNullOrWhiteSpace(name) ||
            args.Length != dimensions + 1 && args.Length != dimensions + 4) return false;
        for (int i = 0; i < dimensions; i++)
        {
            if (!double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out values[i]) || !double.IsFinite(values[i])) return false;
        }
        if (args.Length == dimensions + 4)
            for (int i = 0; i < 3; i++)
                if (!double.TryParse(args[dimensions + 1 + i], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out values[3 + i]) || !double.IsFinite(values[3 + i])) return false;
        return values.Take(dimensions).All(x => x > 0 && x <= 1_000_000);
    }

    private void Register(string name, string aliases, string syntax, string description,
        Func<string[], ICadCommandContext?, Task<CadCommandResult>> run)
    {
        int index = commands.Count; var descriptor = new CadCommandDescriptor(name, aliases, syntax, description);
        foreach (var key in new[] { name }.Concat(aliases.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
        {
            if (!lookup.TryAdd(key, index)) throw new InvalidOperationException($"Duplicate command alias: {key}");
        }
        commands.Add((descriptor, run));
    }

    private static string Describe(CadCommandDescriptor command) => $"{command.Syntax} — {command.Description}";
    private static Task<CadCommandResult> Done(bool success, string message) => Task.FromResult(new CadCommandResult(success, message));
}
