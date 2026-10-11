using System.Collections.Immutable;

namespace Cadoryx.Db;

public sealed record AssemblyMirrorBody(BodyId Source,BodyId Target,AssetId SourceAsset,GeometryRevisionId SourceRevision,
    AssetId ResultAsset,GeometryRevisionId ResultRevision);
public sealed record AssemblyMirrorSlot(ComponentSlotId Source,ComponentSlotId Target);
public sealed record AssemblyMirrorDefinition(DefinitionId Source,DefinitionId Target,
    ImmutableArray<AssemblyMirrorBody> Bodies,ImmutableArray<AssemblyMirrorSlot> Slots);

/// <summary>Two reflections form a proper rigid placement; the geometry carries the local Z reflection.</summary>
public static class AssemblyMirrors
{
    /// <summary>Inner layouts are prepared before consumers of their derived definitions.</summary>
    public static ImmutableArray<Guid> OrderedPatterns(DocumentSnapshot document)
    {
        var patterns=document.AssemblyPatterns.Values.Where(p=>p.Kind==RepetitionKind.Mirror).ToDictionary(p=>p.Id);
        var producers=new Dictionary<DefinitionId,Guid>();
        foreach(var p in patterns.Values)
            foreach(var link in p.Mirrors)
                if(!producers.TryAdd(link.Target,p.Id))throw new CadValidationException("Mirror derived definitions are owned by multiple layouts.");
        var dependencies=new Dictionary<Guid,HashSet<Guid>>();
        foreach(var p in patterns.Values)
        {
            var visited=new HashSet<DefinitionId>();var inputs=new HashSet<Guid>();
            void Visit(DefinitionId id,int depth)
            {
                if(depth>128)throw new CadValidationException("Mirror hierarchy exceeds its definition or depth limit.");
                if(!visited.Add(id))return;
                if(visited.Count>4096)throw new CadValidationException("Mirror hierarchy exceeds its definition or depth limit.");
                if(producers.TryGetValue(id,out var producer))inputs.Add(producer);
                if(document.Definitions.GetValueOrDefault(id) is AssemblyDefinition assembly)
                {
                    // A newly authored inner layout may not yet have its output slot.
                    foreach(var inner in patterns.Values.Where(inner=>inner.Owner==id))inputs.Add(inner.Id);
                    foreach(var child in assembly.Children)Visit(child.DefinitionId,depth+1);
                }
                else if(!document.Definitions.ContainsKey(id))throw new CadValidationException("Mirror source definition is missing.");
            }
            Visit(p.Resolve(document).Seed.DefinitionId,0);dependencies.Add(p.Id,inputs);
        }
        var active=new HashSet<Guid>();var done=new HashSet<Guid>();var ordered=ImmutableArray.CreateBuilder<Guid>();
        void Order(Guid id,int depth)
        {
            if(depth>128||!active.Add(id))throw new CadValidationException("Mirror layout dependencies contain a cycle or exceed their depth limit.");
            if(!done.Contains(id))
            {
                foreach(var input in dependencies[id].Order())Order(input,depth+1);
                done.Add(id);ordered.Add(id);
            }
            active.Remove(id);
        }
        foreach(var id in patterns.Keys.Order())Order(id,0);
        return ordered.ToImmutable();
    }
    public static Vector3d ReflectVector(Vector3d vector,Vector3d normal)=>vector-normal*(2*vector.Dot(normal));
    public static RigidTransform3d Placement(RigidTransform3d plane,RigidTransform3d source)
    {
        plane.Validate();source.Validate();var normal=plane.Rotation.Rotate(Vector3d.UnitZ);
        var origin=plane.Translation+ReflectVector(source.Translation-plane.Translation,normal);
        var x=ReflectVector(source.Rotation.Rotate(new(1,0,0)),normal);
        var z=ReflectVector(source.Rotation.Rotate(Vector3d.UnitZ),normal)*-1;
        return DatumConstructions.Frame(origin,z,x);
    }
    public static bool ControlsDefinition(DocumentSnapshot document,DefinitionId definition)=>
        document.AssemblyPatterns.Values.SelectMany(p=>p.Mirrors).Any(m=>m.Target==definition);
    public static DefinitionId OutputDefinition(AssemblyRepetition pattern,ComponentSlot source)=>pattern.Kind==RepetitionKind.Mirror?
        pattern.Mirrors.SingleOrDefault(m=>m.Source==source.DefinitionId)?.Target??throw new CadValidationException("Mirror source definition has no exact derived definition."):
        source.DefinitionId;

