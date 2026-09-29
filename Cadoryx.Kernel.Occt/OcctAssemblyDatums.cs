using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel : IAssemblyDatumResolver
{
    /// <summary>Extracts only provable analytic datums from the exact original BRep map.</summary>
    public Task<AssemblyDatumReference> ResolveAssemblyDatumAsync(DocumentSnapshot document,
        OccurrencePath path,BodyId bodyId,int fullTopologyIndex,string fingerprint,
        IAssetStore assets,CancellationToken token=default)=>Run(()=>
    {
        token.ThrowIfCancellationRequested();
        var occurrence=document.EnumerateOccurrences().SingleOrDefault(o=>o.Path.Equals(path))??
            throw new CadValidationException("The selected instance path no longer exists.");
        if(document.Definitions[occurrence.DefinitionId] is not PartDefinition part||
           !part.Bodies.Contains(bodyId)||!document.Bodies.TryGetValue(bodyId,out var body))
            throw new CadValidationException("The selected body is not in this instance definition.");
        var geometry=body.Geometry;
        using var shape=OcctGeometryBridge.ReadShape(geometry,assets);
        using var map=RepairSnapshot.Create(shape);
        if(map.Fingerprint!=fingerprint||fullTopologyIndex<0||fullTopologyIndex>=map.Topology.Count)
            throw new CadValidationException("The picked BRep map changed; reselect the face or edge.");
        var kind=map.Topology[fullTopologyIndex].Kind;
        if(kind is not (ShapeKind.Face or ShapeKind.Edge))
            throw new CadValidationException("Select a plane, cylindrical face or circular edge.");
        using var subshape=map.CopySubshape(map.Select(fullTopologyIndex));
        AssemblyDatumGeometry datumKind;Vector3d point,axis;double radius=0;
        if(kind==ShapeKind.Face)
        {
            var surface=subshape.GetFaceSurfaceSnapshot();
            if(surface.SurfaceType is not (SurfaceGeometryType.Plane or SurfaceGeometryType.Cylinder))
                throw new CadValidationException("Only analytic planar or cylindrical faces can be assembly datums.");
            var u=Mid(surface.FirstUParameter,surface.LastUParameter);
            var v=Mid(surface.FirstVParameter,surface.LastVParameter);
            if(surface.SurfaceType==SurfaceGeometryType.Plane)
            {
                var evaluation=subshape.EvaluateFace(u,v);
                point=P(evaluation.Point);axis=P(evaluation.Normal).Normalized();
                datumKind=AssemblyDatumGeometry.PlaneFace;
            }
            else
            {
                if(!double.IsFinite(surface.FirstUParameter)||!double.IsFinite(surface.LastUParameter)||
                   surface.LastUParameter-surface.FirstUParameter<0.1)
                    throw new CadValidationException("Cylindrical face has an ambiguous angular range.");
                double span=surface.LastUParameter-surface.FirstUParameter;
                var p=P(subshape.EvaluateFace(surface.FirstUParameter+span*0.2,v).Point);
                var q=P(subshape.EvaluateFace(surface.FirstUParameter+span*0.5,v).Point);
                var r=P(subshape.EvaluateFace(surface.FirstUParameter+span*0.8,v).Point);
                (point,axis,radius)=Circle(p,q,r);
                var derivative=subshape.EvaluateFaceDerivatives(u,v);
                var direction=P(derivative.VDerivative).Normalized();
                axis=axis.Dot(direction)<0?axis*-1:axis;
                datumKind=AssemblyDatumGeometry.CylinderFace;
            }
        }
        else
        {
            var curve=subshape.GetEdgeCurveSnapshot();
            if(curve.CurveType!=CurveGeometryType.Circle||!double.IsFinite(curve.FirstParameter)||
               !double.IsFinite(curve.LastParameter)||curve.LastParameter-curve.FirstParameter<0.1)
                throw new CadValidationException("Only a nondegenerate analytic circular edge is a datum.");
            var span=curve.LastParameter-curve.FirstParameter;
            (point,axis,radius)=Circle(
                P(subshape.EvaluateEdge(curve.FirstParameter+span*0.2).Point),
                P(subshape.EvaluateEdge(curve.FirstParameter+span*0.5).Point),
                P(subshape.EvaluateEdge(curve.FirstParameter+span*0.8).Point));
            datumKind=AssemblyDatumGeometry.CircleEdge;
        }
        var result=new AssemblyDatumReference(path,occurrence.DefinitionId,bodyId,body.Producer,
            geometry.Revision,geometry.AssetId,map.Fingerprint,fullTopologyIndex,datumKind,
            point,axis,radius);
        result.Validate(document.Id);return result;
    },token);

    private static double Mid(double a,double b)
    {
        if(!double.IsFinite(a)||!double.IsFinite(b)||b<a)
            throw new CadValidationException("The selected analytic surface has no finite parameter bounds.");
        return a+(b-a)*0.5;
    }
    private static Vector3d P(GpPoint p)=>new(p.X,p.Y,p.Z);
    private static (Vector3d Center,Vector3d Axis,double Radius) Circle(Vector3d p,Vector3d q,Vector3d r)
    {
        var a=q-p;var b=r-p;var cross=a.Cross(b);double squared=cross.Dot(cross);
        if(!double.IsFinite(squared)||squared<1e-14)
            throw new CadValidationException("Circular datum is degenerate or too short to identify its center.");
        var center=p+(b.Cross(cross)*a.Dot(a)+cross.Cross(a)*b.Dot(b))/(2*squared);
        var radius=(p-center).Length;
        if(!double.IsFinite(radius)||radius<1e-6||radius>1e9)
            throw new CadValidationException("Circular datum radius is invalid.");
        return(center,cross.Normalized(),radius);
    }
}
