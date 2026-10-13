using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:IFiniteSurfaceExtrusionResolver
{
    public Task<IReadOnlyList<ExactSurfaceLimit>> EnumerateSurfaceLimitsAsync(Cadoryx.Db.DocumentSnapshot snapshot,IAssetStore assets,CancellationToken token=default)=>
        Run<IReadOnlyList<ExactSurfaceLimit>>(()=>
        {
            var result=new List<ExactSurfaceLimit>();
            foreach(var feature in snapshot.Features.Values.Where(f=>!f.IsStale&&!f.IsSuppressed&&f.Result.Kind!=BodyKind.Empty))
            {
                using var shape=OcctGeometryBridge.ReadShape(feature.Result,assets);using var map=RepairSnapshot.Create(shape);
                foreach(var item in map.Topology.Where(t=>t.Kind==ShapeKind.Face))
                {
                    token.ThrowIfCancellationRequested();using var face=map.CopySubshape(item.Selection);
                    var type=face.GetFaceSurfaceSnapshot().SurfaceType;
                    if(type is not (SurfaceGeometryType.Plane or SurfaceGeometryType.Sphere or SurfaceGeometryType.Cylinder))continue;
                    var kind=type switch{SurfaceGeometryType.Sphere=>ExtrusionSurfaceKind.Sphere,SurfaceGeometryType.Cylinder=>ExtrusionSurfaceKind.Cylinder,_=>ExtrusionSurfaceKind.Plane};
                    result.Add(new(new(snapshot.Id,feature.Id,feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,item.Selection.Index,
                        HistoryShapeKind.Face,HistoryAdapterVersion),feature.Result,kind));
                    if(result.Count>10000)throw new CadValidationException("Finite surface inventory is too large; isolate the modeling part.");
                }
            }
            return result;
        },token);
    public Task ConfirmFiniteSurfaceExtrusionAsync(ExtrudeRecipe recipe,GeometryAssetRef result,IAssetStore assets,CancellationToken token=default)
    {
        recipe.Validate();result.Validate();
        if(recipe.Options?.Termination!=ExtrusionTermination.FiniteSurface)throw new CadValidationException("Expected a finite surface extrusion recipe.");
        return Run(()=>
        {
            using var source=recipe.Options.Source is {} input?OcctGeometryBridge.ReadShape(input,assets):null;
            using var replay=recipe.Options.Operation==ExtrusionOperation.NewBody?EvaluateFiniteSurfaceNewBody(recipe,assets,token):
                EvaluateFiniteSurfaceExtrusion(recipe,source!,assets,token);
            if(result.Kind==BodyKind.Empty)return true; // Suppressed result has no material to compare.
            using var expected=OcctGeometryBridge.ReadShape(replay.Geometry,assets);
            using var actual=OcctGeometryBridge.ReadShape(result,assets);using var valid=recipe.Options.Operation==ExtrusionOperation.NewBody?
                CopyFiniteSurfaceMaterial(actual,token):CopyOffsetSolid(actual,token);
            using var intersection=expected.Common(actual);
            double expectedVolume=AuthoringMeasure(expected,InspectionPropertyKind.Volume),actualVolume=AuthoringMeasure(actual,InspectionPropertyKind.Volume);
            double tolerance=Math.Max(1e-6,expectedVolume*1e-9);
            if(Math.Abs(actualVolume-expectedVolume)>tolerance||Math.Abs(AuthoringMeasure(intersection,InspectionPropertyKind.Volume)-expectedVolume)>tolerance||
                expected.GetTopologySummary().UniqueCounts!=actual.GetTopologySummary().UniqueCounts)
                throw new CadValidationException("Stored finite surface result differs from its exact author replay.");
            return true;
        },token);
    }
    private GeometryResult EvaluateFiniteSurfaceExtrusion(ExtrudeRecipe recipe,Shape source,IAssetStore assets,CancellationToken token,bool extractNewBody=false)
    {
        var options=recipe.Options!;
        using var accepted=CopyOffsetSolid(source,token);
        using var sourceMap=RepairSnapshot.Create(source);
        ConfirmFaceFeatureMap(sourceMap,[options.SupportFace!]);
        using var stopSource=OcctGeometryBridge.ReadShape(options.LimitGeometry!,assets);
        using var stopMap=RepairSnapshot.Create(stopSource);
        ConfirmFaceFeatureMap(stopMap,[options.LimitFace!]);
        var sourceFaces=source.GetFaces();var stopFaces=stopSource.GetFaces();
        try
        {
            var support=ExactFaceMember(source,sourceFaces,options.SupportFace!.FullTopologyIndex);
            var stop=ExactFaceMember(stopSource,stopFaces,options.LimitFace!.FullTopologyIndex);
            var surface=stop.GetFaceSurfaceSnapshot();
            if(surface.SurfaceType is not (SurfaceGeometryType.Plane or SurfaceGeometryType.Sphere or SurfaceGeometryType.Cylinder))
                throw new CadValidationException("Finite surface extrusion currently proves plane, sphere and cylinder branches only.");
            var normal=recipe.Placement.Rotation.Rotate(Vector3d.UnitZ);
            if(Math.Abs(NativePlanarNormal(support).Dot(normal))<1-1e-10)
                throw new CadValidationException("The complete starting profile must lie in the exact planar support face.");
            var direction=normal*(options.Side==ExtrusionSide.Negative?-1:1);
            var frame=new RigidTransform3d(recipe.Placement.Translation,NormalRotation(direction));
            // Extract every material face, including holes and recursive islands, from
            // the already validated exact profile builder. No boundary sampling is used.
            using var shortPrism=Create(new ExtrudeRecipe(recipe.Profile,1,recipe.Placement),assets,token);
            var shortFaces=shortPrism.GetFaces();
            try
            {
                var starts=shortFaces.Where(f=>FiniteSurfaceIsProfilePlane(f,recipe.Placement,0)).ToArray();
                if(starts.Length==0)throw new CadValidationException("No complete exact starting profile was found.");
                using var profile=starts.Length==1?starts[0].Clone():ShapeFactory.CreateCompound(starts);
                double area=FiniteSurfaceArea(profile);
                if(Math.Abs(area-AuthoringMeasure(shortPrism,InspectionPropertyKind.Volume))>Math.Max(1e-7,area*1e-9))
                    throw new CadValidationException("Starting material faces do not account for the complete exact unit prism, including every hole and island.");
                using var onSupport=profile.Common(support);
                if(area<=1e-8||Math.Abs(FiniteSurfaceArea(onSupport)-area)>Math.Max(1e-7,area*1e-9))
                    throw new CadValidationException("The exact finite support does not cover the entire starting material profile.");
                var limitBounds=SurfaceFrameBounds(stop,frame);
                if(limitBounds.Min.Z<=1e-6||limitBounds.Max.Z>1_000_000)
                    throw new CadValidationException("The selected finite stop must be entirely ahead of the starting plane within modeling bounds.");
                var branch=FiniteSurfaceAnalyticBranch(stop,surface.SurfaceType,direction);
                if(branch is {} startProof)
                {
                    var startRange=SurfaceFrameBounds(profile,startProof.Frame);
                    if(startRange.Max.Z>=-startProof.Radius-1e-6)
                        throw new CadValidationException("The complete starting profile must precede the analytic surface entry; crossing and inside starts need a separate branch contract.");
                }
                else if(options.SurfaceBranch!=ExtrusionSurfaceBranch.Near)
                    throw new CadValidationException("A plane has exactly one directional branch; choose Near.");
                token.ThrowIfCancellationRequested();
                using var plan=LimitedFeaturePlan.Create(source,profile,support,new()
                {
                    Limit=LocalFeatureLimit.Until,AddMaterial=options.Operation==ExtrusionOperation.Fuse,
                    Direction=new(direction.X,direction.Y,direction.Z)
                },until:stop);
                using var built=plan.Build();
                if(!built.Diagnostics.AlgorithmDone||!built.Diagnostics.ShapeIsValid)
                    throw new CadValidationException("Native finite surface extrusion failed: "+built.Diagnostics.Message);
                using var result=CopyOffsetSolid(built.RequireShape(),token);
                double before=AuthoringMeasure(source,InspectionPropertyKind.Volume),after=AuthoringMeasure(result,InspectionPropertyKind.Volume);
                double volumeTolerance=Math.Max(1e-6,before*1e-9);
                using var nested=source.Common(result);
                if(Math.Abs(AuthoringMeasure(nested,InspectionPropertyKind.Volume)-Math.Min(before,after))>volumeTolerance||
                    (options.Operation==ExtrusionOperation.Fuse?after-before:before-after)<=volumeTolerance)
                    throw new CadValidationException("Finite surface extrusion did not change nested material in the authored operation.");
                using var delta=options.Operation==ExtrusionOperation.Fuse?result.Cut(source):source.Cut(result);
                double length=limitBounds.Max.Z+Math.Max(1,limitBounds.Max.Z*.01);
                using var vector=GpVec.Create(direction.X*length,direction.Y*length,direction.Z*length);
                using var envelope=profile.Extrude(vector);
                using var outside=delta.Cut(envelope);
                if(AuthoringMeasure(outside,InspectionPropertyKind.Volume)>volumeTolerance)
                    throw new CadValidationException("Native changed material outside the complete authored directional profile.");
                using var completeStart=profile.Common(delta);
                if(Math.Abs(FiniteSurfaceArea(completeStart)-area)>Math.Max(1e-7,area*1e-9))
                    throw new CadValidationException("Native extrusion did not preserve the complete starting material domain.");
                var envelopeFaces=envelope.GetFaces();var deltaFaces=delta.GetFaces();
                try
                {
                    var sides=envelopeFaces.Where(f=>!FiniteSurfaceIsProfilePlane(f,frame,length)).ToArray();
                    using var permitted=ShapeFactory.CreateCompound([..sides,stop]);
                    double capArea=0;
                    foreach(var face in deltaFaces)
                    {
                        token.ThrowIfCancellationRequested();double faceArea=FiniteSurfaceArea(face);
                        using var onPermitted=face.Common(permitted);
                        double tolerance=Math.Max(1e-7,faceArea*1e-9);
                        if(Math.Abs(FiniteSurfaceArea(onPermitted)-faceArea)>tolerance)
                            throw new CadValidationException("A native boundary is neither an authored prism side/start nor completely covered by the original finite stop face.");
                        using var cap=face.Common(stop);double covered=FiniteSurfaceArea(cap);
                        if(covered<=tolerance)continue;
                        if(Math.Abs(covered-faceArea)>tolerance)
                            throw new CadValidationException("The original finite stop only partially covers a native terminal cap.");
                        if(branch is {} capProof)
                        {
                            var bounds=SurfaceFrameBounds(face,capProof.Frame);
                            if(options.SurfaceBranch==ExtrusionSurfaceBranch.Near?bounds.Max.Z>=-1e-6:bounds.Min.Z<=1e-6)
                                throw new CadValidationException("Native terminal cap differs from the explicitly selected analytic entry/exit branch or contains a directional tangency.");
                        }
                        capArea+=covered;
                    }
                    if(capArea<=1e-8)throw new CadValidationException("No complete native terminal cap was proved on the original finite stop.");
                }
                finally{foreach(var face in envelopeFaces)face.Dispose();foreach(var face in deltaFaces)face.Dispose();}
                using(var check=RepairSnapshot.Create(source))if(check.Fingerprint!=sourceMap.Fingerprint)
                    throw new CadValidationException("Native surface extrusion changed its exact source representation.");
                using(var check=RepairSnapshot.Create(stopSource))if(check.Fingerprint!=stopMap.Fingerprint)
                    throw new CadValidationException("Native surface extrusion changed its original finite stop representation.");
                token.ThrowIfCancellationRequested();using var material=extractNewBody?CopyFiniteSurfaceMaterial(delta,token):null;
                using var stored=OcctGeometryBridge.StoreShape(material??result,assets);
                return new(stored.Geometry,GeometryAssetLease.Acquire(stored.Geometry,assets),
                    [new("HISTORY.UNSUPPORTED","Limited prism aggregate argument history does not prove a complete original-to-final topology map; reselect final topology explicitly."),
                     new("STYLE.UNSUPPORTED","New surface extrusion faces have no proved per-face style correspondence.")]);
            }
            finally{foreach(var face in shortFaces)face.Dispose();}
        }
        finally{foreach(var face in sourceFaces)face.Dispose();foreach(var face in stopFaces)face.Dispose();}
    }
    private sealed record FiniteSurfaceBranchProof(RigidTransform3d Frame,double Radius);
    private static FiniteSurfaceBranchProof? FiniteSurfaceAnalyticBranch(Shape face,SurfaceGeometryType kind,Vector3d direction)
    {
        if(kind==SurfaceGeometryType.Plane)return null;
        // Recognized OCCT analytic families use their canonical angular/axial
        // parameters. These exact evaluations recover coefficients; bounds below
        // certify the entire cap. They are not samples used to infer a surface type.
        Vector3d Point(double u,double v){var p=face.EvaluateFaceDerivatives(u,v).Point;return new(p.X,p.Y,p.Z);}
        var first=Point(0,0);var opposite=Point(Math.PI,0);var center=(first+opposite)*.5;
        double radius=(first-center).Length;
        if(!double.IsFinite(radius)||radius<=1e-6)throw new CadValidationException("The analytic stop has a degenerate radius.");
        var projection=direction;
        if(kind==SurfaceGeometryType.Cylinder)
        {
            var p=face.EvaluateFaceDerivatives(0,0).VDerivative;
            var axis=new Vector3d(p.X,p.Y,p.Z).Normalized();
            projection=direction-axis*axis.Dot(direction);
            if(projection.Length<=1e-8)throw new CadValidationException("The extrusion direction is parallel to the cylindrical stop axis.");
            projection=projection.Normalized();
        }
        return new(new(center,NormalRotation(projection)),radius);
    }
    private static Bounds3d SurfaceFrameBounds(Shape shape,RigidTransform3d frame)
    {
        using var transform=OcctGeometryBridge.ToNative(frame.Inverse());using var local=shape.Transformed(transform);
        var bounds=local.GetBoundingBox();
        return new(new(bounds.Minimum.X,bounds.Minimum.Y,bounds.Minimum.Z),new(bounds.Maximum.X,bounds.Maximum.Y,bounds.Maximum.Z));
    }
    private static double FiniteSurfaceArea(Shape shape)
    {
        var faces=shape.GetFaces();
        try{return faces.Sum(f=>AuthoringMeasure(f,InspectionPropertyKind.Area));}
        finally{foreach(var face in faces)face.Dispose();}
    }
    private static bool FiniteSurfaceIsProfilePlane(Shape face,RigidTransform3d frame,double z)
    {
        var surface=face.GetFaceSurfaceSnapshot();
        if(surface.SurfaceType!=SurfaceGeometryType.Plane)return false;
        var value=face.EvaluateFace(0,0);
        var normal=new Vector3d(value.Normal.X,value.Normal.Y,value.Normal.Z);
        var point=frame.Inverse().Apply(new(value.Point.X,value.Point.Y,value.Point.Z));
        return Math.Abs(Math.Abs(normal.Dot(frame.Rotation.Rotate(Vector3d.UnitZ)))-1)<=1e-10&&Math.Abs(point.Z-z)<=1e-8;
    }
}
