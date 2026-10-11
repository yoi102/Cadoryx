using System.Collections.Immutable;
using System.Text.Json;
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
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;

namespace Cadoryx.Tests;

public sealed partial class FiniteSurfaceExtrusionTests
{
    [Theory][InlineData("reverse",612)][InlineData("cut",1872)]
    public async Task NegativeDirectionalFuseAndCutPreserveAuthoredVolumes(string kind,double expected)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Reverse material"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,kind);var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Reverse",target.PartId,target.Id));
            Assert.InRange(Math.Abs(expected-session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe).Result.VolumeMm3),0,1e-5);
        }
        Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("missing-support")][InlineData("missing-branch")][InlineData("far-plane")][InlineData("foreign-support")][InlineData("wrong-start")][InlineData("wrong-direction")]
    public async Task InvalidSurfaceAuthorCannotChangeStateOrRetainAssets(string kind)
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Rejected surface author"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var o=recipe.Options!;
            recipe=kind switch
            {
                "missing-support"=>recipe with{Options=o with{SupportFace=null}},
                "missing-branch"=>recipe with{Options=o with{SurfaceBranch=null}},
                "far-plane"=>recipe with{Options=o with{SurfaceBranch=ExtrusionSurfaceBranch.Far}},
                "foreign-support"=>recipe with{Options=o with{SupportFace=o.SupportFace! with{Fingerprint=new string('0',64)}}},
                "wrong-direction"=>recipe with{Options=o with{Side=ExtrusionSide.Negative}},
                _=>recipe with{Placement=RigidTransform3d.Translate(0,0,6)}
            };
            var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==o.Source);var before=session.Snapshot;
            int count=assets.Count;var history=session.HistoryUsage;
            await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new AddExtrusionCommand(recipe,"Rejected",target.PartId,target.Id)));
            Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);Assert.Equal(history,session.HistoryUsage);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact]public async Task ExactSupportRevisionChangeFreezesAndRequiresExplicitNewSelection()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Support revision"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var o=recipe.Options!;var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==o.Source);
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Until",target.PartId,target.Id));
            var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            await session.ExecuteAsync(new RecomputeCommand(o.SupportFace!.FeatureId,new BoxRecipe(10,10,6,RigidTransform3d.Identity)));
            Assert.True(session.Snapshot.Features[feature.Id].IsStale);Assert.Equal(feature.Result,session.Snapshot.Features[feature.Id].Result);
            var source=session.Snapshot.Features[o.SupportFace.FeatureId];
            var support=Pick(session.Snapshot,source.Id,assets,f=>f.GetBoundingBox().Minimum.Z>5.999);
            await session.ExecuteAsync(new RecomputeCommand(feature.Id,recipe with{Placement=RigidTransform3d.Translate(0,0,6),Options=o with{Source=source.Result,SupportFace=support}}));
            Assert.False(session.Snapshot.Features[feature.Id].IsStale);
            Assert.InRange(Math.Abs(696-session.Snapshot.Features[feature.Id].Result.VolumeMm3),0,1e-5);
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact]public async Task StorageRequiresReplayAndIndependentLegacySectionGuard()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        var storage=new CadDocumentStorage(surfaceExtrusionResolver:kernel);
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Stored surface"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"sphere");var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Until",target.PartId,target.Id));
            string path=files.PathFor("surface.cadoryx");await session.SaveAsync(storage,path);
            await Assert.ThrowsAsync<NotSupportedException>(()=>new CadDocumentStorage().LoadAsync(path,assets));
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                loaded.Snapshot.Validate();var saved=(ExtrudeRecipe)loaded.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe).Recipe;
                Assert.Equal(recipe.Options,saved.Options);Assert.Equal(recipe.Placement,saved.Placement);
            }
            var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);
            Assert.Equal(43,manifest.Sections.Single(s=>s.Kind=="features").SchemaVersion);Assert.Contains("cadoryx.finite-surface-extrusion.1",manifest.RequiredCapabilities);
            FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="features"?s with{SchemaVersion=41}:s).ToImmutableArray()});
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact]public async Task StoredSameVolumeForeignMaterialFailsExactReplay()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Foreign result"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");
            using var foreign=await kernel.EvaluateAsync(new BoxRecipe(6,6,17,RigidTransform3d.Translate(100,0,0)),assets);
            Assert.Equal(612,foreign.Geometry.VolumeMm3,5);
            await Assert.ThrowsAsync<CadValidationException>(()=>kernel.ConfirmFiniteSurfaceExtrusionAsync(recipe,foreign.Geometry,assets));
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact]public async Task ProductionEditorEnumeratesExplicitChoicesAndBranchChangeInvalidatesPreview()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Surface editor"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var target=session.Snapshot.Bodies.Values.Single(b=>b.Geometry==recipe.Options!.Source);
            await session.ExecuteAsync(new AddExtrusionCommand(recipe,"Until",target.PartId,target.Id));
            var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);
            var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                await using var editor=new ExtrusionEditorViewModel(doc,kernel,feature.Id);
                Assert.Equal(ExtrusionTermination.FiniteSurface,editor.Termination);Assert.Equal(recipe.Options!.SupportFace,editor.SelectedSupport!.Limit.Selection);
                await editor.LoadFacesCommand.ExecuteAsync(null);Assert.Null(editor.SelectedSurface);Assert.Null(editor.SelectedSupport);
                editor.SelectedSurface=editor.Surfaces.Single(s=>s.Limit.Selection==recipe.Options.LimitFace);
                editor.SelectedSupport=editor.Supports.Single(s=>s.Limit.Selection==recipe.Options.SupportFace);
                var before=session.Snapshot;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,session.Snapshot);
                editor.SurfaceBranch=ExtrusionSurfaceBranch.Far;Assert.False(editor.CanConfirm);Assert.Null(doc.PreviewScene);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);Assert.Same(before,session.Snapshot);
                editor.SurfaceBranch=ExtrusionSurfaceBranch.Near;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
            }
            finally{doc.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }
    [Fact]public async Task StructuredSurfaceAuthorPreservesBothSelectionsAndExplicitBranch()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Surface tool"),assets,kernel,new InlineSessionDispatcher()))
        {
            var recipe=await Seed(session,assets,"plane");var before=session.Snapshot;var source=before.Features[recipe.Options!.SupportFace!.FeatureId];
            var doc=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                var tools=new CadAgentToolset(new CadCommandLineService(),doc);
                var author=new{type="extrude",section=new{profile=CadModelJson.Write(recipe.Profile),frame=new{origin=new[]{0d,0,5},normal=new[]{0d,0,1},u=new[]{1d,0,0}}},
                    operation="Fuse",termination="FiniteSurface",side="Positive",input=CadModelJson.Input(source),limitFace=CadDatumJson.Selection(recipe.Options.LimitFace!),
                    supportFace=CadDatumJson.Selection(recipe.Options.SupportFace!),surfaceBranch="Near"};
                string result=await tools.ExecuteAsync(new("surface","cad_model",JsonSerializer.Serialize(new{action="create",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),
                    partId=source.PartId.ToString(),name="Surface tool",authorJson=JsonSerializer.Serialize(author,CadJson.Options)})),default);
                using(var parsed=JsonDocument.Parse(result))Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(),result);
                var feature=session.Snapshot.Features.Values.Single(f=>f.Recipe is ExtrudeRecipe);Assert.Equal(recipe.Options,((ExtrudeRecipe)feature.Recipe).Options);
                var inspected=CadModelAuthoring.Author(session.Snapshot,feature);Assert.Equal("Near",inspected["surfaceBranch"]);
                using var schema=JsonDocument.Parse(CadModelContract.Read("extrude"));
                var properties=schema.RootElement.GetProperty("schema").GetProperty("properties");Assert.True(properties.TryGetProperty("supportFace",out _));Assert.True(properties.TryGetProperty("surfaceBranch",out _));
            }
            finally{doc.Detach();}
        }
        Assert.Equal(0,assets.Count);
    }
}
