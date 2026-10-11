using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:IDrawingSizeResolver
{
    public async Task<DrawingCylinderSizeEvidence> ResolveDrawingCylinderSizeAsync(DocumentSnapshot document,DrawingTopologyReference source,
        IAssetStore assets,CancellationToken token=default)
    {
        source.Validate(document.Id);
        if(!source.IsCurrent(document)||source.Geometry!=DrawingAttachmentGeometry.CylinderFace)
            throw new CadValidationException("Select a current original cylindrical size wall.");
        using var hold=assets.Acquire(source.Asset);
        await ConfirmDrawingAttachmentAsync(document,source,assets,token).ConfigureAwait(false);
        return await Run(()=>
        {
            using var shape=OcctGeometryBridge.ReadShape(document.Bodies[source.BodyId].Geometry,assets);
            NativeHoleInspector.CylinderEvidence proof;
            try{proof=NativeHoleInspector.InspectCylinder(shape,source.FullTopologyIndex,token);}
            catch(InvalidOperationException error){throw new CadValidationException(error.Message);}
            static Vector3d P(NativeHoleInspector.Point point)=>new(point.X,point.Y,point.Z);
            var evidence=new DrawingCylinderSizeEvidence(source,P(proof.Origin),P(proof.Direction),proof.Diameter,proof.Length,proof.Internal)
                {ProvenFaceIndices=[..proof.Faces],RingEdgeIndices=[..proof.RingEdges]};
            evidence.Validate(document.Id);token.ThrowIfCancellationRequested();return evidence;
        },token).ConfigureAwait(false);
    }
}
