using System.Text.Json;
using System.Collections.Immutable;
using Cadoryx.AI.Contracts;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using MessagePack;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;
using LocalFeatureOperation=Cadoryx.Db.LocalFeatureOperation;

namespace Cadoryx.Tests;

public sealed class SolidOffsetTests
{
    [Theory][InlineData(false,-1,false)][InlineData(false,1,false)][InlineData(true,-1,false)][InlineData(true,1,false)]
    [InlineData(false,-1,true)][InlineData(false,1,true)]
    public async Task SignedOffsetHasAnalyticVolumeExactOutputUndoAndUnchangedSource(bool cylinder,double distance,bool rotated)
    {
        await using var f=await Fixture.Create(cylinder,rotated);var before=f.Session.Snapshot;
        var recipe=new SolidOffsetRecipe(f.Source.Result,distance);
        using(var candidate=await f.Kernel.EvaluateAsync(recipe,f.Assets))
        {
            Assert.Equal(Expected(cylinder,distance),candidate.Geometry.VolumeMm3,4);Assert.Equal(BodyKind.Solid,candidate.Geometry.Kind);
            Assert.Null(candidate.TopologyHistory);Assert.Contains(candidate.Diagnostics,d=>d.Code=="HISTORY.UNSUPPORTED");
        }
        await f.Session.ExecuteAsync(new AddSolidOffsetCommand(f.Source.OutputBodyId,recipe,"Offset"));var after=f.Session.Snapshot;after.Validate();
        var feature=after.Features.Values.Single(p=>p.Recipe is SolidOffsetRecipe);Assert.Equal(f.Source.Result,after.Features[f.Source.Id].Result);
        Assert.Single(after.Bodies);Assert.Equal(Expected(cylinder,distance),feature.Result.VolumeMm3,4);
        Assert.Equal(f.Source.Id,feature.Inputs.Single());Assert.Null(feature.TopologyHistory);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }
    [Theory][InlineData(0)][InlineData(1e-7)][InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(100001)][InlineData(-20)]
    public async Task InvalidOrCollapsedOffsetLeavesAuthorHistoryAndAssetsUnchanged(double distance)
    {
        await using var f=await Fixture.Create();var before=f.Session.Snapshot;int count=f.Assets.Count;var history=f.Session.HistoryUsage;
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Session.ExecuteAsync(new AddSolidOffsetCommand(f.Source.OutputBodyId,new(f.Source.Result,distance),"Bad")));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(history,f.Session.HistoryUsage);Assert.Equal(count,f.Assets.Count);
    }
    [Fact]public async Task WholeSourceEditRecomputesOffsetsAndSuppressionRestoresTheirExactOutput()
    {
        await using var f=await Fixture.Create();await f.Offset(-1);var id=f.OffsetFeature.Id;
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));
        Assert.Equal(9*10*13,f.Session.Snapshot.Features[id].Result.VolumeMm3,4);Assert.False(f.Session.Snapshot.Features[id].IsStale);
        Assert.Equal(f.Session.Snapshot.Features[f.Source.Id].Result,((SolidOffsetRecipe)f.Session.Snapshot.Features[id].Recipe).Source);
        await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(id,true));Assert.Equal(11*12*15,f.Session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,4);
        await f.Session.ExecuteAsync(new SetFeatureSuppressionCommand(id,false));Assert.Equal(9*10*13,f.Session.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,4);
        f.Session.Snapshot.Validate();
    }
    [Fact]public async Task SignedLengthDriverEditsRecomputeAndInvalidDriverRollsBack()
    {
        await using var f=await Fixture.Create();await f.Offset(-1);var id=f.OffsetFeature.Id;
        var parameter=new CadParameter(Guid.NewGuid(),"offset",ParameterDimension.Length,"-2 mm");var binding=new ParameterBinding(Guid.NewGuid(),parameter.Id,ParameterTarget.Feature(id,"Distance"));
        await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[binding])));var after=f.Session.Snapshot;
        Assert.Equal(6*8*11,after.Features[id].Result.VolumeMm3,4);Assert.Equal(-2,((SolidOffsetRecipe)after.Features[id].Recipe).Distance);
        int count=f.Assets.Count;
        await Assert.ThrowsAnyAsync<Exception>(()=>f.Session.ExecuteAsync(new SetParametersCommand(new([parameter with{Expression="-20 mm"}],[binding]))));
        Assert.Same(after,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new RecomputeCommand(id,((SolidOffsetRecipe)after.Features[id].Recipe) with{Distance=-1})));
        Assert.Same(after,f.Session.Snapshot);
    }
    [Fact]public async Task SaveReopenKeepsSignedAuthorAndLegacyLabelCannotSmuggleNewRecipe()
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();await f.Offset(-1);
        var path=files.PathFor("offset.cadoryx");var storage=new CadDocumentStorage();await f.Session.SaveAsync(storage,path);
        var manifest=FormatEvolutionTests.Manifest(path);Assert.Contains("cadoryx.solid-offset.1",manifest.RequiredCapabilities);
        Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["features"].Version,manifest.Sections.Single(s=>s.Kind=="features").SchemaVersion);Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["document"].Version,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);
        using(var loaded=await storage.LoadAsync(path,f.Assets))
        {loaded.Snapshot.Validate();var offset=loaded.Snapshot.Features[f.OffsetFeature.Id];Assert.Equal(-1,((SolidOffsetRecipe)offset.Recipe).Distance);Assert.Equal(1040,offset.Result.VolumeMm3,4);}
        int count=f.Assets.Count;FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=35}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }
    [Theory][InlineData("numbers")][InlineData("companion")][InlineData("sources")][InlineData("nonfinite")]
    public async Task MalformedOffsetPayloadRejectsWithoutLeakingAssets(string mutation)
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();await f.Offset(-1);
        var path=files.PathFor("bad.cadoryx");var storage=new CadDocumentStorage();await f.Session.SaveAsync(storage,path);int count=f.Assets.Count;
        FormatEvolutionTests.RewriteSection(path,"features",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackFeaturesV3>(bytes);
            return MessagePackSerializer.Serialize(data with{Features=data.Features.Select(feature=>
            {
                if(feature.Recipe.Kind!="solid-offset")return feature;
                var recipe=mutation switch{"numbers"=>feature.Recipe with{Numbers=[]},"companion"=>feature.Recipe with{FaceFeature=new()},
                    "sources"=>feature.Recipe with{Sources=[]},_=>feature.Recipe with{Numbers=[double.NaN]}};
                return feature with{Recipe=recipe};
            }).ToArray()});
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,f.Assets));Assert.Equal(count,f.Assets.Count);
    }
    [Fact]public async Task OldValidDocumentStillMigratesAndExactOriginalEdgeCannotBeGuessedAcrossOffset()
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();var path=files.PathFor("old.cadoryx");var storage=new CadDocumentStorage();await f.Session.SaveAsync(storage,path);
        // Emulate the actual legacy four-field point payload before declaring its document version.
        SketchLegacyPointFixture.Prepare(path);
        FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind is "document" or "features"?s with{SchemaVersion=s.Kind=="document"?39:35}:s).ToImmutableArray()});
        using(var loaded=await storage.LoadAsync(path,f.Assets)){loaded.Snapshot.Validate();Assert.Equal(1800,loaded.Snapshot.Bodies.Values.Single().Geometry.VolumeMm3,4);}
        var reference=TopologyReference.Box(f.Session.Snapshot,f.Source.Id,BoxBoundary.XMin,BoxBoundary.YMin);await f.Offset(-1);
        var trace=await f.Kernel.TraceAsync(f.Session.Snapshot,reference,f.OffsetFeature.Id,f.Assets);
        Assert.Equal(HistoryResolutionStatus.Unsupported,trace.Status);Assert.Null(trace.Target);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task EditorUsesWholeSourceSignedDistanceCandidateAndCancelOrConfirm(bool commit)
    {
        await using var f=await Fixture.Create();var before=f.Session.Snapshot;
        await using var editor=new FaceFeatureEditorViewModel(f.Document,f.Kernel,FaceFeatureKind.SolidOffset);
        Assert.True(editor.IsOffset);Assert.False(editor.IsShell);Assert.False(editor.IsDraft);editor.SelectedSource=editor.Sources.Single();editor.OffsetDistance=-1;
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,f.Session.Snapshot);
        if(commit){await editor.ConfirmCommand.ExecuteAsync(null);Assert.Equal(1040,f.OffsetFeature.Result.VolumeMm3,4);}
        else{editor.CancelCommand.Execute(null);Assert.False(editor.HasCandidate);Assert.Same(before,f.Session.Snapshot);}
    }
    [Theory][InlineData("cancel")][InlineData("close")][InlineData("stale")]
    public async Task LateNativeCandidateIsDiscardedOnEditorLifetimeOrStateChange(string action)
    {
        var kernel=new GatedKernel();await using var f=await Fixture.Create(kernel:kernel);var before=f.Session.Snapshot;
        await using var editor=new FaceFeatureEditorViewModel(f.Document,kernel,FaceFeatureKind.SolidOffset);editor.SelectedSource=editor.Sources.Single();editor.OffsetDistance=-1;
        kernel.Gate=true;var preview=editor.PreviewCommand.ExecuteAsync(null);await kernel.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));Task? close=null;
        if(action=="cancel")editor.CancelCommand.Execute(null);else if(action=="close")close=editor.DisposeAsync().AsTask();
        else await f.Session.ExecuteAsync(new EditDocumentCommand("Rename",s=>s with{Name="Changed"}));
        var expected=f.Session.Snapshot;kernel.Release.TrySetResult();await preview;if(close is not null)await close;
        Assert.False(editor.HasCandidate);Assert.Same(expected,f.Session.Snapshot);Assert.Single(f.Session.Snapshot.Features);
        if(action!="stale")Assert.Same(before,f.Session.Snapshot);
    }
    [Fact]public async Task StructuredToolUsesExactInputAndReusableAuthorWithNoOpEdit()
    {
        await using var f=await Fixture.Create();var tools=new CadAgentToolset(new CadCommandLineService(),f.Document);
        var before=f.Session.Snapshot;string author=JsonSerializer.Serialize(new{type="solid_offset",input=new{featureId=f.Source.Id.ToString(),revision=f.Source.Result.Revision.ToString()},distance=-1});
        string result=await tools.ExecuteAsync(new("offset","cad_model",JsonSerializer.Serialize(new{action="create",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),partId=f.Source.PartId.ToString(),name="Tool offset",authorJson=author})),default);
        using var parsed=JsonDocument.Parse(result);Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(),result);Assert.Equal(1040,f.OffsetFeature.Result.VolumeMm3,4);
        result=await tools.ExecuteAsync(new("offset-query","cad_inspect",JsonSerializer.Serialize(new{area="recipes",id=f.OffsetFeature.Id.ToString()})),default);
        Assert.Contains("solid_offset",result);Assert.Contains("\"distance\":-1",result);
        using var inspection=JsonDocument.Parse(result);var edit=inspection.RootElement.GetProperty("items")[0].GetProperty("edit").GetRawText();var after=f.Session.Snapshot;
        result=await tools.ExecuteAsync(new("offset-noop","cad_model",edit),default);
        using var unchanged=JsonDocument.Parse(result);Assert.True(unchanged.RootElement.GetProperty("success").GetBoolean(),result);
        Assert.False(unchanged.RootElement.GetProperty("changed").GetBoolean());Assert.Same(after,f.Session.Snapshot);
    }
    [Fact]public async Task DrivenOffsetRefreshesDrawingAndSurvivesNativeAndStepReopen()
    {
        using var files=new TestFiles();await using var f=await Fixture.Create();await f.Offset(-1);var id=f.OffsetFeature.Id;
        var sheet=TechnicalDrawingSheet.A4Landscape("Offset drawing");await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddSheet(sheet));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Front",DrawingViewKind.Front,new(70,90),1));
        await f.Session.ExecuteAsync(TechnicalDrawingCommands.AddView(sheet.Id,Guid.NewGuid(),"Section",DrawingViewKind.Section,new(140,90),1,sectionNormal:Vector3d.UnitZ,sectionOffsetMm:5));
        var parameter=new CadParameter(Guid.NewGuid(),"skin",ParameterDimension.Length,"-2 mm");var binding=new ParameterBinding(Guid.NewGuid(),parameter.Id,ParameterTarget.Feature(id,"Distance"));
        await f.Session.ExecuteAsync(new SetParametersCommand(new([parameter],[binding])));var after=f.Session.Snapshot;after.Validate();
        Assert.Equal(528,after.Features[id].Result.VolumeMm3,4);
        Assert.All(after.DrawingSheets[sheet.Id].Views,v=>{Assert.Null(v.StaleReason);Assert.NotEmpty(v.Strokes);Assert.Equal(after.Features[id].Result.AssetId,v.Sources.Single().Asset);});
        int count=f.Assets.Count;var storage=new CadDocumentStorage();var path=files.PathFor("offset-drawing.cadoryx");await f.Session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,f.Assets)){loaded.Snapshot.Validate();TechnicalDrawingPdf.Write(loaded.Snapshot,files.PathFor("offset.pdf"));Assert.True(new FileInfo(files.PathFor("offset.pdf")).Length>1000);}
        var step=files.PathFor("offset.step");await f.Kernel.ExportAsync(after,f.Assets,step);
        using(var loaded=await f.Kernel.ImportAsync(step,f.Assets)){loaded.Snapshot.Validate();Assert.Equal(528,loaded.Snapshot.Bodies.Values.Sum(b=>b.Geometry.VolumeMm3),4);Assert.All(loaded.Snapshot.Bodies.Values,b=>Assert.Equal(BodyKind.Solid,b.Geometry.Kind));}
        Assert.Equal(count,f.Assets.Count);
    }
    [Fact]public async Task ExplicitFinalEdgeSupportsFilletAndSourceChangeFreezesItsOldSelection()
    {
        await using var f=await Fixture.Create();await f.Offset(-1);var offset=f.OffsetFeature;ExactTopologySelection selected;
        using(var shape=OcctGeometryBridge.ReadShape(offset.Result,f.Assets))using(var map=RepairSnapshot.Create(shape))
        {
            var edge=map.Topology.Single(t=>
            {
                if(t.Kind!=ShapeKind.Edge)return false;using var native=map.CopySubshape(t.Selection);var b=native.GetBoundingBox();
                return Math.Abs(b.Minimum.Z-1)<1e-5&&Math.Abs(b.Maximum.Z-1)<1e-5&&Math.Abs(b.Minimum.X-1)<1e-5&&Math.Abs(b.Maximum.X-1)<1e-5&&b.SizeY>9.99;
            });
            selected=new(f.Session.Snapshot.Id,offset.Id,offset.Result.Revision,offset.Result.AssetId,map.Fingerprint,edge.Selection.Index,HistoryShapeKind.Edge,OcctGeometryKernel.HistoryAdapterVersion);
        }
        await f.Session.ExecuteAsync(new ExactLocalFeatureCommand(selected,LocalFeatureOperation.Fillet,.1));
        var successor=f.Session.Snapshot.Features.Values.Single(p=>p.Recipe is HistoryFilletRecipe);Assert.False(successor.IsStale);Assert.Equal(offset.Id,successor.Inputs.Single());
        var before=f.Session.Snapshot;await f.Session.ExecuteAsync(new RecomputeCommand(offset.Id,((SolidOffsetRecipe)offset.Recipe) with{Distance=-1.1}));
        Assert.True(f.Session.Snapshot.Features[successor.Id].IsStale);Assert.Equal(successor.Result,f.Session.Snapshot.Features[successor.Id].Result);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
    }
    [Fact]public async Task StaleSourceIdentityAndLockedLayerRejectBeforeChangingHistoryOrAssets()
    {
        await using var f=await Fixture.Create();var old=f.Source.Result;
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));var before=f.Session.Snapshot;int count=f.Assets.Count;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new AddSolidOffsetCommand(f.Source.OutputBodyId,new(old,-1),"Old")));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
        var body=before.Bodies.Values.Single();await f.Session.ExecuteAsync(new EditDocumentCommand("Lock",s=>s with{Layers=s.Layers.SetItem(body.LayerId,s.Layers[body.LayerId] with{IsLocked=true})}));
        before=f.Session.Snapshot;var history=f.Session.HistoryUsage;
        await Assert.ThrowsAsync<CadValidationException>(()=>f.Session.ExecuteAsync(new AddSolidOffsetCommand(body.Id,new(before.Features[f.Source.Id].Result,-1),"Locked")));
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(history,f.Session.HistoryUsage);Assert.Equal(count,f.Assets.Count);
    }
    [Fact]public async Task ImportedFaceColorsAreNotGuessedWhileBodyColorLayerAndMaterialSurvive()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var input=files.PathFor("colored.step");MirrorStyleTests.CreateSource(input);
        using(var imported=await kernel.ImportAsync(input,assets))await using(var session=new CadDocumentSession(imported.Snapshot,assets,kernel,new InlineSessionDispatcher()))
        {
            var source=session.Snapshot.Bodies.Values.Single();Assert.True(source.Appearance.PreserveSourceStyles);Assert.NotNull(source.Geometry.Source);
            var material=new CadMaterial(MaterialId.New(),"Steel",7.85e-6);
            await session.ExecuteAsync(new EditDocumentCommand("Material",s=>s with{Materials=s.Materials.Add(material.Id,material),Bodies=s.Bodies.SetItem(source.Id,source with{MaterialId=material.Id})}));
            var recipe=new SolidOffsetRecipe(source.Geometry,-.2);
            using(var candidate=await kernel.EvaluateAsync(recipe,assets)){Assert.Null(candidate.Geometry.Source);Assert.Contains(candidate.Diagnostics,d=>d.Code=="STYLE.UNSUPPORTED");}
            await session.ExecuteAsync(new AddSolidOffsetCommand(source.Id,recipe,"Colored skin"));var output=session.Snapshot.Bodies.Values.Single();
            Assert.Equal(source.LayerId,output.LayerId);Assert.Equal(source.Appearance.Argb,output.Appearance.Argb);Assert.Equal(source.Appearance.ByLayer,output.Appearance.ByLayer);Assert.False(output.Appearance.PreserveSourceStyles);Assert.Equal(material.Id,output.MaterialId);
            Assert.Equal(3.6*2.6*1.6,output.Geometry.VolumeMm3,5);Assert.Null(output.Geometry.Source);
            var path=files.PathFor("offset-color.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
            using(var loaded=await new CadDocumentStorage().LoadAsync(path,assets)){Assert.Equal(output.Appearance,loaded.Snapshot.Bodies[output.Id].Appearance);Assert.Equal(material.Id,loaded.Snapshot.Bodies[output.Id].MaterialId);}
            var step=files.PathFor("offset-color.step");await kernel.ExportAsync(session.Snapshot,assets,step);
            using(var loaded=await kernel.ImportAsync(step,assets)){var body=loaded.Snapshot.Bodies.Values.Single();Assert.Equal(output.Appearance.Argb,body.Appearance.Argb);Assert.Equal(output.Geometry.VolumeMm3,body.Geometry.VolumeMm3,5);}
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(-.2)][InlineData(.2)]
    public async Task ConcaveThroughHoleOffsetRetainsExactSolidAndAuthoredContainment(double distance)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var box=await kernel.EvaluateAsync(new BoxRecipe(10,12,15,RigidTransform3d.Identity),assets))
        using(var hole=await kernel.EvaluateAsync(new CylinderRecipe(2,17,RigidTransform3d.Translate(5,6,-1)),assets))
        using(var source=await kernel.EvaluateAsync(new BooleanRecipe(BooleanOperation.Cut,[box.Geometry,hole.Geometry]),assets))
        using(var result=await kernel.EvaluateAsync(new SolidOffsetRecipe(source.Geometry,distance),assets))
        using(var original=OcctGeometryBridge.ReadShape(source.Geometry,assets))
        using(var final=OcctGeometryBridge.ReadShape(result.Geometry,assets))
        {
            Assert.True(final.GetValidationReport(geometryChecks:true,exact:true).IsValid);Assert.True(final.GetSelfInterferenceReport().IsClear);
            Assert.Equal(1,final.GetTopologySummary().UniqueCounts.SolidCount);
            using var common=original.Common(final);Assert.Equal(Math.Min(source.Geometry.VolumeMm3,result.Geometry.VolumeMm3),common.InspectProperties(InspectionPropertyKind.Volume).Mass,4);
            Assert.True(distance<0?result.Geometry.VolumeMm3<source.Geometry.VolumeMm3:result.Geometry.VolumeMm3>source.Geometry.VolumeMm3);
            var expectedInward=(10+2*distance)*(12+2*distance)*(15+2*distance)-Math.PI*Math.Pow(2-distance,2)*(15+2*distance);
            if(distance<0)Assert.Equal(expectedInward,result.Geometry.VolumeMm3,4);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("edge")][InlineData("vertex")][InlineData("face")][InlineData("duplicate")][InlineData("multiple")]
    public async Task CompoundWithOtherTopologyRejectsWithoutDiscardingMembersOrLeakingAssets(string kind)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        using(var box=ShapeFactory.CreateBox(10,12,15))using(var extraBox=ShapeFactory.CreateBox(2,3,4))
        using(var edge=ShapeFactory.CreateEdge(new GpPoint(30,30,30),new GpPoint(31,31,31)))
        {
            var faces=extraBox.GetFaces();var vertices=edge.GetSubShapes(ShapeKind.Vertex);
            try
            {
                using var compound=ShapeFactory.CreateCompound([box,kind switch{"edge"=>edge,"vertex"=>vertices[0],"face"=>faces[0],"duplicate"=>box,_=>extraBox}]);
                using var stored=OcctGeometryBridge.StoreShape(compound,assets);int count=assets.Count;
                await Assert.ThrowsAsync<CadValidationException>(()=>kernel.EvaluateAsync(new SolidOffsetRecipe(stored.Geometry with{Kind=BodyKind.Solid},-.2),assets));
                Assert.Equal(count,assets.Count);
            }
            finally{foreach(var face in faces)face.Dispose();foreach(var vertex in vertices)vertex.Dispose();}
        }
        Assert.Equal(0,assets.Count);
    }
    private static double Expected(bool cylinder,double distance)=>cylinder?
        distance<0?32*Math.PI:160*Math.PI+2*Math.PI*(9+3*Math.PI/2+2d/3):distance<0?1040:2700+37*Math.PI+4*Math.PI/3;
    private sealed class Fixture:IAsyncDisposable
    {
        internal MemoryAssetStore Assets{get;}=new();internal IGeometryKernel Evaluator{get;}internal OcctGeometryKernel Kernel{get;}=new();
        internal CadDocumentSession Session{get;}internal CadDocumentViewModel Document{get;}internal FeatureDefinition Source{get;private set;}=null!;
        internal FeatureDefinition OffsetFeature=>Session.Snapshot.Features.Values.Single(p=>p.Recipe is SolidOffsetRecipe);
        private Fixture(IGeometryKernel? kernel){Evaluator=kernel??Kernel;Session=new(DocumentSnapshot.Create("Solid offsets"),Assets,Evaluator,new InlineSessionDispatcher());Document=new(Session,Evaluator,new CadMessageLog());}
        internal static async Task<Fixture> Create(bool cylinder=false,bool rotated=false,IGeometryKernel? kernel=null)
        {
            var f=new Fixture(kernel);var frame=rotated?new RigidTransform3d(new(30,-20,10),Quaterniond.FromEulerDegrees(17,31,43)):RigidTransform3d.Identity;
            await f.Session.ExecuteAsync(new AddBodyCommand(cylinder?new CylinderRecipe(3,10,frame):new BoxRecipe(10,12,15,frame),"Source"));f.Source=f.Session.Snapshot.Features.Values.Single();return f;
        }
        internal Task Offset(double distance)=>Session.ExecuteAsync(new AddSolidOffsetCommand(Source.OutputBodyId,new(Source.Result,distance),"Offset"));
        public async ValueTask DisposeAsync(){Document.Detach();await Session.DisposeAsync();Assert.Equal(0,Assets.Count);}
    }
    private sealed class GatedKernel:IGeometryKernel
    {
        private readonly OcctGeometryKernel inner=new();public bool Gate{get;set;}public string Version=>inner.Version;public bool Supports(GeometryRecipe r)=>inner.Supports(r);
        internal TaskCompletionSource Started{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);internal TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe r,IAssetStore a,CancellationToken t=default)
        {if(Gate&&r is SolidOffsetRecipe){Started.TrySetResult();await Release.Task;return await inner.EvaluateAsync(r,a,CancellationToken.None);}return await inner.EvaluateAsync(r,a,t);}
        public Task<LoadedDocument> ImportAsync(string p,IAssetStore a,CancellationToken t=default)=>inner.ImportAsync(p,a,t);
        public Task ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CancellationToken t=default)=>inner.ExportAsync(s,a,p,t);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CadExportOptions o,CancellationToken t=default)=>inner.ExportAsync(s,a,p,o,t);
    }
}
