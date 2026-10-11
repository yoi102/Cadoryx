using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:IDrawingPlanarSizeResolver
{
    public async Task<DrawingPlanarSizeEvidence> ResolveDrawingPlanarSizeAsync(DocumentSnapshot document,DrawingTopologyReference source,
        DrawingTopologyReference opposite,bool internalWidth,IAssetStore assets,CancellationToken token=default)
    {
        source.Validate(document.Id);opposite.Validate(document.Id);
        if(!source.IsCurrent(document)||!opposite.IsCurrent(document)||source.Kind!=TopologyKind.Face||opposite.Kind!=TopologyKind.Face||
            !source.Path.Equals(opposite.Path)||source.DefinitionId!=opposite.DefinitionId||source.BodyId!=opposite.BodyId||
            source.FeatureId!=opposite.FeatureId||source.Revision!=opposite.Revision||source.Asset!=opposite.Asset||
            source.Fingerprint!=opposite.Fingerprint||source.FullTopologyIndex==opposite.FullTopologyIndex)
            throw new CadValidationException("Select two distinct current original planar size faces on one solid.");
        using var hold=assets.Acquire(source.Asset);
        await ConfirmDrawingAttachmentAsync(document,source,assets,token).ConfigureAwait(false);
        await ConfirmDrawingAttachmentAsync(document,opposite,assets,token).ConfigureAwait(false);
        return await Run(()=>
        {
            using var shape=OcctGeometryBridge.ReadShape(document.Bodies[source.BodyId].Geometry,assets);
            NativePlanarSizeInspector.Evidence proof;
            try{proof=internalWidth?NativePlanarSizeInspector.InspectInternalWidth(shape,source.FullTopologyIndex,opposite.FullTopologyIndex,token):
                NativePlanarSizeInspector.InspectPrism(shape,source.FullTopologyIndex,opposite.FullTopologyIndex,token);}
            catch(InvalidOperationException error){throw new CadValidationException(error.Message);}
            static Vector3d P(NativePlanarSizeInspector.Point p)=>new(p.X,p.Y,p.Z);
            var result=new DrawingPlanarSizeEvidence(source,opposite,P(proof.Origin),P(proof.Direction),proof.Width,proof.Internal)
                {ProvenFaceIndices=[..proof.Faces],FirstBoundaryEdgeIndices=[..proof.FirstEdges],SecondBoundaryEdgeIndices=[..proof.SecondEdges],
                 ConnectingEdgeIndices=[..proof.ConnectingEdges],PrismCapFaceIndices=[..proof.PrismCapFaces],PrismBoundaryEdgeIndices=[..proof.PrismBoundaryEdges]};
            result.Validate(document.Id);token.ThrowIfCancellationRequested();return result;
        },token).ConfigureAwait(false);
    }
}
