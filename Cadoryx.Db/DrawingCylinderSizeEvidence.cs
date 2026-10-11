using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>A complete, finite original cylindrical size wall. This proves size/axis identity;
/// it does not prove that an internal cavity is an open, accessible manufacturing hole.</summary>
public sealed record DrawingCylinderSizeEvidence(DrawingTopologyReference Source,Vector3d Origin,Vector3d Direction,
    double DiameterMm,double LengthMm,bool Internal)
{
    public ImmutableArray<int> ProvenFaceIndices {get;init;}=[];
    public ImmutableArray<int> RingEdgeIndices {get;init;}=[];
    public void Validate(DocumentId document)
    {
        Source.Validate(document);Origin.Validate();Direction.Validate();
        if(Source.Geometry!=DrawingAttachmentGeometry.CylinderFace||Math.Abs(Direction.Length-1)>1e-8||
            !double.IsFinite(DiameterMm)||DiameterMm is <=.000002 or >1e6||
            !double.IsFinite(LengthMm)||LengthMm is <=.000001 or >1e6||
            ProvenFaceIndices.IsDefault||ProvenFaceIndices.Length!=3||ProvenFaceIndices.Any(i=>i is <0 or >100000)||
            ProvenFaceIndices.Distinct().Count()!=3||!ProvenFaceIndices.Contains(Source.FullTopologyIndex)||
            RingEdgeIndices.IsDefault||RingEdgeIndices.Length is <2 or >32||RingEdgeIndices.Any(i=>i is <0 or >100000)||
            RingEdgeIndices.Distinct().Count()!=RingEdgeIndices.Length||RingEdgeIndices.Intersect(ProvenFaceIndices).Any())
            throw new CadValidationException("Invalid original cylindrical size feature evidence.");
    }
    public bool SameProof(DrawingCylinderSizeEvidence other)=>Source==other.Source&&Origin==other.Origin&&Direction==other.Direction&&
        DiameterMm==other.DiameterMm&&LengthMm==other.LengthMm&&Internal==other.Internal&&
        ProvenFaceIndices.SequenceEqual(other.ProvenFaceIndices)&&RingEdgeIndices.SequenceEqual(other.RingEdgeIndices);
}
