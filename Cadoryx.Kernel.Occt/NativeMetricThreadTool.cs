using OcctSharp;

namespace Cadoryx.Kernel.Occt;

/// <summary>Native-local, bounded 60-degree internal thread groove. No mesh or sampled spine authoring.</summary>
internal static class NativeMetricThreadTool
{
    public const string Profile="metric-basic-60-internal-v1";
    public const double Tolerance=1e-6;
    public const int MaximumTurns=64;
    public readonly record struct Proof(double MajorRadius,double PilotRadius,double Pitch,double Length,
        double RootFlatWidth,double FlankAngle,double SweepError,double MaximumTopologyTolerance);

    /// <summary>The caller owns the returned local-Z cutter, clipped exactly to [0,length].
    /// The common workshop pilot can enlarge the basic minor diameter; no tolerance-class compliance is implied.</summary>
    public static Shape Build(double nominalDiameter,double pilotDiameter,double pitch,int hand,double length,
        out Proof proof,CancellationToken token=default)
    {
        proof=default;
        if(!double.IsFinite(nominalDiameter)||!double.IsFinite(pilotDiameter)||!double.IsFinite(pitch)||!double.IsFinite(length)||
            nominalDiameter<=0||pilotDiameter<=0||pilotDiameter>=nominalDiameter||pitch<=0||hand is not (1 or -1)||
            length<pitch||length/pitch>MaximumTurns)
            throw new InvalidOperationException("Modeled internal threads require finite positive dimensions, one to 64 pitches of length and an explicit hand.");
        double major=nominalDiameter/2,pilot=pilotDiameter/2;
        double basicMinor=major-5*Math.Sqrt(3)*pitch/16;
        double inner=Math.Min(basicMinor,pilot)-.02*pitch;
        double halfRoot=pitch/16,halfInner=halfRoot+(major-inner)/Math.Sqrt(3);
        if(inner<=0||halfInner>=.5*pitch||pilot<=inner||pilot>=major)
            throw new InvalidOperationException("The selected pilot does not support the bounded basic thread profile without overlapping turns.");
        token.ThrowIfCancellationRequested();
        double radius=(inner+major)/2,span=length+2*pitch;
        using var support=SurfaceModeling.CreateAnalyticFace(AnalyticSurfaceKind.Cylinder,SketchPlane.XY,
            new(0,Math.Tau,-pitch,length+pitch),radius:radius,tolerance:1e-7);
        var uv=SketchCurve2d.Segment(new(0,-pitch),new(hand*Math.Tau*span/pitch,length+pitch));
        using var edge=SurfaceModeling.LiftCurve(support,uv,build3d:true,tolerance:1e-7);
        using var spine=SurfaceModeling.CreateWire(support,[edge],1e-7);
        using var section=ShapeFactory.CreatePolygonWire([
            new(inner,0,-pitch-halfInner),new(major,0,-pitch-halfRoot),
            new(major,0,-pitch+halfRoot),new(inner,0,-pitch+halfInner)],close:true);
        using var plan=GuidedSweepPlan.Create(spine,[new(section)],new(){Frame=GuidedSweepFrame.Frenet,
            SolidPolicy=SweepSolidPolicy.RequireSolid,Tolerance3d=Tolerance,ToleranceBoundary=Tolerance,ToleranceAngular=Tolerance,
            MaximumSegments=512,MaximumDegree=14});
        token.ThrowIfCancellationRequested();
        using var sweep=plan.Build();
        if(sweep.Diagnostics.ApproximationError is not {} error||!double.IsFinite(error)||error>Tolerance)
            throw new InvalidOperationException("Native thread sweep has no acceptable approximation-error evidence.");
        var swept=sweep.RequireShape();
        if(!swept.IsValid||swept.GetTopologySummary().UniqueCounts.SolidCount!=1)
            throw new InvalidOperationException("Native thread sweep must be one valid solid.");
        token.ThrowIfCancellationRequested();
        using var envelope=ShapeFactory.CreateCylinder(major+.1*pitch,length);
        using var clipped=FeatureModeling.Boolean(FeatureBooleanOperation.Common,[swept],[envelope],new(){NonDestructive=true,RunParallel=false});
        var tool=clipped.RequireShape();var topology=tool.GetTopologySummary();
        double maximum=Math.Max(topology.VertexTolerance.Maximum,Math.Max(topology.EdgeTolerance.Maximum,topology.FaceTolerance.Maximum));
        if(!topology.IsValid||topology.UniqueCounts.SolidCount!=1||!double.IsFinite(maximum)||maximum>1e-5)
            throw new InvalidOperationException("Clipped thread cutter failed solid validity or its 1e-5 mm topology tolerance limit.");
        token.ThrowIfCancellationRequested();
        if(!tool.GetSelfInterferenceReport().IsClear)
            throw new InvalidOperationException("Clipped thread cutter has self-interference or unresolved native analysis faults.");
        token.ThrowIfCancellationRequested();
        proof=new(major,pilot,pitch,length,pitch/8,Math.PI/3,error,maximum);
        // Independent owned topology copy survives every temporary owner above.
        using var snapshot=RepairSnapshot.Create(tool);
        return snapshot.CopyShape();
    }

    /// <summary>Independent screw-coordinate integral outside the drilled pilot, over the full clipped length.</summary>
    public static double RemovedVolume(double nominalDiameter,double pilotDiameter,double pitch,double length)
    {
        double major=nominalDiameter/2,pilot=pilotDiameter/2;
        double integral=(pitch/16+major/Math.Sqrt(3))*(major*major-pilot*pilot)-
            2/(3*Math.Sqrt(3))*(major*major*major-pilot*pilot*pilot);
        return Math.Tau*length/pitch*integral;
    }
}
