using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Editor;

public sealed record SelectionTarget(OccurrencePath Path,BodyId BodyId,GeometryRevisionId GeometryRevision);
public sealed class SelectionService
{
    public ImmutableArray<SelectionTarget> Items { get; private set; }=[];
    public event EventHandler? Changed;
    public void Replace(IEnumerable<SelectionTarget> items)
    {
        var next=items.Distinct().ToImmutableArray();if(Items.SequenceEqual(next))return;Items=next;Changed?.Invoke(this,EventArgs.Empty);
    }
    public void Reconcile(DocumentSnapshot snapshot)
    {
        var paths=snapshot.EnumerateOccurrences().Where(o=>o.IsVisible).ToDictionary(o=>o.Path);
        Replace(Items.Where(i=>i.Path.DocumentId==snapshot.Id&&snapshot.Bodies.TryGetValue(i.BodyId,out var body)&&body.Geometry.Revision==i.GeometryRevision&&
            paths.TryGetValue(i.Path,out var occurrence)&&occurrence.DefinitionId==body.PartId));
    }
}
