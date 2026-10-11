using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed class SketchEllipseProjectionTests
{
    private static ExactSketchCurve Curve(SketchEllipseGeometry ellipse)=>new(ellipse.At(0),ellipse.At(1),Ellipse:ellipse);
    private static void Near(Point2d expected,Point2d actual,double tolerance=1e-7)=>Assert.InRange(double.Hypot(expected.X-actual.X,expected.Y-actual.Y),0,tolerance);
    [Theory] [InlineData(false,Math.PI)] [InlineData(true,Math.PI)] [InlineData(false,-Math.PI)] [InlineData(true,-Math.PI)]
    public void AffineEllipsePreservesPhaseReflectionAndBothParameterDirections(bool reflected,double sweep)
    {
        var ellipse=new SketchEllipseGeometry(new(3,-2),new(5,2),new(reflected?1:-1,reflected?-3:3),.7,sweep);ellipse.Validate();
        var kernel=new OcctGeometryKernel();var curve=Curve(ellipse);
        foreach(double t in new[]{0d,.1,.37,.6,.9,1})
        {
            var actual=kernel.Evaluate(curve,t);Near(ellipse.At(t),actual.Point);Near(ellipse.Derivative(t),actual.NormalizedDerivative);
            var nearest=kernel.Project(curve,ellipse.At(t)).MinBy(p=>p.Distance)!;Assert.InRange(nearest.Distance,0,1e-7);Near(ellipse.At(t),nearest.Point);
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public void FullEllipsesHaveExactConicAreaAndFiniteCrossings(bool reflected)
    {
        var ellipse=new SketchEllipseGeometry(new(0,0),new(5,0),new(0,reflected?-3:3),.3,Math.Tau);var kernel=new OcctGeometryKernel();var curve=Curve(ellipse);
        var area=kernel.InspectRegion([curve]);Assert.Equal(1,area.EdgeCount);Assert.InRange(Math.Abs(area.Area-15*Math.PI),0,1e-6);
        var hits=kernel.Intersect(curve,new(new(-8,0),new(8,0)));Assert.Equal(2,hits.Length);
        Assert.All(hits,h=>Assert.InRange(Math.Abs(Math.Abs(h.Point.X)-5),0,1e-7));
    }
    [Theory] [InlineData(0,3)] [InlineData(3,0)] [InlineData(3,1e-10)]
    public void DegenerateProjectedConicsReject(double first,double second)
    {
        var ellipse=new SketchEllipseGeometry(new(0,0),new(first,0),new(0,second),0,Math.Tau);
        Assert.Throws<CadValidationException>(ellipse.Validate);Assert.Throws<CadValidationException>(()=>Curve(ellipse).Validate());
    }
    internal static ExactTopologySelection Pick(DocumentSnapshot document,IAssetStore assets,CurveGeometryType type)
    {
        var feature=document.Features.Values.Single();using var shape=OcctGeometryBridge.ReadShape(feature.Result,assets);using var map=RepairSnapshot.Create(shape);
        var items=map.Topology.Where(t=>t.Kind==ShapeKind.Edge).Where(t=>
        {
            using var edge=map.CopySubshape(t.Selection);var curve=edge.GetEdgeCurveSnapshot();
            return curve.CurveType==type&&(type!=CurveGeometryType.Circle||Math.Abs(curve.StartPoint.Z)<1e-8);
        }).ToArray();Assert.NotEmpty(items);
        // The fixture explicitly chooses this copied native selection. Later operations must retain its fingerprint.
        var chosen=items[0];return new(document.Id,feature.Id,feature.Result.Revision,feature.Result.AssetId,map.Fingerprint,
            chosen.Selection.Index,HistoryShapeKind.Edge,OcctGeometryKernel.HistoryAdapterVersion);
    }
    [Theory] [InlineData(0)] [InlineData(.4)] [InlineData(1.2)]
    public async Task NativeCircleObliqueProjectionMatchesExactSourceAndCanBeReconfirmed(double angle)
    {
        var (initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(5,10,RigidTransform3d.Identity),"Circle source",part));
        var selection=Pick(session.Snapshot,assets,CurveGeometryType.Circle);
        var plane=new RigidTransform3d(new(1,2,3),Quaterniond.FromAxisAngle(new(0,1,0),angle));
        var projected=await kernel.ProjectEdgeAsync(session.Snapshot,part,selection,plane,assets);Assert.NotNull(projected.Curve.Ellipse);
        await kernel.ConfirmEdgeProjectionAsync(projected,assets);
        using var shape=OcctGeometryBridge.ReadShape(projected.Source,assets);using var map=RepairSnapshot.Create(shape);using var edge=map.CopySubshape(map.Select(selection.FullTopologyIndex));
        var info=edge.GetEdgeCurveSnapshot();var inverse=plane.Inverse();
        foreach(double t in new[]{0d,.1,.37,.8,1})
        {
            var source=edge.EvaluateEdge(info.FirstParameter+(info.LastParameter-info.FirstParameter)*t).Point;
            var value=inverse.Apply(new(source.X,source.Y,source.Z));Near(new(value.X,value.Y),kernel.Evaluate(projected.Curve,t).Point);
        }
        Assert.InRange(Math.Abs(projected.Curve.Ellipse!.Value.Principal().MinorRadius-5*Math.Cos(angle)),0,1e-7);
        var forged=projected with{Curve=Curve(projected.Curve.Ellipse.Value with{Center=new(100,100)})};
        await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ConfirmEdgeProjectionAsync(forged,assets));
    }
    [Fact] public async Task ProjectionRejectsStaleForeignFingerprintFaceAndCollapsedCircle()
    {
        var (initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new CylinderRecipe(5,10,RigidTransform3d.Identity),"Source",part));
        var selection=Pick(session.Snapshot,assets,CurveGeometryType.Circle);
        foreach(var invalid in new[]{selection with{DocumentId=DocumentId.New()},selection with{Revision=GeometryRevisionId.New()},selection with{Kind=HistoryShapeKind.Face},
            selection with{Fingerprint=new string('0',64)},selection with{AdapterVersion="unsupported"}})
            await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ProjectEdgeAsync(session.Snapshot,part,invalid,RigidTransform3d.Identity,assets));
        await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ProjectEdgeAsync(session.Snapshot,DefinitionId.New(),selection,RigidTransform3d.Identity,assets));
        var plane=new RigidTransform3d(Vector3d.Zero,Quaterniond.FromAxisAngle(new(0,1,0),Math.PI/2));
        await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ProjectEdgeAsync(session.Snapshot,part,selection,plane,assets));
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>kernel.ProjectEdgeAsync(session.Snapshot,part,selection,RigidTransform3d.Identity,assets,canceled.Token));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task NativeEllipseFullAndFiniteEdgesRetainSourceDomainAndDerivatives(bool finite)
    {
        var (initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var conic=SketchCurve2d.EllipticArc(new(8,3),5,2,.7,finite?2.2:Math.Tau,.4);
        var end=conic.Evaluate(1).Point;var start=conic.Evaluate(0).Point;
        var chain=SketchCurveChain2d.Create(finite?[conic,SketchCurve2d.Segment(end,start)]:[conic],true);
        using var shape=SketchProfile2d.Create(chain).Extrude(SketchPlane.XY,10);using var stored=OcctGeometryBridge.StoreShape(shape,assets);
        await session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(stored.Geometry),"Ellipse source",part));
        var selection=Pick(session.Snapshot,assets,CurveGeometryType.Ellipse);
        var plane=new RigidTransform3d(new(1,-3,5),Quaterniond.FromAxisAngle(new Vector3d(1,2,3).Normalized(),.5));
        var projected=await kernel.ProjectEdgeAsync(session.Snapshot,part,selection,plane,assets);await kernel.ConfirmEdgeProjectionAsync(projected,assets);
        using var original=OcctGeometryBridge.ReadShape(projected.Source,assets);using var map=RepairSnapshot.Create(original);using var edge=map.CopySubshape(map.Select(selection.FullTopologyIndex));
        var info=edge.GetEdgeCurveSnapshot();var inverse=plane.Inverse();double domain=info.LastParameter-info.FirstParameter;
        Assert.InRange(Math.Abs(domain-(finite?2.2:Math.Tau)),0,1e-8);
        foreach(double t in new[]{0d,.07,.37,.83,1})
        {
            var source=edge.EvaluateEdgeDerivatives(t==1?info.LastParameter:t==0?info.FirstParameter:info.FirstParameter+domain*t);var point=inverse.Apply(new(source.Point.X,source.Point.Y,source.Point.Z));
            var derivative=inverse.Rotation.Rotate(new(source.FirstDerivative.X,source.FirstDerivative.Y,source.FirstDerivative.Z))*domain;
            var result=kernel.Evaluate(projected.Curve,t);Near(new(point.X,point.Y),result.Point);Near(new(derivative.X,derivative.Y),result.NormalizedDerivative);
        }
        var changed=projected with{Curve=Curve(projected.Curve.Ellipse!.Value with{StartAngle=projected.Curve.Ellipse.Value.StartAngle+.1})};
        await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ConfirmEdgeProjectionAsync(changed,assets));
    }
    [Fact] public async Task NativeLineProjectionIsIndependentCopiedValue()
    {
        var (initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Line source",part));
        var selection=Pick(session.Snapshot,assets,CurveGeometryType.Line);
        // Select a plane on which none of the box's three edge directions collapse.
        var rotation=Quaterniond.FromAxisAngle(new Vector3d(1,1,1).Normalized(),.5);var plane=new RigidTransform3d(new(1,2,3),rotation);
        var projected=await kernel.ProjectEdgeAsync(session.Snapshot,part,selection,plane,assets);Assert.Null(projected.Curve.Ellipse);
        await kernel.ConfirmEdgeProjectionAsync(projected,assets);var curve=projected.Curve;await session.DisposeAsync();
        curve.Validate();Near(curve.Start,kernel.Evaluate(curve,0).Point);Near(curve.End,kernel.Evaluate(curve,1).Point);Assert.Equal(0,assets.Count);
    }
}
