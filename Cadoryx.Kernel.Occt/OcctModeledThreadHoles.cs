using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using OcctSharp;

namespace Cadoryx.Kernel.Occt;

public sealed partial class OcctGeometryKernel:IMetricThreadVerifier
{
    internal Action? ThreadNativeStarted {get;set;}
    private GeometryResult EvaluateModeledThreadHole(HoleRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        using var pilot=EvaluateModeledThreadPilot(recipe,assets,token);
        using var threaded=EvaluateModeledThreadCut(recipe,pilot.Geometry,assets,token);
        return FinishComposedDrilling(threaded,[pilot.TopologyHistory,threaded.TopologyHistory],assets,token);
    }

    private GeometryResult EvaluateModeledThreadPilot(HoleRecipe recipe,IAssetStore assets,CancellationToken token)
    {
        if(!recipe.Through)return EvaluateHole(recipe with{Thread=null},assets,token);
        // Build both periodic cutting operands in the same authored frame. The native
        // infinite-hole wall and a separately rotated thread tool can fail a later cut
        // while still returning valid topology; independent removed volume rejects it.
        recipe.Validate();using var source=OcctGeometryBridge.ReadShape(recipe.Source,assets);
        var point=recipe.Frame.Apply(new(recipe.Position.X,recipe.Position.Y,0));
        var frame=new RigidTransform3d(point,recipe.Frame.Rotation*Quaterniond.FromAxisAngle(new(1,0,0),Math.PI));
        double height=ForwardThreadEnvelope(source,frame)+recipe.Thread!.PitchMm;
        using var cylinder=ShapeFactory.CreateCylinder(recipe.Diameter/2,height);
        using var transform=OcctGeometryBridge.ToNative(frame);using var located=cylinder.Transformed(transform);
        using var tool=OcctGeometryBridge.StoreShape(located,assets);
        using var overlap=FeatureModeling.Boolean(FeatureBooleanOperation.Common,[source],[located],new(){NonDestructive=true,RunParallel=false});
        using var cut=EvaluateBoolean(new(BooleanOperation.Cut,[recipe.Source,tool.Geometry]),assets,token);
        double expected=Math.Abs(overlap.RequireShape().InspectProperties(InspectionPropertyKind.Volume).Mass);
        if(cut.Geometry.Kind!=BodyKind.Solid||expected<=1e-9||
            Math.Abs(recipe.Source.VolumeMm3-cut.Geometry.VolumeMm3-expected)>Math.Max(1e-6,expected*1e-7))
            throw new CadValidationException("Modeled thread pilot removal differs from its finite forward cutter.");
        return new(cut.Geometry,GeometryAssetLease.Acquire(cut.Geometry,assets),cut.Diagnostics){TopologyHistory=cut.TopologyHistory};
    }

    private static double ForwardThreadEnvelope(Shape source,RigidTransform3d frame)
    {
        // Enclosure only: never a manufacturing depth or a topology locator.
        // Bounding a rotated global box overestimates a thin plate's axial envelope
        // by its lateral size. Enclose the actual source in drilling coordinates,
        // so a rigidly rotated part does not acquire dozens of unnecessary turns.
        using var transform=OcctGeometryBridge.ToNative(frame.Inverse());
        using var local=source.Transformed(transform);
        return Math.Max(0,local.GetBoundingBox().Maximum.Z);
    }

