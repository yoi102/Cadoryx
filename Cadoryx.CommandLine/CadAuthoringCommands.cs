using System.Globalization;

namespace Cadoryx.CommandLine;

public sealed record CadParameterTarget(string Kind, Guid Owner, string Property,
    Guid? Constraint = null, Guid? Curve = null, int? Index = null);
public sealed record CadParameterEditRequest(string Action, Guid? Id = null, string? Name = null,
    string? Dimension = null, string? Expression = null, Guid? ParameterId = null, CadParameterTarget? Target = null);

public sealed partial class CadCommandLineService
{
    private void RegisterAuthoringCommands()
    {
        RegisterTimelineCommands();
        RegisterDatumCommands();
        RegisterModelCommands();
        RegisterDrawingCommands();
        RegisterFileCommands();
        RegisterMaterialCommands();
        RegisterWorkspaceCommands();
        Register("SKETCH_REGION_SOURCES","","SKETCH_REGION_SOURCES {action:reselect_region_sources,sketchId,revision,id,sources}",
            "Explicit complete whole-region source map: id=group GUID, sources=ordered arrays for all loops and slots. Retains only proved author roles and rejects retained region parameters. Uses captured document/state.",async(args,context,token)=>
            {if(args.Length!=1||!args[0].StartsWith('{')||args[0].Length>16384)throw new ArgumentException("Usage: SKETCH_REGION_SOURCES {action-specific JSON}");return await context!.ReselectSketchRegionSourcesAsync(args[0],token);});
        Register("SKETCH_OFFSET","","SKETCH_OFFSET {action,sketchId,revision,seeds,distanceMm,optional join}",
            "Create an independent exact loop/region offset. Actions: offset_sketch_loop (one seed), offset_sketch_regions. Join: Miter (default), Round or Bevel; returned boundary IDs are new authors.",async(args,context,token)=>
            {if(args.Length!=1||!args[0].StartsWith('{')||args[0].Length>16384)throw new ArgumentException("Usage: SKETCH_OFFSET {action-specific JSON}");return await context!.OffsetSketchAsync(args[0],token);});
        Register("SKETCH_FINITE_CONTACT","","SKETCH_FINITE_CONTACT {action-specific JSON}","Set/delete contact at explicit author curve interior parameters and tangent direction. Query SKETCH_GEOMETRY for exact identities.",async(args,context,token)=>
            {if(args.Length!=1||!args[0].StartsWith('{')||args[0].Length>16384)throw new ArgumentException("Usage: SKETCH_FINITE_CONTACT {action-specific JSON}");return await context!.EditSketchFiniteContactAsync(args[0],token);});
        Register("SKETCH_CURVATURE","","SKETCH_CURVATURE {action,id,revision,curveA,curveB,optional constraintId/enabled}",
            "Set or delete endpoint G2 through an atomic sketch solve. Read SKETCH_GEOMETRY for explicit curve IDs and signed curvature.",async(args,context,token)=>
            {if(args.Length!=1||!args[0].StartsWith('{')||args[0].Length>16384)throw new ArgumentException("Usage: SKETCH_CURVATURE {action-specific JSON}");return await context!.EditSketchCurvatureAsync(args[0],token);});
        Register("SKETCH_CONTACT","","SKETCH_CONTACT {action,id,revision,curveA,curveB,branch,optional constraintId/enabled}",
            "Set or delete finite circular contact through an atomic sketch solve. Query SKETCH_GEOMETRY for exact identities and branches.",async(args,context,token)=>
            {if(args.Length!=1||!args[0].StartsWith('{')||args[0].Length>16384)throw new ArgumentException("Usage: SKETCH_CONTACT {action-specific JSON}");return await context!.EditSketchContactAsync(args[0],token);});
        foreach (var (name, area) in new[] { ("PARAMETERS", "parameters"), ("BINDINGS", "parameter_bindings"),
            ("TARGETS", "parameter_targets"), ("DATUMS", "datums"), ("DATUM_BINDINGS", "datum_associations"), ("RECIPES", "recipes"), ("SKETCH_GEOMETRY", "sketch_geometry"), ("ASSEMBLY_PATTERNS", "assembly_patterns") })
            Register(name, "", name + " [offset limit]", "Read a stable authoring page with exact IDs and mm/rad units.", (args, context) =>
            {
                if (!TryPage(args, out var offset, out var limit)) return Done(false, "Usage: " + name + " [offset limit] (offset 0–100000, limit 1–50)");
                return Task.FromResult(context!.InspectAuthoring(area, offset, limit));
            });
        Register("TIMELINE", "", "TIMELINE part-id [offset limit]", "Read chronological feature identities of one exact part and the session rollback cursor.", (args, context) =>
        {
            if (args.Length < 1 || !Guid.TryParse(args[0], out var part) || part == Guid.Empty ||
                !TryPage(args[1..], out var offset, out var limit)) return Done(false, "Usage: TIMELINE part-id [offset limit]");
            return Task.FromResult(context!.InspectAuthoring("timeline", offset, limit, part));
        });
        Register("PARAM", "", "PARAM SET NEW|id name SCALAR|LENGTH|ANGLE \"expression\" | DELETE id | BIND parameter-id kind owner-id property [companion-id [index]] | UNBIND binding-id | RECOMPUTE",
            "Edit named parameters and exact author fields through one atomic production recompute. Angles evaluate in rad, lengths in mm.", async (args, context, token) =>
            await context!.EditParameterAsync(ParseParameter(args), token));
    }

