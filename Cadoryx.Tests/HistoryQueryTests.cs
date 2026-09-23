using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class HistoryQueryTests
{
    [Theory]
    [InlineData("en-US","History diagnostics","Resolved")]
    [InlineData("zh-CN","历史诊断","已解析")]
    [InlineData("ja-JP","履歴診断","解決済み")]
    public void QueryWindowLabelsAndAllResolutionStatesHaveThreeLanguages(string culture,string title,string resolved)
    {
        var previous=System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture=System.Globalization.CultureInfo.GetCultureInfo(culture);
            var labels=new HistoryQueryLabels();Assert.Equal(title,labels.Title);
            Assert.Equal(resolved,labels.Resolution(HistoryResolutionStatus.Resolved));
            foreach(var status in Enum.GetValues<HistoryResolutionStatus>())
                Assert.NotEqual("HistoryStatus"+status,labels.Resolution(status));
            Assert.NotEqual("HistoryQueryReselect",labels.Reselect);
            Assert.NotEqual("HistoryQuerySteps",labels.Steps);
        }
        finally{System.Globalization.CultureInfo.CurrentUICulture=previous;}
    }

    [Fact] public async Task QueryOwnsItsSourceAndRecalculatesAfterSaveUndoAndReopen()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();var storage=new CadDocumentStorage();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Queries"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Source"));
            var source=session.Snapshot.Features.Values.Single();
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(2,22,32,RigidTransform3d.Translate(4,-1,-1)),"Tool",source.PartId));
            var tool=session.Snapshot.Features.Values.Single(f=>f.Name=="Tool");
            await session.ExecuteAsync(new BooleanCommand(BooleanOperation.Cut,[source.OutputBodyId,tool.OutputBodyId]));
            var cut=session.Snapshot.Features.Values.Single(f=>f.Recipe is BooleanRecipe);
            var reference=TopologyReference.Box(session.Snapshot,source.Id,BoxBoundary.XMin);
            await session.ExecuteAsync(new UpsertTopologyReferenceCommand(reference));
            var query=new HistoryQuery(HistoryQueryId.New(),"Track left face",reference,cut.Id);
            await session.ExecuteAsync(new UpsertHistoryQueryCommand(query));
            Assert.Equal(query,session.Snapshot.HistoryQueries[query.Id]);
            await session.ExecuteAsync(new RemoveTopologyReferenceCommand(reference.Id));
            Assert.Empty(session.Snapshot.TopologyReferences);
            var inspection=await TopologyHistoryInspection.InspectAsync(session.Snapshot,query.Source,query.TargetFeatureId,assets,kernel);
            Assert.Equal(HistoryResolutionStatus.Resolved,inspection.Result.Status);
            Assert.Equal(1,inspection.Result.CompletedSteps);
            string path=files.PathFor("queries.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))
            {
                Assert.Equal(query,loaded.Snapshot.HistoryQueries[query.Id]);
                Assert.Empty(loaded.Snapshot.TopologyReferences);
                Assert.Equal(inspection.Result,await kernel.TraceAsync(loaded.Snapshot,query.Source,query.TargetFeatureId,assets));
                Assert.Equal(10,FormatEvolutionTests.Manifest(path).Sections.Length);
            }
            await session.ExecuteAsync(new RemoveHistoryQueryCommand(query.Id));Assert.Empty(session.Snapshot.HistoryQueries);
            await session.UndoAsync();Assert.Equal(query,session.Snapshot.HistoryQueries[query.Id]);
            await session.RedoAsync();Assert.Empty(session.Snapshot.HistoryQueries);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task MissingEndpointsRemainVisibleWhileMalformedQuerySectionIsRejected()
    {
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        var snapshot=DocumentSnapshot.Create("Missing history");
        var source=new TopologyReference(TopologyReferenceId.New(),snapshot.Id,FeatureId.New(),BodyId.New(),GeometryRevisionId.New(),TopologyKind.Face,BoxBoundary.XMin);
        var query=new HistoryQuery(HistoryQueryId.New(),"Lost endpoint",source,FeatureId.New());
        snapshot=snapshot with{HistoryQueries=snapshot.HistoryQueries.Add(query.Id,query)};
        string path=files.PathFor("missing.cadoryx");await storage.SaveAsync(snapshot,assets,path);
        using(var loaded=await storage.LoadAsync(path,assets))
        {
            Assert.Equal(query,loaded.Snapshot.HistoryQueries[query.Id]);
            var result=await new OcctGeometryKernel().TraceAsync(loaded.Snapshot,query.Source,query.TargetFeatureId,assets);
            Assert.Equal(HistoryResolutionStatus.Missing,result.Status);Assert.Null(result.Target);
        }
        FormatEvolutionTests.RewriteSection(path,"history-queries",bytes=>
        {
            var data=MessagePackSerializer.Deserialize<PackHistoryQueries>(bytes);
            return MessagePackSerializer.Serialize(data with{Queries=[data.Queries[0] with{Source=data.Queries[0].Source with{Policy=0}}]});
        });
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        Assert.Equal(snapshot.Settings,await storage.ReadSettingsAsync(path));Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task ExistingFileMigratesToEmptyQueryRegistryWithoutChangingSource()
    {
        var original=Path.Combine(AppContext.BaseDirectory,"Fixtures","Storage","m4s2-linked.cadoryx");
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(original)));
        using var files=new TestFiles();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        using(var loaded=await storage.LoadAsync(original,assets))
        {
            Assert.Empty(loaded.Snapshot.HistoryQueries);Assert.Contains(loaded.Diagnostics,d=>d.Code=="IO.MIGRATED");
            string migrated=files.PathFor("migrated.cadoryx");await storage.SaveAsync(loaded.Snapshot,assets,migrated);
            using var next=await storage.LoadAsync(migrated,assets);
            Assert.Empty(next.Snapshot.HistoryQueries);Assert.Equal(loaded.Snapshot.StateId,next.Snapshot.StateId);
            Assert.Equal(10,FormatEvolutionTests.Manifest(migrated).Sections.Length);
        }
        Assert.Equal(hash,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(original))));
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task EditorRequiresExplicitSourceReselectionAndClearsOldResultOnDocumentChange()
    {
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Editor"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Source"));
            var feature=session.Snapshot.Features.Values.Single();
            var source=TopologyReference.Box(session.Snapshot,feature.Id,BoxBoundary.XMin);
            await session.ExecuteAsync(new UpsertTopologyReferenceCommand(source));
            await using var editor=new HistoryQueryViewModel(session,kernel);
            editor.SelectedSource=editor.Sources.Single();editor.SelectedTarget=editor.Targets.Single();editor.QueryName="One";
            Assert.Equal(editor.Labels.NoSource,editor.DraftSourceLabel);
            editor.ReselectSourceCommand.Execute(null);
            Assert.Contains(source.Id.Value.ToString("N")[..8],editor.DraftSourceLabel);
            await editor.SaveCommand.ExecuteAsync(null);Assert.Single(session.Snapshot.HistoryQueries);
            await editor.AnalyzeCommand.ExecuteAsync(null);
            Assert.NotEmpty(editor.ResultSummary);
            await session.ExecuteAsync(new RemoveTopologyReferenceCommand(source.Id));
            Assert.Empty(editor.ResultSummary);Assert.Equal(source,editor.SelectedQuery!.Source);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact] public async Task PendingResultCannotOverwriteAChangedQuery()
    {
        var assets=new MemoryAssetStore();var kernel=new GatedKernel();
        await using(var session=new CadDocumentSession(DocumentSnapshot.Create("Pending"),assets,kernel,new InlineSessionDispatcher()))
        {
            await session.ExecuteAsync(new AddBodyCommand(new BoxRecipe(10,20,30,RigidTransform3d.Identity),"Source"));
            var feature=session.Snapshot.Features.Values.Single();
            var source=TopologyReference.Box(session.Snapshot,feature.Id,BoxBoundary.XMin);
            var query=new HistoryQuery(HistoryQueryId.New(),"Before",source,FeatureId.New());
            await session.ExecuteAsync(new UpsertHistoryQueryCommand(query));
            await using var editor=new HistoryQueryViewModel(session,kernel);
            editor.SelectedQuery=editor.Queries.Single();
            var pending=editor.AnalyzeCommand.ExecuteAsync(null);
            await kernel.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await session.ExecuteAsync(new UpsertHistoryQueryCommand(query with{Name="After"}));
            kernel.Release.TrySetResult();await pending;
            Assert.Empty(editor.ResultSummary);Assert.Empty(editor.Steps);
            Assert.Equal("After",editor.SelectedQuery!.Name);
        }
        Assert.Equal(0,assets.Count);
    }

    private sealed class GatedKernel:IGeometryKernel,ITopologyHistoryResolver
    {
        private readonly OcctGeometryKernel inner=new();
        public TaskCompletionSource Started {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>inner.Version;
        public bool Supports(GeometryRecipe recipe)=>inner.Supports(recipe);
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)=>inner.EvaluateAsync(recipe,assets,token);
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>inner.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token=default)=>inner.ExportAsync(snapshot,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>inner.ExportAsync(snapshot,assets,path,options,token);
        public async Task<HistoryResolution> TraceAsync(DocumentSnapshot snapshot,TopologyReference source,FeatureId target,IAssetStore assets,CancellationToken token=default)
        {
            Started.TrySetResult();await Release.Task.WaitAsync(token);
            return await inner.TraceAsync(snapshot,source,target,assets,token);
        }
    }
}
