using System.Collections.Immutable;
using System.Globalization;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;
using Xunit.Abstractions;

namespace Cadoryx.Tests;

public sealed class SketchRegionNaturalParameterTests(ITestOutputHelper output)
{
    private static readonly OcctGeometryKernel Kernel=new();
    public static IEnumerable<object[]> Cases()
    {
        foreach(string kind in new[]{"capsule","bezier","spline"})
        foreach(bool curvature in kind=="capsule"?new[]{false}:new[]{false,true})
        foreach(bool mirror in new[]{false,true})
        foreach(double distance in new[]{.1,-.1})
        foreach(bool reverse in new[]{false,true})
            yield return [kind,curvature,mirror,distance,reverse];
    }

    [Theory,MemberData(nameof(Cases))]
    public async Task AllSourceCoordinateAndRegionDistanceDriversStageAtomicallyAndRetainNativeSolidHistoryAndBothStorageFormats(
        string kind,bool curvature,bool mirror,double distance,bool reverse)
    {
        using var files=new TestFiles();
        var(doc,part)=SketchCommandTests.Document();
        var source=(kind=="capsule"?SketchRegionNaturalChartTests.Capsule(mirror,distance):
            SketchRegionNaturalFreeformChartTests.Ring(kind=="spline",mirror,curvature,distance)) with{PartId=part};
        var sourcePoints=source.Lines.SelectMany(l=>new[]{l.Start,l.End})
            .Concat(source.Arcs.SelectMany(a=>new[]{a.Start,a.Middle,a.End}))
            .Concat(source.Beziers.SelectMany(b=>new[]{b.Start,b.Control,b.End}))
            .Concat(source.Splines.SelectMany(s=>s.Controls)).ToHashSet();
        source=source with{Constraints=source.Constraints.AddRange(source.Points.Where(p=>sourcePoints.Contains(p.Id))
            .Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)))};
        var assets=new MemoryAssetStore();
        var solver=new ManagedSketchConstraintSolver(Kernel);
        await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new UpsertSketchCommand(source,solver));
        var initial=session.Snapshot.Sketches[source.Id];
        var group=Assert.Single(initial.RegionOffsets);
        var root=Assert.Single(SketchRegionAuthorModel.Roots(initial,group.Id));
        var profile=new SketchProfileReference(initial.Id,initial.Revision,[]){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
        await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,initial.Plane)),
            "Natural C1 material",part,sketchSource:profile));
        var feature=Assert.Single(session.Snapshot.Features.Values);
        var before=session.Snapshot;
        const double factor=1.12;
        var scale=new CadParameter(Guid.NewGuid(),"scale",ParameterDimension.Scalar,factor.ToString("R",CultureInfo.InvariantCulture));
        var offset=new CadParameter(Guid.NewGuid(),"offset",ParameterDimension.Length,
            distance.ToString("R",CultureInfo.InvariantCulture)+" mm * scale");
        var parameters=new List<CadParameter>{scale,offset};
        var bindings=new List<ParameterBinding>{new(Guid.NewGuid(),offset.Id,ParameterTarget.SketchRegion(initial.Id,group.Id))};
        int index=0;
        foreach(var fixedPoint in initial.Constraints.OfType<FixPointConstraint>().Where(c=>sourcePoints.Contains(c.Point)))
        foreach(string axis in new[]{"X","Y"})
        {
            double coordinate=axis=="X"?fixedPoint.Position.X:fixedPoint.Position.Y;
            string shift=axis=="X"?" + 2 mm":" - 1 mm";
            var parameter=new CadParameter(Guid.NewGuid(),"source"+index++,ParameterDimension.Length,
                "("+coordinate.ToString("R",CultureInfo.InvariantCulture)+" mm) * scale"+shift);
            parameters.Add(parameter);bindings.Add(new(Guid.NewGuid(),parameter.Id,ParameterTarget.Sketch(initial.Id,fixedPoint.Id,axis)));
        }
        var book=new ParameterBook(parameters.ToImmutableArray(),(reverse?bindings.AsEnumerable().Reverse():bindings).ToImmutableArray());
        await session.ExecuteAsync(new SetParametersCommand(book,solver));
        var accepted=session.Snapshot;
        var solved=accepted.Sketches[initial.Id];
        ParameterGraph.ValidateApplied(accepted);
        Assert.Equal(distance*factor,Assert.Single(solved.RegionOffsets).Distance);
        foreach(var point in initial.Points.Where(p=>sourcePoints.Contains(p.Id)))
        {
            var actual=solved.Points.Single(p=>p.Id==point.Id).Position;
            Assert.InRange(double.Hypot(actual.X-(2+factor*point.Position.X),actual.Y-(-1+factor*point.Position.Y)),0,1e-7);
        }
        Assert.Equal(initial.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),solved.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
        Assert.True(SketchRegionJointAuthors.SameStructure(group.Loops[0].Joints,solved.RegionOffsets[0].Loops[0].Joints));
        Assert.All(solved.RegionOffsets[0].Loops[0].Joints,j=>Assert.Equal(SketchRegionJointKind.SourceEndpoints,j.Kind));
        SketchAssociativeEndpoints.RequireSolved(solved);
        if(curvature)SketchEndpointCurvatureTests.RequireG2(solved);
        Kernel.ConfirmExpressionRegions(solved);
        double radius=2*factor;
        double area=kind=="capsule"?16*factor*radius+Math.PI*radius*radius:10*radius*radius/3;
        double perimeter=kind=="capsule"?16*factor+Math.Tau*radius:4*radius*(1+Math.Asinh(1)/Math.Sqrt(2));
        double d=distance*factor,expected=3*(area+d*perimeter+Math.PI*d*d);
        Assert.InRange(Math.Abs(accepted.Features[feature.Id].Result.VolumeMm3-expected),0,1e-7);
        await session.UndoAsync();Assert.Same(before,session.Snapshot);
        await session.RedoAsync();Assert.Same(accepted,session.Snapshot);

        var storage=new CadDocumentStorage(regionVerifier:Kernel);
        foreach(bool json in new[]{false,true})
        {
            string path=files.PathFor(json?"natural-json.cadoryx":"natural-msgpack.cadoryx");
            await session.SaveAsync(storage,path);
            if(json)SketchExpressionSectionTests.Rewrite(path,"sketch-regions",_=>SketchRegionSectionStorage.EncodeJson([solved]),"json",2);
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                var actual=loaded.Snapshot.Sketches[initial.Id];
                Assert.True(SketchRegionAuthorModel.SameTable(solved,actual));
                Assert.True(SketchExpressionEntities.SameTable(solved,actual));
                output.WriteLine($"storageJson={json}; expectedPoints={solved.Points.Length}; actualPoints={actual.Points.Length}; mismatches="+
                    string.Join(" | ",solved.Points.Zip(actual.Points,(a,b)=>(a,b)).Where(p=>p.a!=p.b).Take(4)
                        .Select(p=>$"expected={p.a};actual={p.b}")));
                // Compare exact ordered elements rather than ImmutableArray's
                // backing-array identity. Parameter storage canonically orders
                // its ID-keyed tables; every complete author value must match.
                Assert.Equal(solved.Points.AsEnumerable(),actual.Points.AsEnumerable());
                Assert.Equal(solved.Constraints.AsEnumerable(),actual.Constraints.AsEnumerable());
                Assert.Equal(book.Parameters.OrderBy(p=>p.Id),loaded.Snapshot.Parameters.Parameters);
                Assert.Equal(book.Bindings.OrderBy(b=>b.Id),loaded.Snapshot.Parameters.Bindings);
                ParameterGraph.ValidateApplied(loaded.Snapshot);
                Kernel.ConfirmExpressionRegions(actual);
                Assert.InRange(Math.Abs(loaded.Snapshot.Features[feature.Id].Result.VolumeMm3-expected),0,1e-7);
                var manifest=FormatEvolutionTests.Manifest(path);
                Assert.Equal("0.7.8",manifest.ApplicationVersion);
                Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);
                Assert.Equal(18,manifest.Sections.Length);
            }
        }
        int assetCount=assets.Count;
        var cyclic=book with{Parameters=book.Parameters.Replace(scale,scale with{Expression="scale"})};
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(cyclic,solver)));
        Assert.Same(accepted,session.Snapshot);Assert.Equal(assetCount,assets.Count);
        var collapse=book with{Parameters=book.Parameters.Replace(offset,offset with{Expression="-100 mm"})};
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(collapse,solver)));
        Assert.Same(accepted,session.Snapshot);Assert.Equal(assetCount,assets.Count);
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>session.ExecuteAsync(new SetParametersCommand(book,solver),cancellation.Token));
        Assert.Same(accepted,session.Snapshot);Assert.Equal(assetCount,assets.Count);
    }
}
