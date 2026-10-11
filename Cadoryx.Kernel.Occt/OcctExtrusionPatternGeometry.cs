using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private GeometryResult EvaluateExtrusionPattern(ExtrusionPatternRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        var layout=recipe.Layout;var source=layout.IncludeSource?recipe.Source:recipe.Template.Options!.Source!;
        Shape current=OcctGeometryBridge.ReadShape(source,assets);
        try
        {
            for(int i=layout.IncludeSource?1:0;i<layout.Count;i++)
            {
                token.ThrowIfCancellationRequested();using var inverse=ExtrusionCopyTransform(layout,i,true);
                // Evaluate the actual operation in the seed's authoring coordinates. Reflection is its own
                // inverse; no negative-determinant transform is encoded as a quaternion or flattened polygon.
                using var localTarget=current.Transformed(inverse);
                using var operation=EvaluateExtrusion(recipe.Template,assets,token,localTarget);
                using var localResult=OcctGeometryBridge.ReadShape(operation.Geometry,assets);
                using var transform=ExtrusionCopyTransform(layout,i,false);var next=localResult.Transformed(transform);
                current.Dispose();current=next;
            }
            token.ThrowIfCancellationRequested();using var stored=OcctGeometryBridge.StoreShape(current,assets);
            return new(stored.Geometry,GeometryAssetLease.Acquire(stored.Geometry,assets),[new("HISTORY.UNSUPPORTED",
                "Extrusion operations are replayed sequentially in authored local coordinates. No original-to-final topology map is composed; select final topology explicitly.")]);
        }
        finally{current.Dispose();}
    }
    private static GpTrsf ExtrusionCopyTransform(RepetitionRecipe layout,int index,bool inverse)
    {
        if(layout.Kind==RepetitionKind.Mirror)
        {var p=layout.Frame.Translation;var n=layout.Frame.Rotation.Rotate(Vector3d.UnitZ);return GpTrsf.CreatePlaneMirror(p.X,p.Y,p.Z,n.X,n.Y,n.Z);}
        RigidTransform3d transform;
        if(layout.Kind==RepetitionKind.Linear)transform=new(layout.Frame.Rotation.Rotate(new(1,0,0))*(index*layout.Step),Quaterniond.Identity);
        else
        {var rotation=Quaterniond.FromAxisAngle(layout.Frame.Rotation.Rotate(Vector3d.UnitZ),index*(layout.FullCircle?Math.Tau/layout.Count:layout.Step));transform=new(layout.Frame.Translation-rotation.Rotate(layout.Frame.Translation),rotation);}
        return OcctGeometryBridge.ToNative(inverse?transform.Inverse():transform);
    }
}
