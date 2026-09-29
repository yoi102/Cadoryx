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
            ["Exact BRep", "Assembly definitions and placements", "Names", "Overall colors"],
            ["Cadoryx feature history", "Sketch constraints", "Drawing sheets", "Assembly mates"],
            ["Source PMI and subshape styles are not exported by the current writer"]));
        if(options.IncludeIges)rows.Add(new("IGES",
            ["Analytic surfaces", "Overall colors where supported by the writer"],
            ["Cadoryx feature history", "Sketch constraints", "Drawing sheets", "Assembly mates"],
            ["Solid volume may reopen as surfaces", "Assembly sharing may be flattened", "PMI and face styles are not verified"]));
        if(options.IncludeStl)rows.Add(new("STL",
            ["Triangulated visible surface coordinates"],
            ["Intrinsic unit declaration", "Exact BRep", "Assembly hierarchy", "Names", "Colors", "PMI", "Feature history", "Drawing sheets"],
            ["Coordinates are written in millimeters; the receiver must be told the unit"]));
        return new(1,snapshot.Id.ToString(),snapshot.StateId.ToString(),
            "Writer capability assessment; not a measured import/export round-trip",rows.ToArray());
    }
}
