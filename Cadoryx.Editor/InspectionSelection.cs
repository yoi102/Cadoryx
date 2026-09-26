using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Editor;

public static class InspectionSelection
{
    public static IReadOnlyList<GeometryInstance> Resolve(DocumentSnapshot snapshot,IEnumerable<SelectionTarget> selection)
    {
        var targets=selection.Distinct().ToArray();
        // Use the same exact path/revision guard as interactive envelope measurement.
        SelectionMeasurement.Measure(snapshot,targets);
        var occurrences=snapshot.EnumerateOccurrences().ToDictionary(o=>o.Path);
        return targets.Select(t=>new GeometryInstance(t.Path,t.BodyId,snapshot.Bodies[t.BodyId].Geometry,occurrences[t.Path].WorldTransform)).ToArray();
    }
}
