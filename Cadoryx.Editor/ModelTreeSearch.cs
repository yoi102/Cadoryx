using Cadoryx.Db;

namespace Cadoryx.Editor;

public sealed record ModelSearchHit(string Name,string Breadcrumb,OccurrencePath Path,SelectionTarget? Body);
public sealed record ModelSearchResult(IReadOnlyList<ModelSearchHit> Hits,bool Truncated);

/// <summary>Search immutable definitions/instances without creating UI tree rows or loading geometry.</summary>
public static class ModelTreeSearch
{
    public static ModelSearchResult Find(DocumentSnapshot snapshot,string query,int limit=200,CancellationToken token=default)
    {
        if(limit is < 1 or > 10000)throw new ArgumentOutOfRangeException(nameof(limit));
        query=query.Trim();if(query.Length==0)return new([],false);
        var hits=new List<ModelSearchHit>();var crumbs=new Dictionary<OccurrencePath,string>();
        foreach(var occurrence in snapshot.EnumerateOccurrences())
        {
            token.ThrowIfCancellationRequested();
            var path=occurrence.Path;var parent=new OccurrencePath(snapshot.Id,path.Slots.RemoveAt(path.Slots.Length-1));
            var definition=snapshot.Definitions[occurrence.DefinitionId];
            var breadcrumb=(crumbs.TryGetValue(parent,out var prefix)?prefix+" / ":"")+occurrence.Name;
            crumbs[path]=breadcrumb;
            if(Matches(occurrence.Name)||Matches(definition.Name))
            {
                if(hits.Count==limit)return new(hits,true);
                hits.Add(new(occurrence.Name+" ["+definition.Name+"]",breadcrumb,path,null));
            }
            if(definition is PartDefinition part)foreach(var id in part.Bodies)
            {
                var body=snapshot.Bodies[id];if(!Matches(body.Name))continue;
                if(hits.Count==limit)return new(hits,true);
                hits.Add(new(body.Name,breadcrumb+" / "+body.Name,path,new(path,id,body.Geometry.Revision)));
            }
        }
        return new(hits,false);
        bool Matches(string text)=>text.Contains(query,StringComparison.OrdinalIgnoreCase);
    }

    public static DefinitionId ResolveDefinition(DocumentSnapshot snapshot,OccurrencePath path)
    {
        if(path.DocumentId!=snapshot.Id)throw new CadValidationException("Foreign instance path.");
        var id=snapshot.RootAssemblyId;
        foreach(var slotId in path.Slots)
        {
            if(snapshot.Definitions[id] is not AssemblyDefinition assembly)
                throw new CadValidationException("Path crosses a part.");
            id=(assembly.Children.SingleOrDefault(s=>s.Id==slotId)
                ??throw new CadValidationException("Instance no longer exists.")).DefinitionId;
        }
        return id;
    }
}
