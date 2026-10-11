using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Typed C2 dependencies in the table's explicit coordinate occurrence.
/// The plane supplies Z and default origin/X; optional point and axis replace origin and X.</summary>
public sealed record DrawingHoleCoordinateBinding(Guid Plane,Guid? OriginPoint=null,Guid? XAxis=null,bool ReverseX=false)
{
    public void Validate()
    {
        if(Plane==Guid.Empty||OriginPoint==Guid.Empty||XAxis==Guid.Empty)
            throw new CadValidationException("Invalid hole coordinate datum identities.");
    }
    public (RigidTransform3d Frame,ImmutableArray<Guid> Revisions) Resolve(DocumentSnapshot document,DefinitionId owner)
    {
        Validate();var plane=DatumConstructions.Plane(document,owner,Plane);
        var origin=plane.Frame.Translation;var normal=plane.Frame.Rotation.Rotate(Vector3d.UnitZ);
        var x=plane.Frame.Rotation.Rotate(new Vector3d(1,0,0));ImmutableArray<Guid> revisions=[plane.Revision];
        if(OriginPoint is {} pointId)
        {
            var point=DatumConstructions.Point(document,owner,pointId);origin=point.Position;revisions=revisions.Add(point.Revision);
            if(Math.Abs((origin-plane.Frame.Translation).Dot(normal))>1e-7)
                throw new CadValidationException("Hole coordinate origin must lie in the selected datum plane.");
        }
        if(XAxis is {} axisId)
        {
            var axis=DatumConstructions.Axis(document,owner,axisId);x=axis.Frame.Rotation.Rotate(Vector3d.UnitZ);revisions=revisions.Add(axis.Revision);
            if(Math.Abs(x.Dot(normal))>1e-8)
                throw new CadValidationException("Hole coordinate X direction must be parallel to the selected datum plane.");
        }
        if(ReverseX)x=x*-1;
        return(OriginPoint is null&&XAxis is null&&!ReverseX?plane.Frame:DatumConstructions.Frame(origin,normal,x),revisions);
    }
}
