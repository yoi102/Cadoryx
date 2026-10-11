namespace Cadoryx.Db;

/// <summary>A directed line in part-local space; an associated line caches an explicitly selected datum axis.</summary>
public sealed record RevolveAxisReference(Vector3d Point,Vector3d Direction,Guid? Datum=null,Guid? DatumRevision=null)
{
    public void Validate()
    {
        DatumConstructions.Coordinate(Point);Direction.Validate();
        if(Math.Abs(Direction.Dot(Direction)-1)>1e-10||(Datum is null)!=(DatumRevision is null)||
            Datum==Guid.Empty||DatumRevision==Guid.Empty)
            throw new CadValidationException("Invalid revolution axis direction or datum identity.");
    }
    public (Vector3d Point,Vector3d Direction) InMeridian(RigidTransform3d placement)
    {
        Validate();placement.Validate();var inverse=placement.Inverse();
        var point=inverse.Apply(Point);var direction=inverse.Rotation.Rotate(Direction);
        if(Math.Abs(point.Y)>1e-7||Math.Abs(direction.Y)>1e-10)
            throw new CadValidationException("The revolution axis must lie in the profile's XZ meridian plane.");
        return(new(point.X,0,point.Z),new Vector3d(direction.X,0,direction.Z).Normalized());
    }
    public RevolveAxisReference Refresh(DocumentSnapshot doc,DefinitionId part)
    {
        if(Datum is not {} id)return this;
        var axis=DatumConstructions.Axis(doc,part,id);
        return this with{Point=axis.Frame.Translation,Direction=axis.Frame.Rotation.Rotate(Vector3d.UnitZ),DatumRevision=axis.Revision};
    }
    public bool SameLineValues(RevolveAxisReference other)=>(Point-other.Point).Length<=1e-8&&(Direction-other.Direction).Length<=1e-10;
    public void ValidateApplied(DocumentSnapshot doc,FeatureDefinition feature)
    {
        Validate();
        if(feature.IsStale)
        {
            if(Datum is {} id&&doc.Datums.Axes.TryGetValue(id,out var frozen)&&
                (frozen.IsStale||frozen.Revision==DatumRevision))
            {
                var retained=new RevolveAxisReference(frozen.Frame.Translation,frozen.Frame.Rotation.Rotate(Vector3d.UnitZ));
                if(!SameLineValues(retained))throw new CadValidationException("Frozen revolution axis cache differs from its retained datum geometry.");
            }
            return;
        }
        var current=Refresh(doc,feature.PartId);
        if(current.DatumRevision!=DatumRevision||!SameLineValues(current))
            throw new CadValidationException("The revolution axis cache is stale.");
    }
}