    private GeometryResult EvaluateModeledThreadCut(HoleRecipe recipe,GeometryAssetRef pilotGeometry,IAssetStore assets,CancellationToken token)
    {
        var thread=recipe.Thread!;recipe.Validate();
        if(thread.Profile!=NativeMetricThreadTool.Profile)throw new CadValidationException("Unsupported modeled thread profile.");
        using var source=OcctGeometryBridge.ReadShape(recipe.Source,assets);
        var point=recipe.Frame.Apply(new(recipe.Position.X,recipe.Position.Y,0));
        var rotation=recipe.Frame.Rotation*Quaterniond.FromAxisAngle(new(1,0,0),Math.PI);
        var frame=new RigidTransform3d(point,rotation);
        double length=thread.DepthMm;
        if(thread.Through)
        {
            // Bounds only enclose the forward cutter. They are not measured hole depth or topology identity.
            length=ForwardThreadEnvelope(source,frame);
        }
        token.ThrowIfCancellationRequested();
        if(!double.IsFinite(length)||length<thread.PitchMm||length/thread.PitchMm>NativeMetricThreadTool.MaximumTurns)
            throw new CadValidationException("Modeled thread axial envelope must span one to 64 pitches. Use symbolic intent for a longer or shorter hole.");
        Shape nativeTool;
        try{ThreadNativeStarted?.Invoke();nativeTool=NativeMetricThreadTool.Build(thread.NominalMm,recipe.Diameter,thread.PitchMm,thread.Hand==ThreadHand.Right?1:-1,length,out _,token);}
        catch(InvalidOperationException error){throw new CadValidationException(error.Message);}
        using(nativeTool)
        using(var transform=OcctGeometryBridge.ToNative(frame))
        using(var located=nativeTool.Transformed(transform))
        using(var tool=OcctGeometryBridge.StoreShape(located,assets))
        using(var pilot=OcctGeometryBridge.ReadShape(pilotGeometry,assets))
        using(var overlap=FeatureModeling.Boolean(FeatureBooleanOperation.Common,[pilot],[located],new(){NonDestructive=true,RunParallel=false}))
        using(var threaded=EvaluateBoolean(new(BooleanOperation.Cut,[pilotGeometry,tool.Geometry]),assets,token))
        {
            // Integrate all witnesses in the same drilling frame. Large translated
            // world coordinates create nearly cancelling surface contributions and
            // spuriously exhaust the adaptive integrator on tiny trimmed intervals.
            // The rigid transform preserves volume; display/import caches stay unchanged.
            using var result=OcctGeometryBridge.ReadShape(threaded.Geometry,assets);
            using var measurementFrame=OcctGeometryBridge.ToNative(frame.Inverse());
            using var localOverlap=overlap.RequireShape().Transformed(measurementFrame);
            using var localPilot=pilot.Transformed(measurementFrame);using var localResult=result.Transformed(measurementFrame);
            double expected=Math.Abs(localOverlap.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-11).Properties.Mass);
            double before=Math.Abs(localPilot.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-11).Properties.Mass);
            double after=Math.Abs(localResult.InspectAdaptiveProperties(InspectionPropertyKind.Volume,1e-11).Properties.Mass);
            double removed=before-after;
            // Cut and Common independently trim approximated 3D sweep surfaces.
            // Stricter quadrature does not remove that representation difference.
            // Use the independently exercised profile consumer's explicit volume
            // comparison policy, not a claim that the native solid is exact to 1e-6 mm³.
            double comparisonTolerance=Math.Max(1e-3,expected*1e-5);
            if(threaded.Geometry.Kind!=BodyKind.Solid||expected<=1e-9||removed<=1e-9||
                Math.Abs(removed-expected)>comparisonTolerance)
                throw new CadValidationException($"Modeled thread removal {removed:R} mm³ differs from the independent exact cutter intersection {expected:R} mm³.");
            if(result.GetTopologySummary().UniqueCounts.SolidCount!=1||!result.GetSelfInterferenceReport().IsClear)
                throw new CadValidationException("The modeled thread target failed solid or self-interference verification.");
            return new(threaded.Geometry with{VolumeMm3=after},GeometryAssetLease.Acquire(threaded.Geometry,assets),threaded.Diagnostics){TopologyHistory=threaded.TopologyHistory};
        }
    }

    public Task ConfirmModeledThreadAsync(GeometryRecipe recipe,GeometryAssetRef result,IAssetStore assets,CancellationToken token=default)
        =>Run(()=>
        {
            if(recipe is not HoleRecipe {Thread.Representation:ThreadHoleRepresentation.Modeled} and
                not HolePatternRecipe {Template.Thread.Representation:ThreadHoleRepresentation.Modeled})
                throw new CadValidationException("Modeled thread verification requires a direct thread hole or its hole replay.");
            using var actual=recipe is HoleRecipe hole?EvaluateHole(hole,assets,token):EvaluateHolePattern((HolePatternRecipe)recipe,assets,token);
            using var generated=OcctGeometryBridge.ReadShape(actual.Geometry,assets);
            using var stored=OcctGeometryBridge.ReadShape(result,assets);
            if(!BrepDirectionRoundtrip.Matches(CanonicalBrep(generated),CanonicalBrep(stored)))
                throw new CadValidationException("Stored modeled thread BRep differs from complete native author replay. No sampled or nearest-topology match is accepted.");
            token.ThrowIfCancellationRequested();return true;
        },token);
}
