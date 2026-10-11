using Cadoryx.Db;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    // This proves a presentation attachment by complete native membership. It does not
    // create container topology identity or broaden the Face/Edge/Vertex history contract.
    private static void AddProvedContainerPresentation(RepetitionRecipe recipe,RepairDocumentSession source,
        IReadOnlyDictionary<int,XdeLabel> labels,IReadOnlyDictionary<int,DirectPresentation> presentations,
        RepairDocumentSession result,TopologyHistory history,Dictionary<int,int> chosen,CancellationToken token)
    {
        static bool Leaf(ShapeKind kind)=>kind is ShapeKind.Face or ShapeKind.Edge or ShapeKind.Vertex;
        var marked=source.Source.Topology.Skip(1).Where(t=>!Leaf(t.Kind)&&presentations[t.Selection.Index].Assigned).ToArray();
        if(marked.Length==0)return;
        var kinds=marked.Select(t=>t.Kind).ToHashSet();
        var candidates=result.Source.Topology.Skip(1).Where(t=>kinds.Contains(t.Kind)).ToArray();
        var sourceLeaves=source.Source.Topology.Where(t=>Leaf(t.Kind)).ToArray();
        var resultLeaves=result.Source.Topology.Where(t=>Leaf(t.Kind)).ToArray();
        if(marked.Length*(long)sourceLeaves.Length+candidates.Length*(long)resultLeaves.Length>2_000_000)
            throw new CadValidationException("STYLES.CONTAINER: Complete native container membership exceeds the presentation proof budget. Choose whole-body presentation explicitly.");
        var sourceShapes=new Dictionary<int,Shape>();var resultShapes=new Dictionary<int,Shape>();
        try
        {
            foreach(var leaf in sourceLeaves){token.ThrowIfCancellationRequested();sourceShapes.Add(leaf.Selection.Index,labels[leaf.Selection.Index].Shape);}
            foreach(var leaf in resultLeaves)
            {
                token.ThrowIfCancellationRequested();
                var label=result.GetOrCreateSubshapeLabel(leaf.Selection);resultShapes.Add(leaf.Selection.Index,label.Shape);
            }
            var members=new Dictionary<int,HashSet<int>>();
            foreach(var candidate in candidates)
            {
                token.ThrowIfCancellationRequested();var label=result.GetOrCreateSubshapeLabel(candidate.Selection);
                using var shape=label.Shape;
                members.Add(candidate.Selection.Index,NativeMembers(shape,resultShapes,token));
            }
            var paths=history.Entries.ToLookup(e=>(e.CopyId,e.SourceIndex));
            foreach(var container in marked)
            {
                token.ThrowIfCancellationRequested();int origin=container.Selection.Index;
                using var shape=labels[origin].Shape;var leaves=NativeMembers(shape,sourceShapes,token);
                if(leaves.Count==0)throw new CadValidationException("STYLES.CONTAINER: Assigned container has no complete native boundary membership.");
                foreach(var copy in recipe.CopyIds)
                {
                    var expected=new HashSet<int>();
                    foreach(int leaf in leaves)
                    {
                        var rows=paths[(copy,leaf)].ToArray();
                        if(rows.Length!=1||rows[0].PathAmbiguous||rows[0].ResultIndex is not {} target||rows[0].SourceKind!=rows[0].ResultKind||
                            rows[0].Evolution is not (TopologyEvolution.Unchanged or TopologyEvolution.Modified))
                            throw new CadValidationException("STYLES.CONTAINER: Assigned container has split, merged, deleted or unproved native boundary paths. Choose whole-body presentation explicitly.");
                        if(!expected.Add(target))throw new CadValidationException("STYLES.CONTAINER: Native container boundaries are not bijective.");
                    }
                    var matching=candidates.Where(t=>t.Kind==container.Kind&&members[t.Selection.Index].SetEquals(expected)).Take(2).ToArray();
                    if(matching.Length!=1)throw new CadValidationException("STYLES.CONTAINER: No unique complete native result container. Choose whole-body presentation explicitly.");
                    int index=matching[0].Selection.Index;
                    if(chosen.TryGetValue(index,out int previous)&&!presentations[previous].Same(presentations[origin]))
                        throw new CadValidationException("STYLES.CONFLICT: Complete native container contributors have incompatible presentation.");
                    chosen[index]=origin;
                }
            }
        }
        finally{foreach(var shape in sourceShapes.Values)shape.Dispose();foreach(var shape in resultShapes.Values)shape.Dispose();}
    }

    private static HashSet<int> NativeMembers(Shape container,IReadOnlyDictionary<int,Shape> leaves,CancellationToken token)
    {
        var members=new HashSet<int>();
        foreach(var (index,leaf) in leaves)
        {token.ThrowIfCancellationRequested();if(RepairSnapshot.FindTopologyIndex(container,leaf)>=0)members.Add(index);}
        return members;
    }
}