    public static void ValidateApplied(DocumentSnapshot document,AssemblyRepetition pattern)
    {
        if(pattern.Kind!=RepetitionKind.Mirror)
        {if(!pattern.Mirrors.IsEmpty)throw new CadValidationException("Only an assembly mirror can own derived definitions.");return;}
        if(pattern.Mirrors.IsDefault||pattern.Mirrors.Length is <1 or >4096||
            pattern.Mirrors.Select(m=>m.Source).Distinct().Count()!=pattern.Mirrors.Length||
            pattern.Mirrors.Select(m=>m.Target).Distinct().Count()!=pattern.Mirrors.Length)
            throw new CadValidationException("Invalid mirror definition identity map.");
        var bySource=pattern.Mirrors.ToDictionary(m=>m.Source);var (_,seed)=pattern.Resolve(document);
        if(!bySource.ContainsKey(seed.DefinitionId))throw new CadValidationException("Mirror root source has no derived definition.");
        var reachable=new HashSet<DefinitionId>();
        void Visit(DefinitionId id,int depth)
        {
            if(depth>128)throw new CadValidationException("Mirror hierarchy exceeds its depth limit.");
            if(!reachable.Add(id))return;
            if(!bySource.TryGetValue(id,out var link))throw new CadValidationException("Mirror hierarchy has an unmapped source definition.");
            CadGuard.Id(link.Source);CadGuard.Id(link.Target);
            if(link.Source==link.Target||bySource.ContainsKey(link.Target)||link.Bodies.IsDefault||link.Slots.IsDefault)
                throw new CadValidationException("Mirror definitions overlap or have uninitialized maps.");
            if(document.Definitions.GetValueOrDefault(link.Target)?.Name!=document.Definitions.GetValueOrDefault(id)?.Name)
                throw new CadValidationException("Mirror definition name differs from its source.");
            if(document.Definitions.GetValueOrDefault(id) is PartDefinition part)
            {
                if(document.Definitions.GetValueOrDefault(link.Target) is not PartDefinition target||!target.Features.IsEmpty||!link.Slots.IsEmpty||
                    part.Bodies.Length!=link.Bodies.Length||target.Bodies.Length!=link.Bodies.Length||link.Bodies.Select(b=>b.Source).Distinct().Count()!=link.Bodies.Length||
                    !part.Bodies.ToHashSet().SetEquals(link.Bodies.Select(b=>b.Source))||
                    !target.Bodies.ToHashSet().SetEquals(link.Bodies.Select(b=>b.Target))||link.Bodies.Select(b=>b.Target).Distinct().Count()!=link.Bodies.Length)
                    throw new CadValidationException("Mirror part output map differs from its active source bodies.");
                foreach(var body in link.Bodies)
                {
                    var source=document.Bodies[body.Source];var mirrored=document.Bodies[body.Target];
                    if(source.Geometry.AssetId!=body.SourceAsset||source.Geometry.Revision!=body.SourceRevision||
                        mirrored.Geometry.AssetId!=body.ResultAsset||mirrored.Geometry.Revision!=body.ResultRevision||
                        mirrored!=CopyBody(source,body.Target,link.Target,mirrored.Geometry))
                        throw new CadValidationException("Mirror geometry cache or source properties are stale.");
                }
            }
            else if(document.Definitions.GetValueOrDefault(id) is AssemblyDefinition assembly)
            {
                if(document.Definitions.GetValueOrDefault(link.Target) is not AssemblyDefinition target||!link.Bodies.IsEmpty||
                    assembly.Children.Length!=link.Slots.Length||target.Children.Length!=link.Slots.Length||link.Slots.Select(s=>s.Source).Distinct().Count()!=link.Slots.Length||
                    !assembly.Children.Select(s=>s.Id).ToHashSet().SetEquals(link.Slots.Select(s=>s.Source))||
                    !target.Children.Select(s=>s.Id).ToHashSet().SetEquals(link.Slots.Select(s=>s.Target))||link.Slots.Select(s=>s.Target).Distinct().Count()!=link.Slots.Length)
                    throw new CadValidationException("Mirror assembly hierarchy differs from its source slots.");
                foreach(var slot in assembly.Children)
                {
                    Visit(slot.DefinitionId,depth+1);var mapping=link.Slots.Single(s=>s.Source==slot.Id);
                    var actual=target.Children.Single(s=>s.Id==mapping.Target);
                    var expected=slot with{Id=mapping.Target,DefinitionId=bySource[slot.DefinitionId].Target,
                        LocalTransform=Placement(RigidTransform3d.Identity,slot.LocalTransform)};
                    if((actual with{LocalTransform=expected.LocalTransform})!=expected||!DatumPlanes.SameFrame(actual.LocalTransform,expected.LocalTransform))
                        throw new CadValidationException("Mirror child placement or properties differ from its source.");
                }
            }
            else throw new CadValidationException("Mirror source definition is missing.");
        }
        Visit(seed.DefinitionId,0);
        if(reachable.Count!=pattern.Mirrors.Length)throw new CadValidationException("Mirror identity map contains unrelated definitions.");
    }
    public static CadBody CopyBody(CadBody source,BodyId target,DefinitionId part,GeometryAssetRef geometry)=>
        source with{Id=target,PartId=part,Geometry=geometry,Producer=null,
            Appearance=source.Appearance with{PreserveSourceStyles=source.Appearance.PreserveSourceStyles&&geometry.Source is not null}};
}
