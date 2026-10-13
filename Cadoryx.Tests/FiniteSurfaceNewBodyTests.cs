using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.CommandLine;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

[Collection("ModelAgentNativeGates")]
public sealed partial class FiniteSurfaceNewBodyTests
{
    public static IEnumerable<object[]> Cases()=>from kind in new[]{"plane","sphere","cylinder"}
        from negative in new[]{false,true} from transform in new[]{0,1,2} from start in new[]{5d,6d}
        select new object[]{kind,negative,transform,start};
    private static RigidTransform3d Outer(int transform)=>transform switch
    {
        1=>new(new(17,-9,3),Quaterniond.FromEulerDegrees(18,27,-35)),
        2=>new(new(500,-40,80),Quaterniond.FromAxisAngle(new(1,0,0),Math.PI)),
        _=>RigidTransform3d.Identity
    };
    private static async Task<ExtrudeRecipe> Seed(CadDocumentSession session,MemoryAssetStore assets,string kind,bool negative=false,int transform=0,double start=6)
    {
        var outer=Outer(transform);var recipe=await FiniteSurfaceExtrusionTests.Seed(session,assets,kind,outer);
        var frame=outer*(negative?new RigidTransform3d(new(0,10,start),Quaterniond.FromAxisAngle(new(1,0,0),Math.PI)):RigidTransform3d.Translate(0,0,start));
        return recipe with{Placement=frame,Options=recipe.Options! with{Operation=ExtrusionOperation.NewBody,Source=null,SupportFace=null,
            Side=negative?ExtrusionSide.Negative:ExtrusionSide.Positive}};
    }
    private static double Expected(string kind,double start)=>kind switch
    {
        "sphere"=>Math.PI*4*(18-start)-2*Math.PI/3*(216-Math.Pow(32,1.5)),
        "cylinder"=>4*(4*(18-start)-(2*Math.Sqrt(32)+36*Math.Asin(1d/3))),
        _=>16*(12-start)
    };
    [Theory,MemberData(nameof(Cases))]
    public async Task FreeStartingPlaneBuildsCompleteFiniteMaterialWithoutConsumingAnyOriginalBody(string kind,bool negative,int transform,double start)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Independent finite material"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,kind,negative,transform,start);var before=session.Snapshot;
            var part=before.Features[recipe.Options!.LimitFace!.FeatureId].PartId;
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Free finite",part));var accepted=session.Snapshot;
            var feature=Assert.Single(accepted.Features.Values,f=>!before.Features.ContainsKey(f.Id));
            Assert.Empty(feature.Inputs);Assert.Null(recipe.Options.Source);Assert.Null(recipe.Options.SupportFace);
            Assert.Equal(before.Bodies.Count+1,accepted.Bodies.Count);
            foreach(var body in before.Bodies)Assert.Equal(body.Value,accepted.Bodies[body.Key]);
            foreach(var original in before.Features)Assert.Equal(original.Value,accepted.Features[original.Key]);
            Assert.InRange(Math.Abs(Expected(kind,start)-feature.Result.VolumeMm3),0,1e-5);
            using(var shape=OcctGeometryBridge.ReadShape(feature.Result,assets))
            {Assert.True(shape.GetValidationReport(true,true).IsValid);Assert.True(shape.GetSelfInterferenceReport().IsClear);Assert.Equal(1,shape.GetTopologySummary().UniqueCounts.SolidCount);}
            await kernel.ConfirmFiniteSurfaceExtrusionAsync(recipe,feature.Result,assets);accepted.Validate();
            await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(accepted,session.Snapshot);
        }
        Assert.Equal(0,assets.Count);Assert.Equal(0,assets.SizeBytes);
    }
    [Theory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task HolesAndRecursiveIslandKeepEveryDisconnectedMaterialSolid(bool island,bool negative)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Independent island"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane",negative);var profile=SketchProfile.FromCircle(new(5,5),2) with{Holes=[new(new(5,5),1)]};
            if(island)profile=profile with{Islands=[new([],new(new(5,5),.5))]};recipe=recipe with{Profile=profile};
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Island",session.Snapshot.Features[recipe.Options!.LimitFace!.FeatureId].PartId));
            var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            Assert.InRange(Math.Abs((island?3.25:3)*Math.PI*6-feature.Result.VolumeMm3),0,1e-5);
            using var shape=OcctGeometryBridge.ReadShape(feature.Result,assets);Assert.Equal(island?2:1,shape.GetTopologySummary().UniqueCounts.SolidCount);
            await kernel.ConfirmFiniteSurfaceExtrusionAsync(recipe,feature.Result,assets);
        }
        Assert.Equal(0,assets.Count);Assert.Equal(0,assets.SizeBytes);
    }
    [Theory][InlineData("small")][InlineData("support")][InlineData("source")][InlineData("wrong-direction")][InlineData("far-plane")][InlineData("foreign-document")][InlineData("fingerprint")][InlineData("target")]
    public async Task IncompleteOrForeignAuthorIsRejectedAtomicallyAndCannotLeavePrivateSeedAssets(string fault)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Rejected new finite"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,fault=="small"?"small":"plane");var options=recipe.Options!;
            var original=session.Snapshot.Features.Values.Single(f=>f.Name=="Target");
            recipe=fault switch
            {
                "support"=>recipe with{Options=options with{SupportFace=FiniteSurfaceExtrusionTests.Pick(session.Snapshot,original.Id,assets,f=>f.GetBoundingBox().Minimum.Z>4.999)}},
                "source"=>recipe with{Options=options with{Source=original.Result}},
                "wrong-direction"=>recipe with{Options=options with{Side=ExtrusionSide.Negative}},
                "far-plane"=>recipe with{Options=options with{SurfaceBranch=ExtrusionSurfaceBranch.Far}},
                "foreign-document"=>recipe with{Options=options with{LimitFace=options.LimitFace! with{DocumentId=DocumentId.New()}}},
                "fingerprint"=>recipe with{Options=options with{LimitFace=options.LimitFace! with{Fingerprint=new string('0',64)}}},
                _=>recipe
            };
            var before=session.Snapshot;int count=assets.Count;long bytes=assets.SizeBytes;var history=session.HistoryUsage;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddExtrusionCommand(recipe,"Rejected",original.PartId,
                fault=="target"?session.Snapshot.Bodies.Values.Single(b=>b.Producer==original.Id).Id:null)));
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);Assert.Equal(bytes,assets.SizeBytes);Assert.Equal(history,session.HistoryUsage);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("plane")][InlineData("sphere")][InlineData("cylinder")][InlineData("island")]
    public async Task StoredIndependentAuthorRequiresExplicitCapabilityAndExactReplay(string kind)
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage(surfaceExtrusionResolver:kernel);
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Stored independent finite"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,kind=="island"?"plane":kind);
            if(kind=="island")recipe=recipe with{Profile=SketchProfile.FromCircle(new(5,5),2) with{Holes=[new(new(5,5),1)],Islands=[new([],new(new(5,5),.5))]}};
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Stored independent",session.Snapshot.Features[recipe.Options!.LimitFace!.FeatureId].PartId));
            var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);string path=files.PathFor("independent.cadoryx");await session.SaveAsync(storage,path);
            Assert.Contains(CadDocumentStorage.FiniteSurfaceNewBodyCapability,FormatEvolutionTests.Manifest(path).RequiredCapabilities);
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                loaded.Snapshot.Validate();var restored=(ExtrudeRecipe)loaded.Snapshot.Features[feature.Id].Recipe;
                Assert.Equal(recipe.Options,restored.Options);Assert.Equal(recipe.Placement,restored.Placement);
                Assert.Equal(JsonSerializer.Serialize(CadModelJson.Write(recipe.Profile),CadJson.Options),
                    JsonSerializer.Serialize(CadModelJson.Write(restored.Profile),CadJson.Options));
                Assert.Equal(feature.Result,loaded.Snapshot.Features[feature.Id].Result);Assert.Empty(loaded.Snapshot.Features[feature.Id].Inputs);
            }
            FormatEvolutionTests.RewriteManifest(path,m=>m with{RequiredCapabilities=m.RequiredCapabilities.Remove(CadDocumentStorage.FiniteSurfaceNewBodyCapability)});
            int count=assets.Count;long bytes=assets.SizeBytes;await Assert.ThrowsAsync<System.IO.InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Equal(count,assets.Count);Assert.Equal(bytes,assets.SizeBytes);
        }
        Assert.Equal(0,assets.Count);Assert.Equal(0,assets.SizeBytes);
    }
    [Fact]
    public async Task OriginalFiniteFuseProgramDoesNotAcquireNewBodyCapability()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage(surfaceExtrusionResolver:kernel);
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Original finite contract"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await FiniteSurfaceExtrusionTests.Seed(session,assets,"plane");var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Original",target.PartId,target.Id));string path=files.PathFor("original.cadoryx");await session.SaveAsync(storage,path);
            Assert.DoesNotContain(CadDocumentStorage.FiniteSurfaceNewBodyCapability,FormatEvolutionTests.Manifest(path).RequiredCapabilities);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task ProductionEditorCanPreviewFreeFrameWithoutSupportAndKeepsExplicitLimitSelection(bool negative)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Independent finite editor"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane",negative);var part=session.Snapshot.Features[recipe.Options!.LimitFace!.FeatureId].PartId;
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Independent",part));var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                await using var editor=new ExtrusionEditorViewModel(doc,kernel,feature.Id);Assert.False(editor.CanChooseSupport);Assert.Null(editor.SelectedSupport);
                await editor.LoadFacesCommand.ExecuteAsync(null);Assert.Null(editor.SelectedSurface);Assert.Null(editor.SelectedSupport);
                var before=session.Snapshot;await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);Assert.Same(before,session.Snapshot);
                editor.SelectedSurface=editor.Surfaces.Single(s=>s.Limit.Selection==recipe.Options.LimitFace);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);
                editor.SurfaceBranch=ExtrusionSurfaceBranch.Far;Assert.False(editor.CanConfirm);Assert.Null(doc.PreviewScene);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);Assert.Same(before,session.Snapshot);
                editor.SurfaceBranch=ExtrusionSurfaceBranch.Near;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
            }
            finally{doc.Detach();}
        }
        Assert.Equal(0,assets.Count);Assert.Equal(0,assets.SizeBytes);
    }
    [Fact]
    public async Task StructuredAuthorRetainsFreeFrameAndNeverInfersTargetOrSupport()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Independent finite tool"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var before=session.Snapshot;var part=before.Features[recipe.Options!.LimitFace!.FeatureId].PartId;
            var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                var tools=new CadAgentToolset(new CadCommandLineService(),doc);
                var author=new{type="extrude",section=new{profile=CadModelJson.Write(recipe.Profile),frame=new{origin=new[]{0d,0,6},normal=new[]{0d,0,1},u=new[]{1d,0,0}}},
                    operation="NewBody",termination="FiniteSurface",side="Positive",limitFace=CadDatumJson.Selection(recipe.Options.LimitFace!),surfaceBranch="Near"};
                string result=await tools.ExecuteAsync(new("independent-finite","cad_model",JsonSerializer.Serialize(new{action="create",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),
                    partId=part.ToString(),name="Independent tool",authorJson=JsonSerializer.Serialize(author,CadJson.Options)})),default);
                using(var parsed=JsonDocument.Parse(result))Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(),result);
                var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);Assert.Empty(feature.Inputs);
                var restored=(ExtrudeRecipe)feature.Recipe;Assert.Equal(recipe.Options,restored.Options);Assert.Equal(recipe.Placement,restored.Placement);
                var inspected=CadModelAuthoring.Author(session.Snapshot,feature);Assert.Equal("NewBody",inspected["operation"]);Assert.False(inspected.ContainsKey("input"));Assert.False(inspected.ContainsKey("supportFace"));
            }
            finally{doc.Detach();}
        }
        Assert.Equal(0,assets.Count);Assert.Equal(0,assets.SizeBytes);
    }
}