    private static CadParameterEditRequest ParseParameter(string[] args)
    {
        if (args.Length == 1 && args[0].Equals("RECOMPUTE", StringComparison.OrdinalIgnoreCase)) return new("recompute");
        if (args.Length == 2 && args[0].Equals("DELETE", StringComparison.OrdinalIgnoreCase)) return new("delete", ExactId(args[1]));
        if (args.Length == 2 && args[0].Equals("UNBIND", StringComparison.OrdinalIgnoreCase)) return new("unbind", ExactId(args[1]));
        if (args.Length == 5 && args[0].Equals("SET", StringComparison.OrdinalIgnoreCase))
        {
            var dimension = new[] { "Scalar", "Length", "Angle" }.SingleOrDefault(s => s.Equals(args[3], StringComparison.OrdinalIgnoreCase)) ??
                throw new ArgumentException("Dimension must be SCALAR, LENGTH or ANGLE.");
            return new("set_expression", args[1].Equals("NEW", StringComparison.OrdinalIgnoreCase) ? null : ExactId(args[1]), args[2], dimension, args[4]);
        }
        if (args.Length is >= 5 and <= 7 && args[0].Equals("BIND", StringComparison.OrdinalIgnoreCase))
        {
            var kind = new[] { "Feature", "Datum", "AssemblyPattern", "SketchConstraint", "ExactSketchCurve", "SketchRegion" }
                .SingleOrDefault(s => s.Equals(args[2], StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("Unknown target kind. Read TARGETS.");
            Guid? companion = args.Length >= 6 ? ExactId(args[5]) : null;
            int? index = null;
            if (args.Length == 7)
            {
                if (!int.TryParse(args[6], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed is < 0 or > 100000)
                    throw new ArgumentException("Invalid exact curve index.");
                index = parsed;
            }
            if (kind is "Feature" or "Datum" or "AssemblyPattern" && args.Length != 5 ||
                kind == "SketchConstraint" && args.Length != 6 || kind == "ExactSketchCurve" && args.Length < 6 ||
                kind == "SketchRegion" && (args.Length != 6 || args[4] != "Distance"))
                throw new ArgumentException("Unexpected or missing target companion. Copy the complete author field from TARGETS.");
            return new("bind", ParameterId: ExactId(args[1]), Target: new(kind, ExactId(args[3]), args[4],
                kind == "SketchConstraint" ? companion : null, kind is "ExactSketchCurve" or "SketchRegion" ? companion : null, index));
        }
        throw new ArgumentException("Invalid PARAM syntax. Type HELP PARAM; expressions with spaces require quotes.");
    }

    private static Guid ExactId(string text) => Guid.TryParse(text, out var id) && id != Guid.Empty ? id :
        throw new ArgumentException("Expected a nonempty exact GUID, copied from an inspection result.");

    private static bool TryPage(string[] args, out int offset, out int limit)
    {
        offset = 0; limit = 50;
        return args.Length == 0 || args.Length == 2 &&
            int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset) && offset is >= 0 and <= 100000 &&
            int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit is >= 1 and <= 50;
    }
}
