using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Two explicitly selected original faces and the complete finite
/// polygonal prism domain proving an external thickness or internal width.</summary>
public sealed record DrawingPlanarSizeEvidence(DrawingTopologyReference Source,DrawingTopologyReference OppositeSource,
    Vector3d Origin,Vector3d Direction,double WidthMm,bool Internal)
{
    public ImmutableArray<int> ProvenFaceIndices {get;init;}=[];
    public ImmutableArray<int> FirstBoundaryEdgeIndices {get;init;}=[];
    public ImmutableArray<int> SecondBoundaryEdgeIndices {get;init;}=[];
    public ImmutableArray<int> ConnectingEdgeIndices {get;init;}=[];
    public ImmutableArray<int> PrismCapFaceIndices {get;init;}=[];
    public ImmutableArray<int> PrismBoundaryEdgeIndices {get;init;}=[];
    public void Validate(DocumentId document)
    {
        Source.Validate(document);OppositeSource.Validate(document);Origin.Validate();Direction.Validate();
        if(Source.Kind!=TopologyKind.Face||OppositeSource.Kind!=TopologyKind.Face||
            Source.Geometry is not (DrawingAttachmentGeometry.PlaneFace or DrawingAttachmentGeometry.GeneralFace)||
            OppositeSource.Geometry is not (DrawingAttachmentGeometry.PlaneFace or DrawingAttachmentGeometry.GeneralFace)||
            !Source.Path.Equals(OppositeSource.Path)||Source.DefinitionId!=OppositeSource.DefinitionId||Source.BodyId!=OppositeSource.BodyId||
            Source.FeatureId!=OppositeSource.FeatureId||Source.Revision!=OppositeSource.Revision||Source.Asset!=OppositeSource.Asset||
            Source.Fingerprint!=OppositeSource.Fingerprint||Source.FullTopologyIndex==OppositeSource.FullTopologyIndex||
            Math.Abs(Direction.Length-1)>1e-8||!double.IsFinite(WidthMm)||WidthMm is <=.000001 or >1e6)
            throw new CadValidationException("A planar size requires two distinct exact faces on the same current solid and a finite positive width.");
        static bool Indices(ImmutableArray<int> items,int min,int max)=>!items.IsDefault&&items.Length>=min&&items.Length<=max&&
            items.All(i=>i is >=0 and <=100000)&&items.Distinct().Count()==items.Length;
        if(!Indices(ProvenFaceIndices,5,66)||!Indices(FirstBoundaryEdgeIndices,3,64)||!Indices(SecondBoundaryEdgeIndices,3,64)||
            !Indices(ConnectingEdgeIndices,3,64)||!Indices(PrismCapFaceIndices,2,2)||!Indices(PrismBoundaryEdgeIndices,6,128)||
            !ProvenFaceIndices.Contains(Source.FullTopologyIndex)||!ProvenFaceIndices.Contains(OppositeSource.FullTopologyIndex)||
            PrismCapFaceIndices.Except(ProvenFaceIndices).Any()||PrismBoundaryEdgeIndices.Length!=2*ConnectingEdgeIndices.Length||
            PrismBoundaryEdgeIndices.Intersect(ConnectingEdgeIndices).Any()||
            ProvenFaceIndices.Intersect(FirstBoundaryEdgeIndices.Concat(SecondBoundaryEdgeIndices).Concat(ConnectingEdgeIndices).Concat(PrismBoundaryEdgeIndices)).Any()||
            FirstBoundaryEdgeIndices.Length!=SecondBoundaryEdgeIndices.Length||
            (Internal?(FirstBoundaryEdgeIndices.Length!=4||PrismCapFaceIndices.Contains(Source.FullTopologyIndex)||PrismCapFaceIndices.Contains(OppositeSource.FullTopologyIndex)):
                (!PrismCapFaceIndices.Contains(Source.FullTopologyIndex)||!PrismCapFaceIndices.Contains(OppositeSource.FullTopologyIndex)||
                 FirstBoundaryEdgeIndices.Intersect(SecondBoundaryEdgeIndices).Any()||FirstBoundaryEdgeIndices.Length!=ConnectingEdgeIndices.Length)))
            throw new CadValidationException("Incomplete original finite planar size boundary proof.");
    }
    public bool SameProof(DrawingPlanarSizeEvidence other)=>Source==other.Source&&OppositeSource==other.OppositeSource&&
        Origin==other.Origin&&Direction==other.Direction&&WidthMm==other.WidthMm&&Internal==other.Internal&&
        ProvenFaceIndices.SequenceEqual(other.ProvenFaceIndices)&&FirstBoundaryEdgeIndices.SequenceEqual(other.FirstBoundaryEdgeIndices)&&
        SecondBoundaryEdgeIndices.SequenceEqual(other.SecondBoundaryEdgeIndices)&&ConnectingEdgeIndices.SequenceEqual(other.ConnectingEdgeIndices)&&
        PrismCapFaceIndices.SequenceEqual(other.PrismCapFaceIndices)&&PrismBoundaryEdgeIndices.SequenceEqual(other.PrismBoundaryEdgeIndices);
}
