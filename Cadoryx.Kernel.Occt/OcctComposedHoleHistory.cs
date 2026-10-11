using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel
{
    public const string ComposedHoleHistoryAdapterVersion="OcctSharp 8.0.1-preview.31.cadoryx.curve-inspection.6 / composed-drilling-history-v1";
    private static HoleRecipe Shaft(HoleRecipe recipe,GeometryAssetRef source)=>recipe with
        {Source=source,Head=HoleHead.Plain,HeadDiameter=0,HeadDepth=0,HeadAngle=0};
    private GeometryResult EvaluateComposedHole(HoleRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        GeometryResult? head=null;
        try
        {
            if(recipe.Head==HoleHead.Counterbore)
                head=EvaluateHole(Shaft(recipe,recipe.Source) with{Diameter=recipe.HeadDiameter,Through=false,Depth=recipe.HeadDepth,Standard=null},assets,token);
            else
            {
                // The pilot is a precondition witness only; its geometry is not a
                // physical operand of the head Boolean and cannot enter its map.
                using var pilot=EvaluateHole(Shaft(recipe,recipe.Source) with{Through=false,Depth=recipe.RecessDepth},assets,token);
                var point=recipe.Frame.Apply(new(recipe.Position.X,recipe.Position.Y,0));
                var rotation=recipe.Frame.Rotation*Quaterniond.FromAxisAngle(new(1,0,0),Math.PI);
                using var cone=ShapeFactory.CreateCone(recipe.HeadDiameter/2,recipe.Diameter/2,recipe.RecessDepth);
                using var transform=OcctGeometryBridge.ToNative(new(point,rotation));using var shape=cone.Transformed(transform);
                using var tool=OcctGeometryBridge.StoreShape(shape,assets);
                head=EvaluateBoolean(new(BooleanOperation.Cut,[recipe.Source,tool.Geometry]),assets,token);
            }
            using var shaft=EvaluateHole(Shaft(recipe,head.Geometry),assets,token);
            if(shaft.Geometry.Kind!=BodyKind.Solid)throw new CadValidationException("A hole must leave one valid solid.");
            return FinishComposedDrilling(shaft,[head.TopologyHistory,shaft.TopologyHistory],assets,token);
        }
        finally{head?.Dispose();}
    }
    private static GeometryResult FinishComposedDrilling(GeometryResult result,IReadOnlyList<TopologyHistory?> stages,IAssetStore assets,CancellationToken token,int nativeStages=2)
    {
        token.ThrowIfCancellationRequested();
        if(nativeStages>TopologyHistoryComposition.MaximumStages||stages.Count==0||stages.Any(s=>s is null))
            return new(result.Geometry,GeometryAssetLease.Acquire(result.Geometry,assets),[new("HISTORY.UNSUPPORTED",
                "A drilling stage has no complete verified native map or exceeds the composition budget. Reselect final topology explicitly.")]);
        var history=stages.Count==1?stages[0]! with{AdapterVersion=ComposedHoleHistoryAdapterVersion,SchemaVersion=3}:
            TopologyHistoryComposition.Compose(stages.Select(s=>s!).ToArray(),ComposedHoleHistoryAdapterVersion,token);
        history.Validate();token.ThrowIfCancellationRequested();
        return new(result.Geometry,GeometryAssetLease.Acquire(result.Geometry,assets)){TopologyHistory=history};
    }
}
