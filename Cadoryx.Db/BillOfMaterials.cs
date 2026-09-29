using System.Collections.Immutable;

namespace Cadoryx.Db;

public sealed record BomOccurrence(string Path,string? ParentPath,int Depth,DefinitionId DefinitionId,
    string Name,string DefinitionName,bool IsAssembly,bool IsVisible,int BodyCount,double? VolumeMm3,double? MassKg);
public sealed record BomPart(DefinitionId DefinitionId,string Name,int Quantity,int BodiesPerInstance,
    double? UnitVolumeMm3,double? UnitMassKg,double? TotalMassKg);
public sealed record BillOfMaterials(DocumentId DocumentId,DocumentStateId StateId,bool VisibleOnly,
    ImmutableArray<BomOccurrence> Occurrences,ImmutableArray<BomPart> Parts)
{
    // Values are cached geometry metadata, not a fresh kernel measurement. Unknown mass stays unknown.
    public static BillOfMaterials Create(DocumentSnapshot document,bool visibleOnly=true,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();document.Validate();
        var rows=ImmutableArray.CreateBuilder<BomOccurrence>();
        foreach(var o in document.EnumerateOccurrences())
        {
            token.ThrowIfCancellationRequested();if(visibleOnly&&!o.IsVisible)continue;
            var definition=document.Definitions[o.DefinitionId];
            var bodies=definition is PartDefinition p?p.Bodies.Select(id=>document.Bodies[id])
                .Where(b=>!visibleOnly||b.IsVisible&&document.Layers[b.LayerId].IsVisible).ToArray():[];
            if(definition is PartDefinition&&visibleOnly&&bodies.Length==0)continue;
            bool measurable=bodies.Length>0&&bodies.All(b=>b.Geometry.Kind==BodyKind.Solid&&
                (b.Producer is not {} producer||!document.Features[producer].IsStale));
            double? volume=measurable?bodies.Sum(b=>b.Geometry.VolumeMm3):null;
            double? mass=measurable&&bodies.All(b=>b.MaterialId.HasValue)?
                bodies.Sum(b=>b.Geometry.VolumeMm3*document.Materials[b.MaterialId!.Value].DensityKgPerMm3):null;
            if(volume is {} v&&!double.IsFinite(v)||mass is {} m&&!double.IsFinite(m))throw new CadValidationException("BOM totals overflow.");
            rows.Add(new(o.Path.ToString(),o.Path.Slots.Length==1?null:new OccurrencePath(document.Id,o.Path.Slots.RemoveAt(o.Path.Slots.Length-1)).ToString(),
                o.Path.Slots.Length,o.DefinitionId,o.Name,definition.Name,definition is AssemblyDefinition,o.IsVisible,bodies.Length,volume,mass));
        }
        var parts=rows.Where(r=>!r.IsAssembly).GroupBy(r=>r.DefinitionId).Select(group=>
        {
            var first=group.First();var count=group.Count();double? total=first.MassKg*count;
            if(total is {} m&&!double.IsFinite(m))throw new CadValidationException("BOM mass overflow.");
            return new BomPart(group.Key,first.DefinitionName,count,first.BodyCount,first.VolumeMm3,first.MassKg,total);
        }).OrderBy(p=>p.Name,StringComparer.Ordinal).ThenBy(p=>p.DefinitionId.ToString(),StringComparer.Ordinal).ToImmutableArray();
        return new(document.Id,document.StateId,visibleOnly,rows.ToImmutable(),parts);
    }
}
