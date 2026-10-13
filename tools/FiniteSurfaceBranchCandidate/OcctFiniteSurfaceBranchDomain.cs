using Cadoryx.Db;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    // A geometric operation on a private deep copy, not a replacement selection.
    // Original document/asset/revision/fingerprint/member checks stay in the caller.
    private static Shape CopyFiniteSurfaceBranchDomain(Shape selectedStop,
        FiniteSurfaceBranchProof? proof,ExtrusionSurfaceBranch requested,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var snapshot=RepairSnapshot.Create(selectedStop);
        using var independent=snapshot.CopyShape();
        if(proof is null)return independent.Clone();
        var bounds=SurfaceFrameBounds(independent,proof.Frame);
        bool far=requested==ExtrusionSurfaceBranch.Far;
        if(far?bounds.Max.Z<=1e-6:bounds.Min.Z>=-1e-6)
            throw new CadValidationException("The original finite face has no material on the explicitly selected analytic branch.");
        double margin=Math.Max(1,proof.Radius*1e-6);
        double minimumZ=far?0:bounds.Min.Z-margin;
        double maximumZ=far?bounds.Max.Z+margin:0;
        double width=bounds.Max.X-bounds.Min.X+2*margin;
        double depth=bounds.Max.Y-bounds.Min.Y+2*margin;
        double height=maximumZ-minimumZ;
        CadGuard.Finite(width,depth,height,minimumZ);
        if(width<=0||depth<=0||height<=0)
            throw new CadValidationException("The exact branch domain has degenerate bounds.");
        using var localBox=ShapeFactory.CreateBox(width,depth,height);
        using var placement=OcctGeometryBridge.ToNative(proof.Frame*
            RigidTransform3d.Translate(bounds.Min.X-margin,bounds.Min.Y-margin,minimumZ));
        using var limiter=localBox.Transformed(placement);
        token.ThrowIfCancellationRequested();
        var branch=independent.Common(limiter);
        try
        {
            token.ThrowIfCancellationRequested();
            if(FiniteSurfaceArea(branch)<=1e-8)
                throw new CadValidationException("The original finite face does not cover any selected branch material.");
            return branch;
        }
        catch{branch.Dispose();throw;}
    }
}
