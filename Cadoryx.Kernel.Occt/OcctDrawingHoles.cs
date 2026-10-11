using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:IDrawingHoleResolver
{
    public async Task<DrawingHoleEvidence> ResolveDrawingHoleAsync(DocumentSnapshot document,DrawingTopologyReference shaft,
        IAssetStore assets,CancellationToken token=default)
    {
        shaft.Validate(document.Id);
        bool modeled=shaft.FeatureId is {} feature&&document.Features[feature].Recipe is HoleRecipe {Thread.Representation:ThreadHoleRepresentation.Modeled};
        if(!shaft.IsCurrent(document)||shaft.Kind!=TopologyKind.Face||
            (modeled?shaft.Geometry is not (DrawingAttachmentGeometry.CylinderFace or DrawingAttachmentGeometry.GeneralFace):shaft.Geometry!=DrawingAttachmentGeometry.CylinderFace))
            throw new CadValidationException("Select a current original shaft wall or a directly authored thread tooth face.");
        using var hold=assets.Acquire(shaft.Asset);
        await ConfirmDrawingAttachmentAsync(document,shaft,assets,token).ConfigureAwait(false);
        return await Run(()=>
        {
            if(modeled)
            {
                var modeledRecipe=(HoleRecipe)document.Features[shaft.FeatureId!.Value].Recipe;var current=document.Bodies[shaft.BodyId].Geometry;
                return threadExecutor is {} executor?executor.InspectAsync(modeledRecipe,current,shaft,assets,token).GetAwaiter().GetResult():
                    InspectModeledThreadDrawing(current,shaft,modeledRecipe,assets,token);
            }
            using var shape=OcctGeometryBridge.ReadShape(document.Bodies[shaft.BodyId].Geometry,assets);
            NativeHoleInspector.Evidence proof;
            try{proof=NativeHoleInspector.Inspect(shape,shaft.FullTopologyIndex,token);}
            catch(InvalidOperationException error){throw new CadValidationException(error.Message);}
            static Vector3d P(NativeHoleInspector.Point p)=>new(p.X,p.Y,p.Z);
            var result=new DrawingHoleEvidence(shaft,P(proof.Entry),P(proof.Direction),proof.Diameter,proof.Through,
                proof.Depth,(HoleHead)proof.Head,proof.HeadDiameter,proof.HeadDepth,proof.HeadAngle){ProvenFaceIndices=[..proof.Faces]};
            // This label is available only for a directly producing current hole whose entry,
            // direction and all manufacturing dimensions match the independent solid proof.
            if(shaft.FeatureId is {} id&&document.Features[id].Recipe is HoleRecipe recipe&&
                (recipe.Frame.Apply(new(recipe.Position.X,recipe.Position.Y,0))-result.Entry).Length<1e-6&&
                (recipe.Frame.Rotation.Rotate(new(0,0,-1))-result.Direction).Length<1e-6&&
                Math.Abs(recipe.Diameter-result.DiameterMm)<1e-6&&recipe.Through==result.Through&&
                (recipe.Through||Math.Abs(recipe.Depth-result.DepthMm)<1e-6)&&recipe.Head==result.Head&&
                (recipe.Head==HoleHead.Plain||Math.Abs(recipe.HeadDiameter-result.HeadDiameterMm)<1e-6&&
                    Math.Abs(recipe.RecessDepth-result.HeadDepthMm)<1e-6&&Math.Abs(recipe.HeadAngle-result.HeadAngle)<1e-6))
            {
                recipe.Validate();
                result=result with{SpecificationSource=DrawingHoleSpecificationSource.CurrentAuthoredHole,AuthoredFeature=id,Standard=recipe.Standard,Thread=recipe.Thread};
            }
            result.Validate(document.Id);token.ThrowIfCancellationRequested();return result;
        },token).ConfigureAwait(false);
    }
}
