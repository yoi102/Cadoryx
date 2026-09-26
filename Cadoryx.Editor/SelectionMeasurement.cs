using Cadoryx.Db;

namespace Cadoryx.Editor;

/// <summary>World envelope of transformed stored local bounds. This is not a tight BRep box,
/// a minimum shape distance, a center of mass, or a Boolean union volume.</summary>
public sealed record SelectionMeasurement(int BodyInstances,Bounds3d WorldEnvelope,
    double VolumeSumMm3,int VolumetricInstances,double? MassSumKg,double? EnvelopeCenterDistanceMm)
{
    public static SelectionMeasurement? Measure(DocumentSnapshot snapshot,IEnumerable<SelectionTarget> selection)
    {
        var targets=selection.Distinct().ToArray();if(targets.Length==0)return null;
        var envelopes=new List<Bounds3d>();double volume=0,mass=0;int volumetric=0;bool hasAllMass=true;
        foreach(var target in targets)
        {
            if(target.Path.DocumentId!=snapshot.Id||target.Path.Slots.IsEmpty||target.Path.Slots.Length>128)
                throw new CadValidationException("Invalid measurement instance path.");
            var id=snapshot.RootAssemblyId;var world=RigidTransform3d.Identity;
            foreach(var slotId in target.Path.Slots)
            {
                if(snapshot.Definitions[id] is not AssemblyDefinition assembly)throw new CadValidationException("Invalid measurement path.");
                var slot=assembly.Children.SingleOrDefault(s=>s.Id==slotId)??throw new CadValidationException("Measurement instance no longer exists.");
                world*=slot.LocalTransform;id=slot.DefinitionId;
            }
            if(!snapshot.Bodies.TryGetValue(target.BodyId,out var body)||body.PartId!=id||body.Geometry.Revision!=target.GeometryRevision)
                throw new CadValidationException("Measurement selection is stale or belongs to a different part.");
            var bounds=body.Geometry.Bounds;var points=new List<Vector3d>(8);
            foreach(var x in new[]{bounds.Min.X,bounds.Max.X})
            foreach(var y in new[]{bounds.Min.Y,bounds.Max.Y})
            foreach(var z in new[]{bounds.Min.Z,bounds.Max.Z})points.Add(world.Apply(new(x,y,z)));
            envelopes.Add(Envelope(points));
            if(body.Geometry.Kind is BodyKind.Solid or BodyKind.Compound)
            {
                volumetric++;volume+=body.Geometry.VolumeMm3;
                if(body.MaterialId is {} material)mass+=body.Geometry.VolumeMm3*snapshot.Materials[material].DensityKgPerMm3;
                else hasAllMass=false;
            }
            else hasAllMass=false;
        }
        var union=Envelope(envelopes.SelectMany(b=>new[]{b.Min,b.Max}));
        double? distance=envelopes.Count==2?(Center(envelopes[0])-Center(envelopes[1])).Length:null;
        return new(targets.Length,union,volume,volumetric,hasAllMass?mass:null,distance);
    }
    private static Vector3d Center(Bounds3d b)=>(b.Min+b.Max)/2;
    private static Bounds3d Envelope(IEnumerable<Vector3d> points)
    {
        var a=points.ToArray();
        return new(new(a.Min(p=>p.X),a.Min(p=>p.Y),a.Min(p=>p.Z)),new(a.Max(p=>p.X),a.Max(p=>p.Y),a.Max(p=>p.Z)));
    }
}
