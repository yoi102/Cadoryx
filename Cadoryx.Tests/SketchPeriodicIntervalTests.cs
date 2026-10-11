using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchPeriodicIntervalTests
{
    internal static SketchFreeformGeometry Basis(int kind)=>kind switch
    {
        0=>SketchFreeformGeometryTests.Geometry(3),1=>SketchFreeformGeometryTests.Geometry(4),
        _=>new(SketchFreeformKind.BSpline,[new(8,0),new(10,4),new(12,7),new(16,0),new(12,-4),new(10,-2)],
            [1,.7,1.1,.8,1.2,1],[5,5.7,7],[3,3,3],3,true,5,7)
    };
    internal static SketchFreeformGeometry Interval(int kind,int range,bool reversed)
    {
        var g=Basis(kind);double first=g.Knots[0],period=g.Knots[^1]-first;
        var(a,b)=range switch{0=>(.875,1.125),1=>(1.0625,1.3125),2=>(-.1875,.0625),_=>(.3,1.3)};
        return g with{FirstParameter=first+a*period,LastParameter=first+b*period,Reversed=reversed};
    }
    [Theory]
    [InlineData(0,0,false)][InlineData(0,0,true)][InlineData(0,1,false)][InlineData(0,1,true)]
    [InlineData(0,2,false)][InlineData(0,2,true)][InlineData(0,3,false)][InlineData(0,3,true)]
    [InlineData(1,0,false)][InlineData(1,0,true)][InlineData(1,1,false)][InlineData(1,1,true)]
    [InlineData(1,2,false)][InlineData(1,2,true)][InlineData(1,3,false)][InlineData(1,3,true)]
    [InlineData(2,0,false)][InlineData(2,0,true)][InlineData(2,1,false)][InlineData(2,1,true)]
    [InlineData(2,2,false)][InlineData(2,2,true)][InlineData(2,3,false)][InlineData(2,3,true)]
    public void CopiedPeriodicBasisAndFiniteNativeRepresentationAgreeAtAndAcrossTheSeam(int kind,int range,bool reversed)
    {
        var g=Interval(kind,range,reversed);g.Validate();var basis=Basis(kind);double origin=basis.Knots[0],period=basis.Knots[^1]-origin;
        using var native=FreeformAuthoring.CreateCurve(FreeformCurveDefinition.BSpline(basis.Poles.Select(p=>new GpPoint(p.X,p.Y,0)).ToArray(),
            basis.Knots,basis.Multiplicities,basis.Degree,true,basis.Weights));
        var exact=new ExactSketchCurve(g.At(0),g.At(1),Freeform:g);var kernel=new OcctGeometryKernel();
        foreach(double t in new[]{0d,.1,.25,.5,.75,.9,1})
        {
            double u=g.FirstParameter+(g.LastParameter-g.FirstParameter)*(reversed?1-t:t),offset=(u-origin)%period;if(offset<0)offset+=period;
            var expected=native.EvaluateEdgeDerivatives(origin+offset);double scale=(g.LastParameter-g.FirstParameter)*(reversed?-1:1);
            var managed=g.Evaluate(t);var value=kernel.Evaluate(exact,t);
            SketchFreeformGeometryTests.Near(new(expected.Point.X,expected.Point.Y),managed.Point);
            SketchFreeformGeometryTests.Near(new(expected.Point.X,expected.Point.Y),value.Point);
            SketchFreeformGeometryTests.Near(new(expected.FirstDerivative.X*scale,expected.FirstDerivative.Y*scale),managed.Derivative);
            SketchFreeformGeometryTests.Near(managed.Derivative,value.NormalizedDerivative);
        }
        Assert.True(g.Periodic);Assert.True(g.Poles.SequenceEqual(basis.Poles));Assert.True(g.Knots.SequenceEqual(basis.Knots));Assert.True(g.Weights.SequenceEqual(basis.Weights));
    }
    [Theory][InlineData(0,false)][InlineData(0,true)][InlineData(1,false)][InlineData(1,true)][InlineData(2,false)][InlineData(2,true)]
    public async Task PeriodicFiniteBoundariesKeepTheOriginalBasisThroughSolidUndoAndStorage(int range,bool reversed)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var g=Interval(0,range,reversed);var d=new SketchDraft(CadSketch.Create(part,"Periodic finite boundary",RigidTransform3d.Identity));d.AddFreeform(g);
            var id=d.Value.Freeforms.Single().Id;d.ExposeCurveEndpoints(id);SketchExactEndpointTests.Close(d,id,false);
            await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var s=session.Snapshot.Sketches[d.Value.Id];var ends=SketchFixedCurveEndpoints.All(s).Single().Ends;
            var source=SketchProfileReference.CreateMixed(s,[id,s.Lines.Single().Id]);await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,s.Plane)),"Periodic solid",part,sketchSource:source));
            var accepted=session.Snapshot;var feature=accepted.Features.Values.Single();Assert.True(feature.Result.VolumeMm3>0);Assert.True(g.SameValues(s.Freeforms.Single().Geometry));
            var storage=new CadDocumentStorage();string path=files.PathFor("periodic-finite.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                Assert.True(g.SameValues(loaded.Snapshot.Sketches[s.Id].Freeforms.Single().Geometry));Assert.Equal(ends,SketchFixedCurveEndpoints.All(loaded.Snapshot.Sketches[s.Id]).Single().Ends);
                Assert.True(g.SameValues(Assert.IsType<ExtrudeRecipe>(loaded.Snapshot.Features[feature.Id].Recipe).Profile.BoundaryCurves[0].Freeform!));loaded.Snapshot.Validate();
            }
            await session.UndoAsync();await session.RedoAsync();Assert.Same(accepted,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(-1,false)][InlineData(-1,true)][InlineData(0,false)][InlineData(0,true)][InlineData(1,false)][InlineData(1,true)]
    public void C0SeamEndpointsAgreeWithTheNativeFiniteCurveInBothDirections(int shift,bool reversed)
    {
        var basis=Basis(2);double period=basis.Knots[^1]-basis.Knots[0];
        var g=basis with{FirstParameter=basis.FirstParameter+shift*period,LastParameter=basis.LastParameter+shift*period,Reversed=reversed};var kernel=new OcctGeometryKernel();
        var curve=new ExactSketchCurve(g.At(0),g.At(1),Freeform:g);
        foreach(double t in new[]{0d,1d})
        {
            var managed=g.Evaluate(t);var native=kernel.Evaluate(curve,t);
            SketchFreeformGeometryTests.Near(managed.Point,native.Point);
            SketchFreeformGeometryTests.Near(managed.Derivative,native.NormalizedDerivative);
        }
    }
    [Theory][InlineData(-1,false)][InlineData(-1,true)][InlineData(0,false)][InlineData(0,true)][InlineData(1,false)][InlineData(1,true)]
    public async Task C0FiniteSolidCanEndAtThePeriodicSeamWithoutChangingTheAuthorBasis(int shift,bool reversed)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var basis=Basis(2);double period=basis.Knots[^1]-basis.Knots[0];
            var geometry=basis with{FirstParameter=basis.FirstParameter+(shift+.25)*period,LastParameter=basis.LastParameter+shift*period,Reversed=reversed};
            var draft=new SketchDraft(CadSketch.Create(part,"C0 finite seam",RigidTransform3d.Identity));draft.AddFreeform(geometry);
            var id=draft.Value.Freeforms.Single().Id;draft.ExposeCurveEndpoints(id);SketchExactEndpointTests.Close(draft,id,false);
            await session.ExecuteAsync(new UpsertSketchCommand(draft.Value,new ManagedSketchConstraintSolver()));var sketch=session.Snapshot.Sketches[draft.Value.Id];
            var source=SketchProfileReference.CreateMixed(sketch,[id,sketch.Lines.Single().Id]);
            await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,sketch.Plane)),"C0 seam solid",part,sketchSource:source));
            var accepted=session.Snapshot;Assert.True(accepted.Features.Values.Single().Result.VolumeMm3>0);Assert.True(geometry.SameValues(accepted.Sketches[sketch.Id].Freeforms.Single().Geometry));
            var storage=new CadDocumentStorage();string path=files.PathFor("c0-periodic.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets)){Assert.True(geometry.SameValues(loaded.Snapshot.Sketches[sketch.Id].Freeforms.Single().Geometry));loaded.Snapshot.Validate();}
            await session.UndoAsync();await session.RedoAsync();Assert.Same(accepted,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task IntervalParametersCanCrossTheSeamWhileEndpointsAndConsumersFollowTheirExplicitIds()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var solver=new ManagedSketchConstraintSolver();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var d=new SketchDraft(CadSketch.Create(part,"Periodic driven interval",RigidTransform3d.Identity));var initialCurve=Basis(0) with{FirstParameter=.5,LastParameter=1.5};d.AddFreeform(initialCurve);
            var id=d.Value.Freeforms.Single().Id;d.ExposeCurveEndpoints(id);SketchExactEndpointTests.Close(d,id,false);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var s=session.Snapshot.Sketches[d.Value.Id];
            var source=SketchProfileReference.CreateMixed(s,[id,s.Lines.Single().Id]);await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,s.Plane)),"Driven periodic solid",part,sketchSource:source));
            var ends=SketchFixedCurveEndpoints.All(s).Single().Ends;var first=SketchCurveParameterTests.Number("first",3.5,ParameterDimension.Scalar);var last=SketchCurveParameterTests.Number("last",4.5,ParameterDimension.Scalar);
            var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(new([first,last],[SketchCurveParameterTests.Drive(first,ParameterTarget.SketchCurve(s.Id,id,"FirstParameter")),SketchCurveParameterTests.Drive(last,ParameterTarget.SketchCurve(s.Id,id,"LastParameter"))])));
            var after=session.Snapshot;var updated=after.Sketches[s.Id];Assert.Equal(ends,SketchFixedCurveEndpoints.All(updated).Single().Ends);
            Assert.True(updated.Freeforms.Single().Geometry.SameValues(initialCurve with{FirstParameter=3.5,LastParameter=4.5}));Assert.False(after.Features.Values.Single().IsStale);after.Validate();
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public void MultiplePeriodsExcessiveShiftsAndNonperiodicExtrapolationStayRejected()
    {
        var g=Basis(0);Assert.Throws<CadValidationException>(()=>(g with{FirstParameter=0,LastParameter=4.01}).Validate());
        Assert.Throws<CadValidationException>(()=>(g with{FirstParameter=4100,LastParameter=4101}).Validate());
        var nonperiodic=SketchExactEndpointTests.Geometry(2);Assert.Throws<CadValidationException>(()=>(nonperiodic with{FirstParameter=-1}).Validate());
    }
    [Theory][InlineData("sketches")][InlineData("features")]
    public async Task LegacySchemasCannotDisguiseAnOutsidePeriodicInterval(string section)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var d=new SketchDraft(CadSketch.Create(part,"Versioned periodic interval",RigidTransform3d.Identity));d.AddFreeform(Interval(0,0,false));var id=d.Value.Freeforms.Single().Id;
            d.ExposeCurveEndpoints(id);SketchExactEndpointTests.Close(d,id,false);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));var s=session.Snapshot.Sketches[d.Value.Id];var source=SketchProfileReference.CreateMixed(s,[id,s.Lines.Single().Id]);
            await session.ExecuteAsync(new AddBodyCommand(source.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,s.Plane)),"Periodic consumer",part,sketchSource:source));
            var storage=new CadDocumentStorage();string path=files.PathFor("legacy-periodic.cadoryx");await session.SaveAsync(storage,path);
            FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind==section?s with{SchemaVersion=section=="sketches"?15:34}:s).ToImmutableArray()});
            var error=await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Contains("legacy curve schema",error.ToString());
        }
        Assert.Equal(0,assets.Count);
    }
}
