using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    private GeometryResult EvaluateHole(HoleRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        if(recipe.Thread?.Representation==ThreadHoleRepresentation.Modeled)
            return threadExecutor is {} executor?executor.EvaluateAsync(recipe,assets,token).GetAwaiter().GetResult():EvaluateModeledThreadHole(recipe,assets,token);
        using var shape=OcctGeometryBridge.ReadShape(recipe.Source,assets);
        using var source=RepairSnapshot.Create(shape);
        if(source.Topology.Count(t=>t.Kind==ShapeKind.Solid)!=1)
            throw new CadValidationException("A hole requires exactly one solid target.");
        var point=recipe.Frame.Apply(new(recipe.Position.X,recipe.Position.Y,0));
        var direction=recipe.Frame.Rotation.Rotate(new(0,0,-1));
        var drillingRotation=recipe.Frame.Rotation*Quaterniond.FromAxisAngle(new(1,0,0),Math.PI);
        LocalFeatureResult Drill(RepairSnapshot target,double radius,bool through,double depth)
        {
            token.ThrowIfCancellationRequested();
            var operation=LocalFeatures.Hole(target,new(through?LocalHoleMode.ThroughAll:LocalHoleMode.Blind,
                new(point.X,point.Y,point.Z),new(direction.X,direction.Y,direction.Z),radius,Last:depth));
            try
            {
                if(!operation.Diagnostics.AlgorithmDone)throw new CadValidationException("Hole did not complete. Check entry, direction and blind depth.");
                var result=operation.RequireShape();
                using var check=RepairSnapshot.Create(result);
                if(check.Topology.Count(t=>t.Kind==ShapeKind.Solid)!=1)throw new CadValidationException("A hole must leave exactly one solid.");
                using var targetShape=target.CopyShape();
                double before=targetShape.InspectProperties(InspectionPropertyKind.Volume).Mass,after=result.InspectProperties(InspectionPropertyKind.Volume).Mass;
                if(!double.IsFinite(after)||after<=1e-9||before-after<=Math.Max(1e-9,before*1e-12))
                    throw new CadValidationException("Hole did not remove material or destroyed its target.");
                // Native through modes can cut the full axis even behind the origin. Independently prove
                // the actual removed volume equals a finite cutter in the authored drilling direction.
                // Bounds only enclose this cutter; they never infer blind depth, bottom faces or identity.
                double height=depth;
                if(through)
                {
                    var box=targetShape.GetBoundingBox();height=1;
                    foreach(double x in new[]{box.Minimum.X,box.Maximum.X})foreach(double y in new[]{box.Minimum.Y,box.Maximum.Y})foreach(double z in new[]{box.Minimum.Z,box.Maximum.Z})
                        height=Math.Max(height,(new Vector3d(x,y,z)-point).Length+1);
                }
                using var cylinder=ShapeFactory.CreateCylinder(radius,height);
                using var transform=OcctGeometryBridge.ToNative(new(point,drillingRotation));using var cutter=cylinder.Transformed(transform);
                using var common=targetShape.Common(cutter);double expected=Math.Abs(common.InspectProperties(InspectionPropertyKind.Volume).Mass);
                if(expected<=1e-9||Math.Abs(before-after-expected)>Math.Max(1e-6,expected*1e-7))
                    throw new CadValidationException("Native hole removal differs from the authored entry, direction or depth.");
                return operation;
            }
            catch{operation.Dispose();throw;}
        }
        if(recipe.Head==HoleHead.Plain)
        {
            var operation=Drill(source,recipe.Diameter/2,recipe.Through,recipe.Depth);
            return FinishLocal(recipe.Source,source,operation,assets,token);
        }
        return EvaluateComposedHole(recipe,assets,token);
    }
}
