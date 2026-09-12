using System.Collections.Immutable;
using Cadoryx.Db;

namespace Cadoryx.Editor;

public sealed record SelectionTarget(OccurrencePath Path,BodyId BodyId,GeometryRevisionId GeometryRevision);
public sealed class SelectionService
{
    public ImmutableArray<SelectionTarget> Items { get; private set; }=[];
    public OccurrencePath? Occurrence {get;private set;}
    public event EventHandler? Changed;
    public void Replace(IEnumerable<SelectionTarget> items)
    {
        var next=items.Distinct().ToImmutableArray();var path=next.FirstOrDefault()?.Path;
        if(Items.SequenceEqual(next)&&Equals(Occurrence,path))return;Items=next;Occurrence=path;Changed?.Invoke(this,EventArgs.Empty);
    }
    public void SelectOccurrence(OccurrencePath? path)
    {
        if(Items.IsEmpty&&Equals(Occurrence,path))return;Items=[];Occurrence=path;Changed?.Invoke(this,EventArgs.Empty);
    }
    public void Reconcile(DocumentSnapshot snapshot)
    {
        var paths=snapshot.EnumerateOccurrences().ToDictionary(o=>o.Path);
        var previous=Occurrence;
        var next=Items.Where(i=>i.Path.DocumentId==snapshot.Id&&snapshot.Bodies.TryGetValue(i.BodyId,out var body)&&body.Geometry.Revision==i.GeometryRevision&&
            paths.TryGetValue(i.Path,out var occurrence)&&occurrence.DefinitionId==body.PartId).ToArray();
        if(next.Length==0&&previous is not null&&paths.ContainsKey(previous))SelectOccurrence(previous);else Replace(next);
    }
}
