using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:IExchangeObservationService
{
    public async Task<ExchangeModelObservation> ObserveExchangeAsync(DocumentSnapshot snapshot,IAssetStore assets,
        bool visibleOnly=true,CancellationToken token=default)
    {
        snapshot.Validate();var leases=new List<IAssetLease>();
        try
        {
            foreach(var id in snapshot.ReferencedAssets()){token.ThrowIfCancellationRequested();leases.Add(assets.Acquire(id));}
            return await Run(()=>ObserveExchange(snapshot,assets,visibleOnly,token),token).ConfigureAwait(false);
        }
        finally{foreach(var lease in leases)lease.Dispose();}
    }
    private static ExchangeModelObservation ObserveExchange(DocumentSnapshot snapshot,IAssetStore assets,bool visibleOnly,CancellationToken token)
    {
        bool IncludeBody(BodyId id)
        {
            var body=snapshot.Bodies[id];return body.Geometry.Kind!=BodyKind.Empty&&
                (!visibleOnly||body.IsVisible&&snapshot.Layers[body.LayerId].IsVisible);
        }
        var included=new Dictionary<DefinitionId,bool>();
        bool IncludeDefinition(DefinitionId id)
        {
            if(included.TryGetValue(id,out bool known))return known;
            bool value=snapshot.Definitions[id] switch
            {
                PartDefinition part=>part.Bodies.Any(IncludeBody),
                AssemblyDefinition assembly=>assembly.Children.Any(s=>(!visibleOnly||s.IsVisible)&&IncludeDefinition(s.DefinitionId)),
                _=>false
            };
            included.Add(id,value);return value;
        }
        var occurrences=snapshot.EnumerateOccurrences(4096).Where(o=>(!visibleOnly||o.IsVisible)&&IncludeDefinition(o.DefinitionId)).ToArray();
        var groups=new Dictionary<DefinitionId,int>();var reuse=new Dictionary<DefinitionId,int>();
        var graph=ImmutableArray.CreateBuilder<ExchangeOccurrenceObservation>();
        var colors=new HashSet<uint>();var styles=new HashSet<uint>();var effective=new HashSet<uint>();using var contexts=new ExportStyleContexts(assets);
        double area=0,volume=0;int count=0,solidBodies=0,otherBodies=0,facesCount=0,edgesCount=0;
        Vector3d? minimum=null,maximum=null;
        foreach(var occurrence in occurrences)
        {
            token.ThrowIfCancellationRequested();var definition=snapshot.Definitions[occurrence.DefinitionId];
            if(!groups.TryGetValue(definition.Id,out int group)){group=groups.Count;groups.Add(definition.Id,group);}
            var bodies=definition is PartDefinition part?part.Bodies.Where(IncludeBody).ToArray():[];
            graph.Add(new(graph.Count,occurrence.Path.Slots.Length,occurrence.Name,definition.Name,definition is AssemblyDefinition,
                group,occurrence.WorldTransform,bodies.Length));
            if(definition is PartDefinition)reuse[definition.Id]=reuse.GetValueOrDefault(definition.Id)+1;
            foreach(var id in bodies)
            {
                token.ThrowIfCancellationRequested();if(++count>256)throw new CadValidationException("Exchange measurement is limited to 256 body instances and 4096 occurrences.");
                var body=snapshot.Bodies[id];using var local=OcctGeometryBridge.ReadShape(body.Geometry,assets);
                using var transform=OcctGeometryBridge.ToNative(occurrence.WorldTransform);using var shape=local.Transformed(transform);
                var topology=shape.GetTopologySummary();if(!topology.IsValid)throw new CadValidationException("Exchange measurement requires valid native topology.");
                facesCount=checked(facesCount+topology.UniqueCounts.FaceCount);edgesCount=checked(edgesCount+topology.UniqueCounts.EdgeCount);
                if(facesCount>250000)throw new CadValidationException("Exchange measurement face budget exceeded.");
                var box=ExchangeGeometryBounds(shape,body.Geometry.Kind,token);var min=box.Min;var max=box.Max;
                minimum=minimum is {} oldMin?new(Math.Min(oldMin.X,min.X),Math.Min(oldMin.Y,min.Y),Math.Min(oldMin.Z,min.Z)):min;
                maximum=maximum is {} oldMax?new(Math.Max(oldMax.X,max.X),Math.Max(oldMax.Y,max.Y),Math.Max(oldMax.Z,max.Z)):max;
                var faces=shape.GetFaces();
                try{foreach(var face in faces){token.ThrowIfCancellationRequested();area+=Math.Abs(face.InspectProperties(InspectionPropertyKind.Area).Mass);}}
                finally{foreach(var face in faces)face.Dispose();}
                if(body.Geometry.Kind==BodyKind.Solid&&topology.UniqueCounts.SolidCount==1)
                {volume+=Math.Abs(shape.InspectProperties(InspectionPropertyKind.Volume).Mass);solidBodies++;}
                else otherBodies++;
                var appearance=occurrence.AppearanceOverride??body.Appearance;
                uint fallback=appearance.ByLayer?snapshot.Layers[body.LayerId].Argb:appearance.Argb;colors.Add(fallback);bool hasStyle=false;
                if(occurrence.AppearanceOverride is null&&appearance.PreserveSourceStyles&&!appearance.ByLayer&&body.Geometry.Source is {} source)
                {
                    var presentation=contexts.Get(source).GetLabel(source.DefinitionEntry).GetPresentationStyles();
                    try{foreach(var style in presentation)if(style.EffectiveColor is {} color){uint value=OcctGeometryBridge.ToArgb(color);styles.Add(value);effective.Add(value);hasStyle=true;}}
                    finally{foreach(var style in presentation)style.Dispose();}
                }
                if(!hasStyle)effective.Add(fallback);
            }
        }
        if(count==0)throw new CadValidationException("There is no geometry to measure.");
        CadGuard.Finite(area,volume);var bounds=new Bounds3d(minimum!.Value,maximum!.Value);bounds.Validate();token.ThrowIfCancellationRequested();
        return new(count,reuse.Count,[..reuse.Values.Order()],bounds,area,volume,solidBodies,otherBodies,
            facesCount,edgesCount,[..colors.Order()],[..styles.Order()],[..effective.Order()],graph.ToImmutable());
    }
}
