using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using OcctSharp;
using MessagePack;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public class GuidedAuthoringTests
{
    private static RigidTransform3d PathFrame=>new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0));
    private static SketchProfile Circle(double r)=>SketchProfile.FromCircle(new(0,0),r);
    private static SketchProfile Square(double half)=>new([new(-half,-half),new(half,-half),new(half,half),new(-half,half)]);
    private static SweepRecipe Straight(SketchProfile? profile=null,GuidedOutput output=GuidedOutput.Solid)=>
        new(new(profile??Circle(1),RigidTransform3d.Identity),new([new(new(0,0),new(0,10))],PathFrame),output);
    private static LoftRecipe Loft(SketchProfile a,SketchProfile b,GuidedOutput output=GuidedOutput.Solid)=>
        new([RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,5)],[new(Guid.NewGuid(),null,[a,b])],output,Ruled:true);
    [Theory][InlineData(GuidedOutput.Solid)][InlineData(GuidedOutput.Sheet)]
    public async Task StraightSweepHasExplicitOutputAndNativeRoundtrip(GuidedOutput output)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var result=await kernel.EvaluateAsync(Straight(output:output),assets))
        {
            Assert.Equal(output==GuidedOutput.Solid?BodyKind.Solid:BodyKind.Sheet,result.Geometry.Kind);
            using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);Assert.True(shape.GetSelfInterferenceReport().IsClear);
            if(output==GuidedOutput.Solid)Assert.Equal(10*Math.PI,result.Geometry.VolumeMm3,5);
            else{Assert.Equal(0,result.Geometry.VolumeMm3);Assert.Equal(20*Math.PI,shape.InspectProperties(InspectionPropertyKind.Area).Mass,5);}
            Assert.Equal(0,result.Geometry.Bounds.Min.Z,5);Assert.Equal(10,result.Geometry.Bounds.Max.Z,5);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task CurvedAndCornerSweepsPreserveAnalyticVolume(bool corner)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var curves=corner?ImmutableArray.Create(new SketchBoundaryCurve(new(0,0),new(0,10)),new(new(0,10),new(10,10))):
            [new SketchBoundaryCurve(new(0,0),new(10,10),new(10-10/Math.Sqrt(2),10/Math.Sqrt(2)))];
        var recipe=Straight() with{Path=new(curves,PathFrame),Corner=corner?SweepCorner.RightCorner:SweepCorner.Transformed};
        using(var result=await kernel.EvaluateAsync(recipe,assets))Assert.InRange(Math.Abs(result.Geometry.VolumeMm3-(corner?20*Math.PI:5*Math.PI*Math.PI)),0,1e-3);
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task FixedProfileHolesAndNestedIslandsAreExact()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var profile=Circle(3) with{Holes=[new(new(0,0),2)],Islands=[new([],new(new(0,0),1))]};
        using(var result=await kernel.EvaluateAsync(Straight(profile),assets))
        {Assert.Equal(BodyKind.Compound,result.Geometry.Kind);Assert.Equal(60*Math.PI,result.Geometry.VolumeMm3,4);}
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(GuidedOutput.Solid)][InlineData(GuidedOutput.Sheet)]
    public async Task RuledLoftChecksVolumeAndOpenSheetArea(GuidedOutput output)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var result=await kernel.EvaluateAsync(Loft(Square(1),Square(2),output),assets))
        {
            Assert.Equal(output==GuidedOutput.Solid?BodyKind.Solid:BodyKind.Sheet,result.Geometry.Kind);
            if(output==GuidedOutput.Solid)Assert.Equal(140d/3,result.Geometry.VolumeMm3,5);
            else{using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);Assert.Equal(12*Math.Sqrt(26),shape.InspectProperties(InspectionPropertyKind.Area).Mass,5);}
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task AuthorSelectedLoftChannelsDoNotDependOnArrayPosition()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var root=Guid.NewGuid();var hole=Guid.NewGuid();var island=Guid.NewGuid();
        var recipe=new LoftRecipe([RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,10)],
            [new(island,hole,[Circle(1),Circle(1)]),new(root,null,[Circle(3),Circle(3)]),new(hole,root,[Circle(2),Circle(2)])],Ruled:true);
        using(var result=await kernel.EvaluateAsync(recipe,assets)){Assert.Equal(BodyKind.Compound,result.Geometry.Kind);Assert.Equal(60*Math.PI,result.Geometry.VolumeMm3,4);}
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task MultipleSectionsAndSquareCircleTransitionAreValid()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var recipe=Loft(Square(1),Square(2));
        recipe=recipe with{Stations=[RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,5),RigidTransform3d.Translate(0,0,10)],
            Channels=[recipe.Channels[0] with{Profiles=[Square(1),Square(2),Square(1)]}]};
        using(var result=await kernel.EvaluateAsync(recipe,assets))Assert.Equal(280d/3,result.Geometry.VolumeMm3,5);
        using(var result=await kernel.EvaluateAsync(Loft(Circle(1),Square(2)) with{Ruled=false},assets))
        {Assert.Equal(BodyKind.Solid,result.Geometry.Kind);Assert.True(result.Geometry.VolumeMm3>0);}
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task PreviewUndoRedoAndInvalidEditAreAtomic()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Guided"),assets,kernel,new InlineSessionDispatcher()))
        {
            var before=session.Snapshot;using(var preview=await kernel.EvaluateAsync(Straight(),assets)){Assert.Same(before,session.Snapshot);Assert.Equal(1,assets.Count);}Assert.Equal(0,assets.Count);
            await session.ExecuteAsync(new AddBodyCommand(Straight(),"Sweep"));var after=session.Snapshot;after.Validate();
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            int count=assets.Count;var bad=Straight() with{Section=new(Circle(1),RigidTransform3d.Translate(0,0,2))};
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddBodyCommand(bad,"Invalid")));Assert.Same(after,session.Snapshot);Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task GapBranchCrossingClosedAndReversedPathsReject()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var paths=new ImmutableArray<SketchBoundaryCurve>[]
        {
            [new(new(0,0),new(0,5)),new(new(1,5),new(1,10))],
            [new(new(0,0),new(0,5)),new(new(0,5),new(0,10)),new(new(0,5),new(5,5))],
            [new(new(0,0),new(0,10)),new(new(0,10),new(5,0)),new(new(5,0),new(-5,5))],
            [new(new(0,0),new(0,10)),new(new(0,10),new(0,0))],
            [new(new(0,0),new(0,5)),new(new(0,10),new(0,5))]
        };
        foreach(var path in paths)
        {await Assert.ThrowsAnyAsync<ArgumentException>(()=>kernel.EvaluateAsync(Straight() with{Path=new(path,PathFrame)},assets));Assert.Equal(0,assets.Count);}
    }
    [Fact] public async Task WrongSectionPoseAndExcessiveArcRadiusReject()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        foreach(var frame in new[]{RigidTransform3d.Translate(1,0,0),new RigidTransform3d(Vector3d.Zero,Quaterniond.FromEulerDegrees(0,90,0))})
        {await Assert.ThrowsAsync<CadValidationException>(()=>kernel.EvaluateAsync(Straight() with{Section=new(Circle(1),frame)},assets));Assert.Equal(0,assets.Count);}
        var arc=Straight(Circle(15)) with{Path=new([new(new(0,0),new(10,10),new(10-10/Math.Sqrt(2),10/Math.Sqrt(2)))],PathFrame)};
        await Assert.ThrowsAnyAsync<Exception>(()=>kernel.EvaluateAsync(arc,assets));Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task LoftWrongOrderNormalsParentsAndHolesReject()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var recipe=Loft(Square(1),Square(2));var root=recipe.Channels[0].Id;
        foreach(var invalid in new[]{recipe with{Stations=[RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,-5)]},
            recipe with{Stations=[RigidTransform3d.Identity,RigidTransform3d.Identity]},
            recipe with{Channels=[recipe.Channels[0] with{Parent=Guid.NewGuid()}]},
            recipe with{Channels=recipe.Channels.Add(new(Guid.NewGuid(),root,[Circle(3),Circle(3)]))},
            recipe with{Channels=recipe.Channels.Add(new(Guid.NewGuid(),root,[Circle(1),Circle(1)]))}})
        {await Assert.ThrowsAsync<CadValidationException>(()=>kernel.EvaluateAsync(invalid,assets));Assert.Equal(0,assets.Count);}
    }
    [Fact] public async Task CancellationBeforeNativeWorkLeavesNoAssets()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>kernel.EvaluateAsync(Straight(),assets,cancel.Token));Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false,false)][InlineData(true,false)][InlineData(false,true)][InlineData(true,true)]
    public async Task BezierAndSplinePathsUseOpenCurveValidation(bool spline,bool curved)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var edge=spline?new SketchBoundaryCurve(new(0,0),new(curved?4:0,10)){SplineControls=[new(0,0),new(0,3),new(curved?-1:0,7),new(curved?4:0,10)]}:
            new SketchBoundaryCurve(new(0,0),new(curved?4:0,10)){BezierControl=new(0,5)};
        var recipe=Straight(Circle(.2)) with{Path=new([edge],PathFrame)};
        using(var result=await kernel.EvaluateAsync(recipe,assets))
        {
            Assert.Equal(BodyKind.Solid,result.Geometry.Kind);
            if(!curved)Assert.Equal(.4*Math.PI,result.Geometry.VolumeMm3,5);
            else Assert.True(result.Geometry.VolumeMm3>.4*Math.PI);
            await using var session=new CadDocumentSession(DocumentSnapshot.Create("Curve"),assets,kernel,new InlineSessionDispatcher());
            await session.ExecuteAsync(new AddBodyCommand(recipe,"Curve path"));var path=files.PathFor("curve.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);var restored=Assert.IsType<SweepRecipe>(loaded.Snapshot.Features.Values.Single().Recipe);
            Assert.Equal(edge.BezierControl,restored.Path.Curves[0].BezierControl);Assert.Equal(edge.SplineControls.ToArray(),restored.Path.Curves[0].SplineControls.ToArray());
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task TransverseFilledSectionsRejectEvenWhenBoundaryWiresDoNotCross()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var recipe=Loft(Square(2),Square(2)) with{Stations=[RigidTransform3d.Identity,new(new(0,0,.1),Quaterniond.FromEulerDegrees(80,0,0))]};
        await Assert.ThrowsAsync<CadValidationException>(()=>kernel.EvaluateAsync(recipe,assets));Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FrozenRecipeRoundtripRecomputeAndSuccessorStayAtomic(bool loft)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Guided storage"),assets,kernel,new InlineSessionDispatcher()))
        {
            GeometryRecipe recipe=loft?Loft(Square(1),Square(2)):Straight();
            await session.ExecuteAsync(new AddBodyCommand(recipe,"Guided"));var source=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(1,1,1,RigidTransform3d.Translate(20,0,0)),"Other",source.PartId));
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Fuse,session.Snapshot.Bodies.Keys));var following=session.Snapshot.Features.Values.Single(f=>f.Inputs.Contains(source.Id));
            var path=files.PathFor("guided.cadoryx");await session.SaveAsync(storage,path);int retained=assets.Count;
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                loaded.Snapshot.Validate();var saved=loaded.Snapshot.Features[source.Id].Recipe;
                if(loft){var typed=Assert.IsType<LoftRecipe>(saved);Assert.Equal(((LoftRecipe)recipe).Channels[0].Id,typed.Channels[0].Id);Assert.Equal(((LoftRecipe)recipe).Stations.ToArray(),typed.Stations.ToArray());}
                else{var typed=Assert.IsType<SweepRecipe>(saved);Assert.Equal(((SweepRecipe)recipe).Path.Curves.ToArray(),typed.Path.Curves.ToArray());Assert.Equal(((SweepRecipe)recipe).Path.Placement,typed.Path.Placement);}
                await using var reopened=new CadDocumentSession(loaded.Snapshot,assets,kernel,new InlineSessionDispatcher());
                var before=reopened.Snapshot;GeometryRecipe edit=loft?((LoftRecipe)saved) with{Stations=[RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,10)]}:
                    ((SweepRecipe)saved) with{Section=new(Circle(2),RigidTransform3d.Identity)};
                await reopened.ExecuteAsync(new RecomputeCommand(source.Id,edit));var after=reopened.Snapshot;after.Validate();
                Assert.Equal((loft?280d/3:40*Math.PI)+1,after.Features[following.Id].Result.VolumeMm3,4);
                Assert.Null(after.Features[source.Id].TopologyHistory);
                await reopened.UndoAsync();Assert.Same(before,reopened.Snapshot);await reopened.RedoAsync();Assert.Same(after,reopened.Snapshot);
            }
            Assert.Equal(retained,assets.Count);
            var manifest=FormatEvolutionTests.Manifest(path);Assert.Contains("cadoryx.guided-authoring.1",manifest.RequiredCapabilities);
            Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["features"].Version,manifest.Sections.Single(s=>s.Kind=="features").SchemaVersion);
            FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=25}:s).ToImmutableArray()});
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(retained,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("nested")][InlineData("companion")][InlineData("kind")][InlineData("output")]
    public async Task MalformedGuidedStorageRejectsWithoutAssetLeaks(string mutation)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Bad guided"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(Straight(),"Sweep"));var path=files.PathFor("bad.cadoryx");await session.SaveAsync(storage,path);int count=assets.Count;
            FormatEvolutionTests.RewriteSection(path,"features",bytes=>
            {
                var section=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);var feature=section.Features[0];var r=feature.Recipe;var guided=r.Guided!;var s=guided.Sweep!;
                r=mutation switch
                {
                    "nested"=>r with{Guided=guided with{Sweep=s with{Section=s.Section with{Guided=guided}}}},
                    "companion"=>r with{Numbers=[1]},"kind"=>r with{Kind="box"},
                    _=>r with{Guided=guided with{Sweep=s with{Output=99}}}
                };
                return MessagePackSerializer.Serialize(section with{Features=[feature with{Recipe=r}]});
            });
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task GuidedSolidStepRoundtripPreservesVolumeAndTopology(bool loft)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var result=await kernel.EvaluateAsync(loft?Loft(Square(1),Square(2)):Straight(),assets))
        {
            using var shape=OcctGeometryBridge.ReadShape(result.Geometry,assets);var step=files.PathFor("guided.step");ShapeExchange.WriteStep(shape,step);
            using var reopened=ShapeExchange.ReadStep(step);Assert.True(reopened.IsValid);Assert.True(reopened.GetSelfInterferenceReport().IsClear);
            Assert.Equal(result.Geometry.VolumeMm3,Math.Abs(reopened.InspectProperties(InspectionPropertyKind.Volume).Mass),4);
            Assert.Equal(shape.GetTopologySummary().UniqueCounts.FaceCount,reopened.GetTopologySummary().UniqueCounts.FaceCount);
        }
        Assert.Equal(0,assets.Count);
    }
    private static CadSketch RoundSketch(DefinitionId part,string name,double radius,RigidTransform3d frame)
    {
        var center=SketchEntityId.New();var circle=SketchEntityId.New();
        return CadSketch.Create(part,name,frame) with{Points=[new(center,new(0,0))],Circles=[new(circle,center,radius)],
            Constraints=[new FixPointConstraint(SketchConstraintId.New(),center,new(0,0)),new RadiusConstraint(SketchConstraintId.New(),circle,radius)]};
    }
    private static SketchProfileReference RoundSource(CadSketch sketch)=>new(sketch.Id,sketch.Revision,[],sketch.Circles.Single().Id);
    [Fact] public async Task LinkedSweepParametersDirectedPathRevisionAndStorageDriveSuccessor()
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var section=RoundSketch(part,"Section",1,RigidTransform3d.Identity);await session.ExecuteAsync(new UpsertSketchCommand(section,new ManagedSketchConstraintSolver()));section=session.Snapshot.Sketches[section.Id];
            var a=SketchEntityId.New();var b=SketchEntityId.New();var line=SketchEntityId.New();
            var path=CadSketch.Create(part,"Directed path",PathFrame) with{Points=[new(a,new(0,0)),new(b,new(0,10))],Lines=[new(line,b,a)],
                Constraints=[new FixPointConstraint(SketchConstraintId.New(),a,new(0,0)),new VerticalConstraint(SketchConstraintId.New(),line),new LengthConstraint(SketchConstraintId.New(),line,10)]};
            await session.ExecuteAsync(new UpsertSketchCommand(path,new ManagedSketchConstraintSolver()));path=session.Snapshot.Sketches[path.Id];
            var recipe=Straight() with{Section=new(Circle(1),section.Plane){Source=RoundSource(section)},
                Path=new SketchPathReference(path.Id,path.Revision,[new(line,Reverse:true)]).Resolve(session.Snapshot,part)};
            await session.ExecuteAsync(new AddBodyCommand(recipe,"Linked sweep",part));var feature=session.Snapshot.Features.Values.Single();
            var parameter=new CadParameter(Guid.NewGuid(),"Radius",ParameterDimension.Length,"2 mm");
            var binding=new ParameterBinding(Guid.NewGuid(),parameter.Id,ParameterTarget.Sketch(section.Id,section.Constraints.OfType<RadiusConstraint>().Single().Id));
            await session.ExecuteAsync(new SetParametersCommand(new([parameter],[binding])));
            Assert.Equal(40*Math.PI,session.Snapshot.Features[feature.Id].Result.VolumeMm3,5);
            path=session.Snapshot.Sketches[path.Id];var length=path.Constraints.OfType<LengthConstraint>().Single();
            await session.ExecuteAsync(new UpsertSketchCommand(path with{Constraints=path.Constraints.Select(c=>c==length?length with{Length=15}:c).ToImmutableArray()},new ManagedSketchConstraintSolver()));
            Assert.Equal(60*Math.PI,session.Snapshot.Features[feature.Id].Result.VolumeMm3,5);session.Snapshot.Validate();
            var current=Assert.IsType<SweepRecipe>(session.Snapshot.Features[feature.Id].Recipe);Assert.True(current.Path.Source!.Edges.Single().Reverse);
            Assert.Equal(session.Snapshot.Sketches[path.Id].Revision,current.Path.Source.Revision);
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new RemoveSketchCommand(path.Id)));
            var filename=files.PathFor("linked-sweep.cadoryx");await session.SaveAsync(new CadDocumentStorage(),filename);
            using var loaded=await new CadDocumentStorage().LoadAsync(filename,assets);loaded.Snapshot.Validate();
            var restored=Assert.IsType<SweepRecipe>(loaded.Snapshot.Features[feature.Id].Recipe);Assert.Equal(current.Section.Source!.SketchId,restored.Section.Source!.SketchId);Assert.Equal(current.Section.Source.Revision,restored.Section.Source.Revision);
            Assert.Equal(current.Path.Source.Edges.ToArray(),restored.Path.Source!.Edges.ToArray());
            var before=session.Snapshot;int count=assets.Count;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(session.Snapshot.Sketches[path.Id] with{Plane=RigidTransform3d.Identity},new ManagedSketchConstraintSolver())));
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task LinkedLoftMultipleSketchesRefreshTogetherAndKeepCorrespondenceIds()
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var datum=new CadDatumPlane(Guid.NewGuid(),part,"Upper datum",DatumPlaneKind.Offset,DatumPlaneSource.Base(DocumentWorkPlaneKind.XY),Value:5);
            await session.ExecuteAsync(new SetDatumsCommand(DatumBook.Empty with{Planes=ImmutableDictionary<Guid,CadDatumPlane>.Empty.Add(datum.Id,datum)}));
            datum=session.Snapshot.Datums.Planes[datum.Id];
            var lower=RoundSketch(part,"Lower",1,RigidTransform3d.Identity);var upper=RoundSketch(part,"Upper",2,datum.Frame);
            await session.ExecuteAsync(new UpsertSketchCommand(lower,new ManagedSketchConstraintSolver()));await session.ExecuteAsync(new UpsertSketchCommand(upper,new ManagedSketchConstraintSolver(),datum:datum.Id));lower=session.Snapshot.Sketches[lower.Id];upper=session.Snapshot.Sketches[upper.Id];
            var recipe=Loft(Circle(1),Circle(2));recipe=recipe with{Channels=[recipe.Channels[0] with{Sources=[RoundSource(lower),RoundSource(upper)]}]};
            await session.ExecuteAsync(new AddBodyCommand(recipe,"Linked loft",part));var feature=session.Snapshot.Features.Values.Single();var channel=recipe.Channels[0].Id;
            var radius=upper.Constraints.OfType<RadiusConstraint>().Single();
            await session.ExecuteAsync(new SetDatumsCommand(session.Snapshot.Datums with{Planes=session.Snapshot.Datums.Planes.SetItem(datum.Id,datum with{Value=10})}));
            upper=session.Snapshot.Sketches[upper.Id];
            await session.ExecuteAsync(new UpsertSketchCommand(upper with{
                Constraints=upper.Constraints.Select(c=>c==radius?radius with{Radius=3}:c).ToImmutableArray()},new ManagedSketchConstraintSolver()));
            session.Snapshot.Validate();var result=session.Snapshot.Features[feature.Id];Assert.Equal(130*Math.PI/3,result.Result.VolumeMm3,4);
            var current=Assert.IsType<LoftRecipe>(result.Recipe);Assert.Equal(channel,current.Channels[0].Id);Assert.Equal(10,current.Stations[1].Translation.Z);
            Assert.Equal(session.Snapshot.Sketches[upper.Id].Revision,current.Channels[0].Sources[1]!.Revision);
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new RemoveSketchCommand(lower.Id)));
            var filename=files.PathFor("linked-loft.cadoryx");await session.SaveAsync(new CadDocumentStorage(),filename);
            using var loaded=await new CadDocumentStorage().LoadAsync(filename,assets);loaded.Snapshot.Validate();
            var restored=Assert.IsType<LoftRecipe>(loaded.Snapshot.Features[feature.Id].Recipe);Assert.Equal(channel,restored.Channels[0].Id);
            Assert.Equal(current.Channels[0].Sources.Select(s=>s!.SketchId),restored.Channels[0].Sources.Select(s=>s!.SketchId));
            var before=session.Snapshot;int count=assets.Count;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetDatumsCommand(session.Snapshot.Datums with{Planes=session.Snapshot.Datums.Planes.SetItem(datum.Id,session.Snapshot.Datums.Planes[datum.Id] with{Value=-1})})));
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExactGuidedRimFilletPersistsAndFreezesAfterSourceEdit(bool loft)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Guided local successor"),assets,kernel,new InlineSessionDispatcher()))
        {
            GeometryRecipe recipe=loft?Loft(Square(1),Square(2)):Straight();await session.ExecuteAsync(new AddBodyCommand(recipe,"Guided"));
            var f=session.Snapshot.Features.Values.Single();ExactTopologySelection selection;
            using(var shape=OcctGeometryBridge.ReadShape(f.Result,assets))using(var map=RepairSnapshot.Create(shape))
            {
                // This fixture selects its authored top rim; production never reuses this geometric lookup after an edit.
                var picked=map.Topology.Single(t=>
                {
                    if(t.Kind!=ShapeKind.Edge)return false;using var edge=map.CopySubshape(t.Selection);var b=edge.GetBoundingBox();
                    return Math.Abs(b.Minimum.Z-(loft?5:10))<1e-5&&Math.Abs(b.Maximum.Z-(loft?5:10))<1e-5&&
                        (!loft||Math.Abs(b.Minimum.Y+2)<1e-5&&Math.Abs(b.Maximum.Y+2)<1e-5&&b.SizeX>3.99);
                }).Selection;
                selection=new(session.Snapshot.Id,f.Id,f.Result.Revision,f.Result.AssetId,map.Fingerprint,picked.Index,HistoryShapeKind.Edge,OcctGeometryKernel.HistoryAdapterVersion);
            }
            await session.ExecuteAsync(new ExactLocalFeatureCommand(selection,Cadoryx.Db.LocalFeatureOperation.Fillet,.1));
            var successor=session.Snapshot.Features.Values.Single(g=>g.Id!=f.Id);Assert.Equal(f.Id,successor.Inputs.Single());Assert.NotNull(successor.TopologyBinding!.ExactEdge);
            Assert.False(successor.IsStale);Assert.True(successor.Result.VolumeMm3<f.Result.VolumeMm3);Assert.NotNull(successor.TopologyHistory);
            var path=files.PathFor("guided-local.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets)){loaded.Snapshot.Validate();Assert.Equal(successor.TopologyBinding,loaded.Snapshot.Features[successor.Id].TopologyBinding);}
            var before=session.Snapshot;GeometryRecipe changed=loft?((LoftRecipe)recipe) with{Stations=[RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,10)]}:
                ((SweepRecipe)recipe) with{Section=new(Circle(2),RigidTransform3d.Identity)};
            await session.ExecuteAsync(new RecomputeCommand(f.Id,changed));var frozen=session.Snapshot.Features[successor.Id];
            Assert.True(frozen.IsStale);Assert.Equal(successor.Result,frozen.Result);Assert.Null(frozen.TopologyHistory);
            await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ConfirmAsync(session.Snapshot,selection,assets));
            await session.UndoAsync();Assert.Same(before,session.Snapshot);Assert.False(session.Snapshot.Features[successor.Id].IsStale);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task LinkedGuidedParameterUpdateRefreshesDrawingAndInvalidEditKeepsEverything(bool loft)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher()))
        {
            var lower=RoundSketch(part,"Lower",1,RigidTransform3d.Identity);await session.ExecuteAsync(new UpsertSketchCommand(lower,new ManagedSketchConstraintSolver()));lower=session.Snapshot.Sketches[lower.Id];
            GeometryRecipe recipe;
            if(loft)
            {
                var upper=RoundSketch(part,"Upper",2,RigidTransform3d.Translate(0,0,10));await session.ExecuteAsync(new UpsertSketchCommand(upper,new ManagedSketchConstraintSolver()));upper=session.Snapshot.Sketches[upper.Id];
                var seed=Loft(Circle(1),Circle(2));recipe=seed with{Stations=[lower.Plane,upper.Plane],Channels=[seed.Channels[0] with{Sources=[RoundSource(lower),RoundSource(upper)]}]};
            }
            else recipe=Straight() with{Section=new(Circle(1),lower.Plane){Source=RoundSource(lower)}};
            await session.ExecuteAsync(new AddBodyCommand(recipe,"Guided",part));var f=session.Snapshot.Features.Values.Single();
            var sheet=TechnicalDrawingSheet.A4Landscape("Guided drawing");await session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(70,90),1));
            await session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Section",DrawingViewKind.Section,new(140,90),1,sectionNormal:Vector3d.UnitZ,sectionOffsetMm:5));
            var before=session.Snapshot;Assert.All(before.DrawingSheets[sheet.Id].Views,v=>Assert.NotEmpty(v.Strokes));
            var parameter=new CadParameter(Guid.NewGuid(),"Radius",ParameterDimension.Length,"2 mm");
            var binding=new ParameterBinding(Guid.NewGuid(),parameter.Id,ParameterTarget.Sketch(lower.Id,lower.Constraints.OfType<RadiusConstraint>().Single().Id));
            await session.ExecuteAsync(new SetParametersCommand(new([parameter],[binding])));var after=session.Snapshot;
            after.Validate();Assert.NotEqual(f.Result.AssetId,after.Features[f.Id].Result.AssetId);
            Assert.All(after.DrawingSheets[sheet.Id].Views,v=>{Assert.Null(v.StaleReason);Assert.NotEmpty(v.Strokes);Assert.Equal(after.Features[f.Id].Result.AssetId,v.Sources.Single().Asset);});
            Assert.Equal(40*Math.PI,after.Features[f.Id].Result.VolumeMm3,4);
            int count=assets.Count;await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new SetParametersCommand(new([parameter with{Expression="-1 mm"}],[binding]))));
            Assert.Same(after,session.Snapshot);Assert.Equal(count,assets.Count);
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
            string path=files.PathFor("guided-drawing.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            using var loaded=await new CadDocumentStorage().LoadAsync(path,assets);loaded.Snapshot.Validate();Assert.All(loaded.Snapshot.DrawingSheets[sheet.Id].Views,v=>Assert.Null(v.StaleReason));
            string pdf=files.PathFor("guided.pdf");TechnicalDrawingPdf.Write(loaded.Snapshot,pdf);Assert.True(new FileInfo(pdf).Length>1000);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task FinalGeneratedEdgeAndFaceBindExactlyAndBecomeStaleAfterEdit(bool loft)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Exact guided topology"),assets,kernel,new InlineSessionDispatcher()))
        {
            GeometryRecipe recipe=loft?Loft(Square(1),Square(2)):Straight();await session.ExecuteAsync(new AddBodyCommand(recipe,"Guided"));
            var f=session.Snapshot.Features.Values.Single();using var shape=OcctGeometryBridge.ReadShape(f.Result,assets);using var map=RepairSnapshot.Create(shape);
            var selections=new List<ExactTopologySelection>();
            foreach(var kind in new[]{ShapeKind.Edge,ShapeKind.Face})
            {
                var picked=map.Topology.First(t=>t.Kind==kind).Selection;
                var selection=new ExactTopologySelection(session.Snapshot.Id,f.Id,f.Result.Revision,f.Result.AssetId,map.Fingerprint,picked.Index,
                    kind==ShapeKind.Edge?HistoryShapeKind.Edge:HistoryShapeKind.Face,OcctGeometryKernel.HistoryAdapterVersion);
                await kernel.ConfirmAsync(session.Snapshot,selection,assets);selections.Add(selection);
                await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ConfirmAsync(session.Snapshot,selection with{Fingerprint=new string('0',64)},assets));
            }
            GeometryRecipe changed=loft?((LoftRecipe)recipe) with{Stations=[RigidTransform3d.Identity,RigidTransform3d.Translate(0,0,10)]}:
                ((SweepRecipe)recipe) with{Section=new(Circle(2),RigidTransform3d.Identity)};
            await session.ExecuteAsync(new RecomputeCommand(f.Id,changed));
            foreach(var selection in selections)await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ConfirmAsync(session.Snapshot,selection,assets));
            Assert.Null(session.Snapshot.Features[f.Id].TopologyHistory);
        }
        Assert.Equal(0,assets.Count);
    }
}
