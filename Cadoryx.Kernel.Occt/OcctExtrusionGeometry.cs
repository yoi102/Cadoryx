using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:IExtrusionLimitResolver
{
    public Task<IReadOnlyList<ExactPlanarLimit>> EnumeratePlanarLimitsAsync(DocumentSnapshot document,IAssetStore assets,CancellationToken token=default)=>
        Run<IReadOnlyList<ExactPlanarLimit>>(()=>
        {
            var result=new List<ExactPlanarLimit>();
            foreach(var body in document.Bodies.Values)
            {
                if(body.Producer is not {} id||!document.Features.TryGetValue(id,out var feature)||feature.IsStale||feature.IsSuppressed)continue;
                using var shape=OcctGeometryBridge.ReadShape(body.Geometry,assets);using var map=RepairSnapshot.Create(shape);
                foreach(var item in map.Topology.Where(t=>t.Kind==ShapeKind.Face))
                {
                    token.ThrowIfCancellationRequested();using var face=map.CopySubshape(item.Selection);
                    if(face.GetFaceSurfaceSnapshot().SurfaceType!=SurfaceGeometryType.Plane)continue;
                    result.Add(new(new(document.Id,id,feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,item.Selection.Index,
                        HistoryShapeKind.Face,HistoryAdapterVersion),feature.Result));
                    if(result.Count>10000)throw new CadValidationException("Limit face inventory is too large; isolate the modeling part.");
                }
            }
            return result;
        },token);
    private GeometryResult EvaluateExtrusion(ExtrudeRecipe recipe,IAssetStore assets,CancellationToken token,Shape? sourceOverride=null)
    {
        var options=recipe.Options!;
        using var source=sourceOverride?.Clone()??(options.Source is {} input?OcctGeometryBridge.ReadShape(input,assets):null);
        if(source is not null)
        {
            using var map=RepairSnapshot.Create(source);
            if(map.Topology.Count(t=>t.Kind==ShapeKind.Solid)!=1)
                throw new CadValidationException("Select exactly one solid target for extrusion.");
        }
        if(options.Termination==ExtrusionTermination.FiniteSurface)return options.Operation==ExtrusionOperation.NewBody?
            EvaluateFiniteSurfaceNewBody(recipe,assets,token):EvaluateFiniteSurfaceExtrusion(recipe,source!,assets,token);
        RigidTransform3d? limit=options.LimitPlane;
        if(options.LimitFace is {} selection)
        {
            using var shape=OcctGeometryBridge.ReadShape(options.LimitGeometry!,assets);
            using var map=RepairSnapshot.Create(shape);
            if(selection.AdapterVersion!=HistoryAdapterVersion||selection.Fingerprint!=map.Fingerprint||
                selection.FullTopologyIndex>=map.Topology.Count||map.Topology[selection.FullTopologyIndex].Kind!=ShapeKind.Face)
                throw new CadValidationException("The selected limit face changed. Reselect it explicitly.");
            using var face=map.CopySubshape(map.Select(selection.FullTopologyIndex));
            var surface=face.GetFaceSurfaceSnapshot();
            if(surface.SurfaceType!=SurfaceGeometryType.Plane)throw new CadValidationException("The limit must be a planar face.");
            double Mid(double first,double last)=>double.IsFinite(first)&&double.IsFinite(last)?(first+last)/2:
                throw new CadValidationException("The selected face has no finite parameter domain.");
            var evaluated=face.EvaluateFace(Mid(surface.FirstUParameter,surface.LastUParameter),Mid(surface.FirstVParameter,surface.LastVParameter));
            var normal=new Vector3d(evaluated.Normal.X,evaluated.Normal.Y,evaluated.Normal.Z).Normalized();
            limit=new(new(evaluated.Point.X,evaluated.Point.Y,evaluated.Point.Z),NormalRotation(normal));
        }
        var direction=recipe.Placement.Rotation.Rotate(Vector3d.UnitZ)*(options.Side==ExtrusionSide.Negative?-1:1);
        double start=0,length=recipe.Distance;
        if(options.Termination==ExtrusionTermination.Distance)
        {
            if(options.Side==ExtrusionSide.Negative)start=-length;
            if(options.Side==ExtrusionSide.Symmetric)start=-length/2;
            if(options.Side==ExtrusionSide.TwoSides){start=-options.Backward;length+=options.Backward;}
        }
        else if(options.Termination==ExtrusionTermination.Through)
        {
            // Bounds only enclose a finite directional cutter; its start remains the authored profile plane.
            var extent=Corners(source!).Select(p=>recipe.Placement.Inverse().Apply(p).Z).ToArray();
            length=options.Side==ExtrusionSide.Negative?Math.Max(0,-extent.Min())+1:
                options.Side==ExtrusionSide.Symmetric?2*Math.Max(Math.Abs(extent.Min()),Math.Abs(extent.Max()))+2:Math.Max(0,extent.Max())+1;
            start=options.Side==ExtrusionSide.Negative?-length:options.Side==ExtrusionSide.Symmetric?-length/2:0;
        }
        else
        {
            var plane=limit!.Value;var normal=plane.Rotation.Rotate(Vector3d.UnitZ);
            if(normal.Dot(direction)<0){plane=new(plane.Translation,plane.Rotation*Quaterniond.FromAxisAngle(new(1,0,0),Math.PI));normal=normal*-1;}
            double dot=normal.Dot(direction);
            if(dot<1e-8)throw new CadValidationException("Extrusion direction is parallel to its limit plane.");
            using var profile=OcctSketchProfileGeometry.CreateFace(recipe.Profile,false);
            using var placement=OcctGeometryBridge.ToNative(recipe.Placement);using var face=profile.Transformed(placement);
            length=Corners(face).Max(p=>(plane.Translation-p).Dot(normal)/dot)+1;
            if(!double.IsFinite(length)||length<=1e-6||length>1_000_000)throw new CadValidationException("The limit plane is behind or too far from the profile.");
            if(options.Side==ExtrusionSide.Negative)start=-length;
            limit=plane;
        }
        if(length<=1e-6||length>2_000_000)throw new CadValidationException("Extrusion extent is outside supported bounds.");
        token.ThrowIfCancellationRequested();
        using var tool=Create(new ExtrudeRecipe(recipe.Profile,length,recipe.Placement*RigidTransform3d.Translate(0,0,start)),assets,token);
        Shape? clipped=null;
        try
        {
            if(limit is {} plane)
            {
                using var beyond=BeyondPlane(tool,plane);
                // Prove the entire authored outer profile starts before the plane, including curved boundaries.
                using var profile=OcctSketchProfileGeometry.CreateFace(recipe.Profile,false);
                using var placement=OcctGeometryBridge.ToNative(recipe.Placement);using var face=profile.Transformed(placement);
                using var excluded=face.Common(beyond);
                double area=face.InspectProperties(InspectionPropertyKind.Area).Mass;
                if(FaceArea(excluded)>Math.Max(1e-8,area*1e-9))
                    throw new CadValidationException("The limit plane intersects or precedes the starting profile.");
                clipped=tool.Cut(beyond);
            }
            token.ThrowIfCancellationRequested();var effective=clipped??tool;
            using var result=options.Operation switch{ExtrusionOperation.Fuse=>source!.Fuse(effective),ExtrusionOperation.Cut=>source!.Cut(effective),_=>effective.Clone()};
            using var check=RepairSnapshot.Create(result);
            if(check.Topology.All(t=>t.Kind!=ShapeKind.Solid))throw new CadValidationException("Extrusion must leave valid solid material.");
            double after=result.InspectProperties(InspectionPropertyKind.Volume).Mass;
            if(source is not null)
            {
                if(check.Topology.Count(t=>t.Kind==ShapeKind.Solid)!=1)throw new CadValidationException("Extrusion must leave exactly one target solid.");
                double before=source.InspectProperties(InspectionPropertyKind.Volume).Mass;
                double change=options.Operation==ExtrusionOperation.Cut?before-after:after-before;
                if(change<=Math.Max(1e-9,before*1e-12))throw new CadValidationException("Extrusion did not change the target material.");
            }
            using var stored=OcctGeometryBridge.StoreShape(result,assets,adaptiveVolume:recipe.Profile.RequiresExactRegionInspection);
            if(stored.Geometry.Kind is not (BodyKind.Solid or BodyKind.Compound)||after<=1e-9)
                throw new CadValidationException("Extrusion did not produce valid solid geometry.");
            return new(stored.Geometry,GeometryAssetLease.Acquire(stored.Geometry,assets),source is null?[]:[new("HISTORY.UNSUPPORTED",
                "Extrusion and target boolean history are not composed; reselect final topology explicitly.")]);
        }
        finally{clipped?.Dispose();}
    }
    private static Vector3d[] Corners(Shape shape)
    {
        var box=shape.GetBoundingBox();
        return (from x in new[]{box.Minimum.X,box.Maximum.X} from y in new[]{box.Minimum.Y,box.Maximum.Y}
                from z in new[]{box.Minimum.Z,box.Maximum.Z} select new Vector3d(x,y,z)).ToArray();
    }
    private static Shape BeyondPlane(Shape tool,RigidTransform3d plane)
    {
        var points=Corners(tool).Select(plane.Inverse().Apply).ToArray();
        double minX=points.Min(p=>p.X)-1,minY=points.Min(p=>p.Y)-1;
        using var box=ShapeFactory.CreateBox(points.Max(p=>p.X)-minX+1,points.Max(p=>p.Y)-minY+1,Math.Max(1,points.Max(p=>p.Z)+1));
        using var transform=OcctGeometryBridge.ToNative(plane*RigidTransform3d.Translate(minX,minY,0));
        return box.Transformed(transform);
    }
    private static Quaterniond NormalRotation(Vector3d normal)
    {
        if(normal.Z<-1+1e-12)return Quaterniond.FromAxisAngle(new(1,0,0),Math.PI);
        double norm=Math.Sqrt(2*(1+normal.Z));return new(-normal.Y/norm,normal.X/norm,0,(1+normal.Z)/norm);
    }
    private static double FaceArea(Shape shape)
    {
        var faces=shape.GetSubShapes(ShapeKind.Face);
        try{return faces.Sum(f=>f.InspectProperties(InspectionPropertyKind.Area).Mass);}
        finally{foreach(var face in faces)face.Dispose();}
    }
}
