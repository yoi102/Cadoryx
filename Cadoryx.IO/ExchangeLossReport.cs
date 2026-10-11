using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.IO;

public sealed record ExchangeLossRow(string Format,string[] Preserved,string[] Lost,string[] Conditional);

/// <summary>Declared writer capabilities, not a claim that a third-party reader round-tripped the file.</summary>
public sealed record ExchangeLossReport(int SchemaVersion,string DocumentId,string StateId,string Basis,
    ExchangeLossRow[] Formats)
{
    public static ExchangeLossReport Create(DocumentSnapshot snapshot,DeliveryOptions options)
    {
        var rows=new List<ExchangeLossRow>();
        if(options.IncludeStep)rows.Add(new("STEP",
            ["Generated BRep geometry", "Assembly definitions and placements", "Names", "Overall colors"],
            ["Cadoryx feature history", "Sketch constraints", "Drawing sheets", "Assembly mates"],
            ["Supported source subshape colors use exact transformation history; unsupported styles and source PMI are not transferred",
             "Source layers, per-face visibility and physical/PBR materials require separate receiver verification; surface-color success does not prove these attributes survived"]));
        if(options.IncludeIges)rows.Add(new("IGES",
            ["Analytic surfaces", "Overall colors where supported by the writer"],
            ["Cadoryx feature history", "Sketch constraints", "Drawing sheets", "Assembly mates"],
            ["Solid volume may reopen as surfaces", "Assembly sharing may be flattened", "Supported source subshape colors use exact transformation history; source PMI is not transferred",
             "Source layers, per-face visibility and physical/PBR materials require separate receiver verification; surface-color success does not prove these attributes survived"]));
        if(options.IncludeStl)rows.Add(new("STL",
            ["Triangulated visible surface coordinates"],
            ["Intrinsic unit declaration", "Exact BRep", "Assembly hierarchy", "Names", "Colors", "PMI", "Feature history", "Drawing sheets"],
            ["Coordinates are written in millimeters; the receiver must be told the unit"]));
        if(snapshot.Features.Values.Any(f=>f.Recipe is HoleRecipe {Thread:not null} or HolePatternRecipe {Template.Thread:not null}))
            rows=rows.Select(row=>row with{Lost=[..row.Lost,"Thread designation, pitch, hand, profile identity and effective depth as author semantics; symbolic intent has only pilot geometry, modeled intent has tooth geometry"]}).ToList();
        if(snapshot.Features.Values.Any(f=>f.Recipe is SweepRecipe or LoftRecipe))
            rows=rows.Select(row=>row with
            {
                Lost=[..row.Lost,"Guided author profiles, signed offset operations, native intervals, path direction, station/channel identities and source dependencies as editable Cadoryx semantics"],
                Conditional=[..row.Conditional,"Guided modeling may already include bounded native curve/surface approximations; proven prism branches retain their authored geometry. Exchange compares with the generated BRep and does not certify algebraic exactness against author expressions or a stored per-result modeling-error bound"]
            }).ToList();
        return new(1,snapshot.Id.ToString(),snapshot.StateId.ToString(),
            "Writer capability assessment; not a measured import/export round-trip",rows.ToArray());
    }
}
