using System.Collections.Immutable;

namespace Cadoryx.Db;

public enum DrawingHoleSpecificationSource { CurrentGeometry,CurrentAuthoredHole }

/// <summary>Owned manufacturing dimensions proved on the exact current original solid.
/// Depth includes the head recess. No historical recipe is evidence of a surviving hole.</summary>
public sealed record DrawingHoleEvidence(DrawingTopologyReference Source,Vector3d Entry,Vector3d Direction,
    double DiameterMm,bool Through,double DepthMm,HoleHead Head,double HeadDiameterMm,double HeadDepthMm,double HeadAngle,
    DrawingHoleSpecificationSource SpecificationSource=DrawingHoleSpecificationSource.CurrentGeometry,
    FeatureId? AuthoredFeature=null,MetricClearanceSpec? Standard=null,MetricThreadSpec? Thread=null)
{
    public ImmutableArray<int> ProvenFaceIndices {get;init;}=[];
    public void Validate(DocumentId document)
    {
        Source.Validate(document);Entry.Validate();Direction.Validate();
        bool modeled=Thread?.Representation==ThreadHoleRepresentation.Modeled;
        if((modeled?Source.Geometry is not (DrawingAttachmentGeometry.CylinderFace or DrawingAttachmentGeometry.GeneralFace):Source.Geometry!=DrawingAttachmentGeometry.CylinderFace)||Math.Abs(Direction.Length-1)>1e-8||
            !Enum.IsDefined(Head)||!Enum.IsDefined(SpecificationSource)||
            !double.IsFinite(DiameterMm)||DiameterMm is <=.000002 or >1e6||
            !double.IsFinite(DepthMm)||DepthMm is <=.000001 or >1e6||
            !double.IsFinite(HeadDiameterMm)||!double.IsFinite(HeadDepthMm)||!double.IsFinite(HeadAngle)||
            ProvenFaceIndices.IsDefault||ProvenFaceIndices.Length<3||ProvenFaceIndices.Length>(modeled?4096:8)||
            ProvenFaceIndices.Any(i=>i is <0 or >100000)||ProvenFaceIndices.Distinct().Count()!=ProvenFaceIndices.Length||
            !ProvenFaceIndices.Contains(Source.FullTopologyIndex)||
            Head==HoleHead.Plain&&(HeadDiameterMm!=0||HeadDepthMm!=0||HeadAngle!=0)||
            Head!=HoleHead.Plain&&(HeadDiameterMm<=DiameterMm||HeadDepthMm<=0||HeadDepthMm>=DepthMm)||
            Head==HoleHead.Counterbore&&HeadAngle!=0||Head==HoleHead.Countersink&&HeadAngle is <=0 or >=Math.PI||
            (SpecificationSource==DrawingHoleSpecificationSource.CurrentAuthoredHole)!=(AuthoredFeature is not null)||
            (Standard is not null||Thread is not null)&&AuthoredFeature is null)
            throw new CadValidationException("Invalid current manufacturing hole evidence.");
        if(AuthoredFeature is {} feature){CadGuard.Id(feature);if(Source.FeatureId!=feature)throw new CadValidationException("Hole specification requires the current producing feature.");}
        if(Standard is {} standard&&Math.Abs(DiameterMm-MetricClearanceHoles.Diameter(standard))>1e-6)
            throw new CadValidationException("Clearance specification differs from the current hole diameter.");
        // Through holes have a measured solid depth; the author's through depth is always zero.
        Thread?.ValidateForHole(DiameterMm,Through,Through?0:DepthMm,Head,Standard,1e-6);
    }
    public bool SameProof(DrawingHoleEvidence other)=>Source==other.Source&&Entry==other.Entry&&Direction==other.Direction&&
        DiameterMm==other.DiameterMm&&Through==other.Through&&DepthMm==other.DepthMm&&Head==other.Head&&
        HeadDiameterMm==other.HeadDiameterMm&&HeadDepthMm==other.HeadDepthMm&&HeadAngle==other.HeadAngle&&
        SpecificationSource==other.SpecificationSource&&AuthoredFeature==other.AuthoredFeature&&Standard==other.Standard&&Thread==other.Thread&&
        ProvenFaceIndices.SequenceEqual(other.ProvenFaceIndices);
}
public sealed record DrawingHoleRow(Guid Id,int Number,DrawingHoleEvidence Evidence,Point2d CoordinatesMm,string? StaleReason=null)
{
    public void Validate(DocumentId document)
    {
        if(Id==Guid.Empty||Number is <1 or >9999||!double.IsFinite(CoordinatesMm.X)||!double.IsFinite(CoordinatesMm.Y)||
            Math.Abs(CoordinatesMm.X)>1e9||Math.Abs(CoordinatesMm.Y)>1e9||StaleReason is {Length:>2048})
            throw new CadValidationException("Invalid manufacturing hole row.");
        Evidence.Validate(document);
    }
}
/// <summary>Explicit origin and axes in one exact occurrence's local coordinates.
/// Rows may reference other instances; their coordinates follow each current world placement.</summary>
public sealed record DrawingHoleTable(Guid Id,Guid ViewId,string Name,OccurrencePath CoordinatePath,
    DefinitionId CoordinateDefinition,RigidTransform3d LocalFrame,Point2d PositionMm)
{
    public ImmutableArray<DrawingHoleRow> Rows {get;init;}=[];
    public string? StaleReason {get;init;}
    public DrawingHoleCoordinateBinding? CoordinateBinding {get;init;}
    public ImmutableArray<Guid> CoordinateRevisions {get;init;}=[];
    public void Validate(DocumentId document)
    {
        if(Id==Guid.Empty||ViewId==Guid.Empty||Name is not {Length:>=1 and <=32}||Name.Any(char.IsControl)||
            CoordinatePath is null||CoordinatePath.DocumentId!=document||CoordinatePath.Slots.Length is <1 or >128||
            !double.IsFinite(PositionMm.X)||!double.IsFinite(PositionMm.Y)||Rows.IsDefault||Rows.Length>32||
            StaleReason is {Length:>2048})throw new CadValidationException("Invalid manufacturing hole table.");
        CadGuard.Id(CoordinateDefinition);LocalFrame.Validate();
        CoordinateBinding?.Validate();
        if(CoordinateRevisions.IsDefault||CoordinateRevisions.Any(r=>r==Guid.Empty)||
            CoordinateRevisions.Length!=(CoordinateBinding is {} binding?1+(binding.OriginPoint is null?0:1)+(binding.XAxis is null?0:1):0))
            throw new CadValidationException("Invalid hole coordinate revision cache.");
        foreach(var row in Rows){if(row is null)throw new CadValidationException("Missing hole row.");row.Validate(document);}
        if(Rows.Select(r=>r.Id).Distinct().Count()!=Rows.Length||Rows.Select(r=>r.Number).Distinct().Count()!=Rows.Length||
            Rows.Select(r=>(r.Evidence.Source.Path,r.Evidence.Source.BodyId,r.Evidence.Source.FullTopologyIndex)).Distinct().Count()!=Rows.Length)
            throw new CadValidationException("Hole row IDs, numbers and selected original walls must be unique in a table.");
    }
    public Point2d Coordinates(DocumentSnapshot document,DrawingHoleEvidence evidence)
    {
        var occurrences=document.EnumerateOccurrences().ToDictionary(o=>o.Path);
        if(!occurrences.TryGetValue(CoordinatePath,out var basis)||basis.DefinitionId!=CoordinateDefinition||
            !occurrences.TryGetValue(evidence.Source.Path,out var source)||source.DefinitionId!=evidence.Source.DefinitionId)
            throw new CadValidationException("Hole table coordinate occurrence is missing or changed.");
        var frame=basis.WorldTransform*(CoordinateBinding?.Resolve(document,CoordinateDefinition).Frame??LocalFrame);
        var direction=frame.Rotation.Inverse().Rotate(source.WorldTransform.Rotation.Rotate(evidence.Direction));
        if(Math.Abs(direction.Z)<1-1e-8)throw new CadValidationException("Hole axis must be parallel to the explicit table Z axis.");
        var point=frame.Inverse().Apply(source.WorldTransform.Apply(evidence.Entry));
        return new(point.X,point.Y);
    }
    public DrawingHoleTable RefreshCoordinates(DocumentSnapshot document)
    {
        if(!document.EnumerateOccurrences().Any(o=>o.Path.Equals(CoordinatePath)&&o.DefinitionId==CoordinateDefinition))
            throw new CadValidationException("Hole coordinate occurrence is missing or changed.");
        if(CoordinateBinding is null)return CoordinateRevisions.IsEmpty?this:this with{CoordinateRevisions=[]};
        var(frame,revisions)=CoordinateBinding.Resolve(document,CoordinateDefinition);
        if(LocalFrame==frame&&CoordinateRevisions.SequenceEqual(revisions))return this;
        return this with{LocalFrame=frame,CoordinateRevisions=revisions};
    }
    public void ConfirmCoordinateCache(DocumentSnapshot document)
    {
        var actual=RefreshCoordinates(document);
        if(actual.LocalFrame!=LocalFrame||!actual.CoordinateRevisions.SequenceEqual(CoordinateRevisions))
            throw new CadValidationException("Hole coordinate cache differs from its current named datums.");
    }
}
