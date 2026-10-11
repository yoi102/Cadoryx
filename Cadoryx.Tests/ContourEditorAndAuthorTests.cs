using System.Text.Json;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Toolboxes;
using OcctSharp;
using Xunit;
using DocumentSnapshot=Cadoryx.Db.DocumentSnapshot;
using Operation=Cadoryx.Db.LocalFeatureOperation;

namespace Cadoryx.Tests;

public sealed partial class ContourLocalTests
{
    [Theory][InlineData("constant")][InlineData("linear")][InlineData("smooth")][InlineData("sampled")][InlineData("spline")]
    public async Task CompleteLawEditorPreservesStableTablesAndAtomicPreviewCancelOrCommit(string mode)
    {
        await using var f=await Fixture.Create(true);var feature=await AddTestContour(f,mode);var recipe=(ContourLocalRecipe)feature.Recipe;FeatureId? requested=null;
        f.Document.ContourLocalEditRequested+=(_,id)=>requested=id;f.Document.EditFeature(feature.Id);Assert.Equal(feature.Id,requested);
        await using var editor=new ContourLocalEditorViewModel(f.Document,f.Kernel,Operation.Fillet,feature.Id);
        Assert.False(editor.CanChooseSource);var row=Assert.Single(editor.Programs);Assert.Equal(recipe.Programs[0].Id,row.Id);Assert.Equal(2,row.ChainMembers.Count());
        Assert.Equal(CadModelJson.Write<ContourRadiusLaw>(recipe.Programs[0].Radius!).GetRawText(),CadModelJson.Write<ContourRadiusLaw>(editor.BuildRecipe().Programs[0].Radius!).GetRawText());
        var before=f.Session.Snapshot;int count=f.Assets.Count;
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(before,f.Session.Snapshot);
        if(mode=="constant")row.Radius=.8;else if(mode=="linear")row.LastRadius=.8;else if(mode=="smooth")row.FirstDerivative=.3;
        else if(mode=="sampled")row.Samples[1].Radius=.8;else row.Poles[1].Radius=.8;
        Assert.False(editor.HasCandidate);Assert.Null(f.Document.PreviewScene);
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);var volume=f.Document.PreviewScene!.Items.Single().Geometry.VolumeMm3;
        editor.CancelCommand.Execute(null);Assert.Same(before,f.Session.Snapshot);Assert.False(editor.HasCandidate);Assert.Equal(count,f.Assets.Count);
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        var after=f.Session.Snapshot;Assert.Equal(volume,after.Features[feature.Id].Result.VolumeMm3,5);Assert.Equal(row.Id,((ContourLocalRecipe)after.Features[feature.Id].Recipe).Programs[0].Id);
        await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);await f.Session.RedoAsync();Assert.Same(after,f.Session.Snapshot);
    }
    [Fact]public async Task EditorDiscoversFullChainsRejectsDuplicatesAndSupportsExplicitTwoDistanceChamfer()
    {
        await using var f=await Fixture.Create();var before=f.Session.Snapshot;
        await using var editor=new ContourLocalEditorViewModel(f.Document,f.Kernel,Operation.Chamfer);Assert.Null(editor.SelectedSource);
        editor.SelectedSource=Assert.Single(editor.Sources);
        var seed=f.Select(ShapeKind.Edge,b=>Math.Abs(b.Maximum.X)<1e-5&&Math.Abs(b.Maximum.Y)<1e-5&&b.SizeZ>14.99);
        var face=f.Select(ShapeKind.Face,b=>Math.Abs(b.Maximum.X)<1e-5);
        await editor.PickExactAsync(seed);Assert.True(editor.WantsSupportFace);Assert.Empty(editor.Programs);
        await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.HasCandidate);
        await editor.PickExactAsync(face);Assert.False(editor.WantsSupportFace);var p=Assert.Single(editor.Programs);Assert.Equal(face,p.SupportFace);
        p.Distance=1;p.UseSecondDistance=true;p.SecondDistance=2;await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
        Assert.Equal(1785,f.Document.PreviewScene!.Items.Single().Geometry.VolumeMm3,5);editor.CancelCommand.Execute(null);
        await editor.PickExactAsync(seed);await editor.PickExactAsync(face);Assert.Single(editor.Programs);Assert.Contains(Cadoryx.Lang.Strings.Strings.ContourOverlap,editor.Status);
        editor.ReselectProgramCommand.Execute(null);await editor.PickExactAsync(seed);await editor.PickExactAsync(face);Assert.Equal(p.Id,editor.Programs.Single().Id);
        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
        Assert.Same(before,await UndoReturn());
        async Task<DocumentSnapshot> UndoReturn(){await f.Session.UndoAsync();return f.Session.Snapshot;}
    }
    [Fact]public async Task EachStaleContourMustBeExplicitlyReselectedAndSurvivingProgramDriversFollowIdsAfterReordering()
    {
        await using var f=await Fixture.Create();await using(var create=new ContourLocalEditorViewModel(f.Document,f.Kernel,Operation.Fillet))
        {
            create.SelectedSource=Assert.Single(create.Sources);await create.PickExactAsync(f.Select(ShapeKind.Edge,b=>Math.Abs(b.Maximum.X)<1e-5&&Math.Abs(b.Maximum.Y)<1e-5&&b.SizeZ>14.99));
            await create.PickExactAsync(f.Select(ShapeKind.Edge,b=>b.Minimum.X>9.99&&b.Minimum.Y>11.99&&b.SizeZ>14.99));
            create.Programs[0].Radius=.5;create.Programs[1].Radius=.8;await create.PreviewCommand.ExecuteAsync(null);Assert.True(create.CanConfirm,create.Status);await create.ConfirmCommand.ExecuteAsync(null);
        }
        var feature=f.Session.Snapshot.Features.Values.Single(v=>v.Recipe is ContourLocalRecipe);var recipe=(ContourLocalRecipe)feature.Recipe;
        var target=ParameterTarget.Feature(feature.Id,ContourParameterTargets.Property(recipe.Programs[0].Id,"Radius"));var param=new CadParameter(Guid.NewGuid(),"kept",ParameterDimension.Length,"0.5 mm");var binding=new ParameterBinding(Guid.NewGuid(),param.Id,target);
        await f.Session.ExecuteAsync(new SetParametersCommand(new([param],[binding])));
        await using(var edit=new ContourLocalEditorViewModel(f.Document,f.Kernel,Operation.Fillet,feature.Id))
        {
            edit.SelectedProgram=edit.Programs[0];edit.MoveProgramDownCommand.Execute(null);Assert.Equal(recipe.Programs[0].Id,edit.Programs[1].Id);
            await edit.PreviewCommand.ExecuteAsync(null);Assert.True(edit.CanConfirm,edit.Status);await edit.ConfirmCommand.ExecuteAsync(null);Assert.Equal(.5,ParameterTargets.Read(f.Session.Snapshot,target).Value);
        }
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));var frozen=f.Session.Snapshot;
        await using(var edit=new ContourLocalEditorViewModel(f.Document,f.Kernel,Operation.Fillet,feature.Id))
        {
            await edit.PreviewCommand.ExecuteAsync(null);Assert.False(edit.HasCandidate);
            edit.SelectedProgram=edit.Programs[0];edit.ReselectProgramCommand.Execute(null);await edit.PickExactAsync(f.Select(ShapeKind.Edge,b=>b.Minimum.X>10.99&&b.Minimum.Y>11.99&&b.SizeZ>14.99));
            await edit.PreviewCommand.ExecuteAsync(null);Assert.False(edit.HasCandidate);Assert.Same(frozen,f.Session.Snapshot);
            edit.SelectedProgram=edit.Programs[1];edit.ReselectProgramCommand.Execute(null);await edit.PickExactAsync(f.Select(ShapeKind.Edge,b=>Math.Abs(b.Maximum.X)<1e-5&&Math.Abs(b.Maximum.Y)<1e-5&&b.SizeZ>14.99));
            await edit.PreviewCommand.ExecuteAsync(null);Assert.True(edit.CanConfirm,edit.Status);await edit.ConfirmCommand.ExecuteAsync(null);Assert.False(f.Session.Snapshot.Features[feature.Id].IsStale);
            Assert.Equal(.5,ParameterTargets.Read(f.Session.Snapshot,target).Value);
        }
    }
    [Theory][InlineData("preview","cancel")][InlineData("preview","close")][InlineData("preview","state")][InlineData("discovery","cancel")][InlineData("discovery","close")][InlineData("discovery","state")]
    public async Task LateNativePreviewOrDiscoveryCannotOutliveEditorOrCapturedState(string phase,string action)
    {
        await using var f=await Fixture.Create();var gate=new ContourGatedKernel{Discovery=phase=="discovery"};await using var editor=new ContourLocalEditorViewModel(f.Document,gate,Operation.Fillet);
        editor.SelectedSource=Assert.Single(editor.Sources);var pick=f.Select(ShapeKind.Edge,b=>Math.Abs(b.Maximum.X)<1e-5&&Math.Abs(b.Maximum.Y)<1e-5&&b.SizeZ>14.99);Task work;
        if(phase=="discovery"){gate.Gate=true;work=editor.PickExactAsync(pick);}
        else{await editor.PickExactAsync(pick);gate.Gate=true;work=editor.PreviewCommand.ExecuteAsync(null);}
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));var before=f.Session.Snapshot;int count=f.Assets.Count;Task? close=null;
        if(action=="cancel")editor.CancelCommand.Execute(null);else if(action=="close")close=editor.DisposeAsync().AsTask();
        else await f.Session.ExecuteAsync(new EditDocumentCommand("Change",s=>s with{Name="Changed"}));
        var expected=f.Session.Snapshot;gate.Release.TrySetResult();await work;if(close is not null)await close;
        Assert.False(editor.HasCandidate);Assert.Null(f.Document.PreviewScene);Assert.Same(expected,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
        if(phase=="discovery")Assert.Empty(editor.Programs);if(action!="state")Assert.Same(before,f.Session.Snapshot);
    }
    [Theory][InlineData("constant")][InlineData("linear")][InlineData("smooth")][InlineData("sampled")][InlineData("spline")]
    public async Task StructuredContourAuthorDiscoversReadOnlyAndReturnsReusableNoOpEdit(string mode)
    {
        await using var f=await Fixture.Create(true);var tools=new CadAgentToolset(new CadCommandLineService(),f.Document);var before=f.Session.Snapshot;
        var seed=f.Select(ShapeKind.Edge,b=>Math.Abs(b.Maximum.X)<1e-5&&Math.Abs(b.Maximum.Y)<1e-5&&b.SizeZ>7.49);var program=Guid.NewGuid();
        string author=JsonSerializer.Serialize(new{type="contour_local",input=CadModelJson.Input(f.Source),operation="Fillet",programs=new[]{new{id=program,seed=CadDatumJson.Selection(seed),radius=CadModelJson.Write<ContourRadiusLaw>(TestLaw(mode))}}});
        var args=new{action="inspect_contours",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),partId=f.Source.PartId.ToString(),authorJson=author,limit=1};
        string result=await tools.ExecuteAsync(new("discover","cad_model",JsonSerializer.Serialize(args)),default);using(var parsed=JsonDocument.Parse(result))
        {Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(),result);Assert.Equal(2,parsed.RootElement.GetProperty("total").GetInt32());Assert.True(parsed.RootElement.GetProperty("hasMore").GetBoolean());Assert.Equal(15,parsed.RootElement.GetProperty("contours")[0].GetProperty("length").GetDouble(),5);}
        Assert.Same(before,f.Session.Snapshot);int count=f.Assets.Count;
        result=await tools.ExecuteAsync(new("create","cad_model",JsonSerializer.Serialize(new{action="create",documentId=before.Id.ToString(),stateId=before.StateId.ToString(),partId=f.Source.PartId.ToString(),name="Tool contour",authorJson=author})),default);
        using(var parsed=JsonDocument.Parse(result)){Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(),result);Assert.Equal(program.ToString(),parsed.RootElement.GetProperty("createdProgramIds")[0].GetString());}
        var after=f.Session.Snapshot;var feature=after.Features.Values.Single(p=>p.Recipe is ContourLocalRecipe);
        result=await tools.ExecuteAsync(new("query","cad_inspect",JsonSerializer.Serialize(new{area="recipes",id=feature.Id.ToString()})),default);string edit;
        using(var parsed=JsonDocument.Parse(result)){var item=parsed.RootElement.GetProperty("items")[0];Assert.True(item.GetProperty("available").GetBoolean());Assert.Equal("contour_local",item.GetProperty("author").GetProperty("type").GetString());edit=item.GetProperty("edit").GetRawText();}
        count=f.Assets.Count;var history=f.Session.HistoryUsage;result=await tools.ExecuteAsync(new("noop","cad_model",edit),default);
        using(var parsed=JsonDocument.Parse(result)){Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(),result);Assert.False(parsed.RootElement.GetProperty("changed").GetBoolean());}
        Assert.Same(after,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);Assert.Equal(history,f.Session.HistoryUsage);
    }
    [Fact]public void ContourContractIsPagedAndContainsOnlyExactAuthorInputs()
    {
        var offset=0;var definitions=new HashSet<string>();
        do
        {
            var json=CadModelContract.Read("contour_local",offset,50);Assert.True(json.Length<=5500);using var parsed=JsonDocument.Parse(json);var root=parsed.RootElement;
            Assert.DoesNotContain("chain",root.GetProperty("schema").GetRawText(),StringComparison.OrdinalIgnoreCase);
            foreach(var p in root.GetProperty("schema").GetProperty("$defs").EnumerateObject())definitions.Add(p.Name);
            if(!root.GetProperty("hasMore").GetBoolean())break;int next=root.GetProperty("nextOffset").GetInt32();Assert.True(next>offset);offset=next;
        }while(true);
        Assert.Contains("edge_selection",definitions);Assert.Contains("sampled_radius",definitions);Assert.Contains("spline_radius",definitions);
    }
    [Fact]public async Task StructuredReselectionUsesFreshExplicitSeedsAndRejectsUnreviewedChainCaches()
    {
        await using var f=await Fixture.Create();var feature=await AddTestContour(f,"linear");var tools=new CadAgentToolset(new CadCommandLineService(),f.Document);
        await f.Session.ExecuteAsync(new RecomputeCommand(f.Source.Id,new BoxRecipe(11,12,15,RigidTransform3d.Identity)));var before=f.Session.Snapshot;int count=f.Assets.Count;
        var seed=f.Select(ShapeKind.Edge,b=>Math.Abs(b.Maximum.X)<1e-5&&Math.Abs(b.Maximum.Y)<1e-5&&b.SizeZ>14.99);var old=((ContourLocalRecipe)feature.Recipe).Programs[0];
        var program=new Dictionary<string,object>{["id"]=old.Id,["seed"]=CadDatumJson.Selection(seed),["radius"]=CadModelJson.Write<ContourRadiusLaw>(old.Radius!),["chain"]=old.Chain};
        var author=new{type="contour_local",input=CadModelJson.Input(before.Features[f.Source.Id]),operation="Fillet",programs=new[]{program}};
        async Task<string> Run(DocumentSnapshot state,string action)=>await tools.ExecuteAsync(new("reselect","cad_model",JsonSerializer.Serialize(new{action,documentId=state.Id.ToString(),stateId=state.StateId.ToString(),id=feature.Id.ToString(),revision=state.Features[feature.Id].Result.Revision.ToString(),authorJson=JsonSerializer.Serialize(author)})),default);
        var json=await Run(before,"reselect");using(var parsed=JsonDocument.Parse(json))Assert.False(parsed.RootElement.GetProperty("success").GetBoolean(),json);
        Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);program.Remove("chain");
        json=await Run(before,"edit");using(var parsed=JsonDocument.Parse(json))Assert.False(parsed.RootElement.GetProperty("success").GetBoolean(),json);Assert.Same(before,f.Session.Snapshot);
        json=await Run(before,"reselect");using(var parsed=JsonDocument.Parse(json))Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(),json);
        Assert.False(f.Session.Snapshot.Features[feature.Id].IsStale);Assert.Equal(old.Id,((ContourLocalRecipe)f.Session.Snapshot.Features[feature.Id].Recipe).Programs[0].Id);
        var after=f.Session.Snapshot;json=await Run(before,"reselect");using(var parsed=JsonDocument.Parse(json))Assert.False(parsed.RootElement.GetProperty("success").GetBoolean(),json);Assert.Same(after,f.Session.Snapshot);
    }
    private sealed class ContourGatedKernel:IGeometryKernel,IContourLocalResolver
    {
        private readonly OcctGeometryKernel inner=new();internal bool Gate{get;set;}internal bool Discovery{get;init;}
        internal TaskCompletionSource Started{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);internal TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>inner.Version;public bool Supports(GeometryRecipe r)=>inner.Supports(r);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe r,IAssetStore a,CancellationToken t=default)
        {if(Gate&&!Discovery&&r is ContourLocalRecipe){Started.TrySetResult();await Release.Task;return await inner.EvaluateAsync(r,a,CancellationToken.None);}return await inner.EvaluateAsync(r,a,t);}
        public async Task<ContourChainEvidence> DiscoverContourAsync(DocumentSnapshot d,ExactTopologySelection seed,Operation o,ExactTopologySelection? face,IAssetStore a,CancellationToken t=default)
        {if(Gate&&Discovery){Started.TrySetResult();await Release.Task;return await inner.DiscoverContourAsync(d,seed,o,face,a,CancellationToken.None);}return await inner.DiscoverContourAsync(d,seed,o,face,a,t);}
        public Task ConfirmContourRecipeAsync(ContourLocalRecipe r,IAssetStore a,CancellationToken t=default)=>inner.ConfirmContourRecipeAsync(r,a,t);
        public Task<LoadedDocument> ImportAsync(string p,IAssetStore a,CancellationToken t=default)=>inner.ImportAsync(p,a,t);
        public Task ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CancellationToken t=default)=>inner.ExportAsync(s,a,p,t);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot s,IAssetStore a,string p,CadExportOptions o,CancellationToken t=default)=>inner.ExportAsync(s,a,p,o,t);
    }
}
