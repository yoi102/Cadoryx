using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace Cadoryx.Tests;

[Collection("IsolatedNativeDeadlines")]
public sealed class SketchExpressionCurvedRaceTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> LateCases()=>from kind in CurvedConsumerTestScene.Kinds
        from reverse in new[]{false,true} from tilted in new[]{false,true} from parameter in new[]{false,true}
        from afterNative in new[]{false,true} from boundary in new[]{"cancel","dispose","detach","generation"}
        select new object[]{kind,reverse,tilted,parameter,afterNative,boundary};
    [Theory,MemberData(nameof(LateCases))]
    public async Task LateCurvedNativeResultCannotPublishAfterEditorOrDocumentInvalidation(string kind,bool reverse,bool tilted,bool parameter,bool afterNative,string boundary)
    {
        await using var f=await CurvedConsumerTestScene.Create(kind,reverse,tilted,parameter);
        var kernel=new HeldKernel(f.Kernel,parameter?f.EditedVolume:f.OriginalVolume){Hold=true,AfterNative=afterNative};
        var before=f.Session.Snapshot;int count=f.Assets.Count;long generation=f.Session.Generation;
        GuidedEditorViewModel? guided=null;ParameterEditorViewModel? parameters=null;Task? pending=null,closing=null;
        try
        {
            if(parameter){parameters=f.Parameters(kernel);pending=parameters.PreviewCommand.ExecuteAsync(null);}
            else{guided=f.Guided(kernel);pending=guided.PreviewCommand.ExecuteAsync(null);}
            await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(afterNative?count+1:count,f.Assets.Count);Assert.Null(f.Document.PreviewScene);Assert.Same(before,f.Session.Snapshot);
            switch(boundary)
            {
                case "cancel":if(parameter)parameters!.CancelCommand.Execute(null);else guided!.CancelCommand.Execute(null);break;
                case "dispose":closing=parameter?parameters!.DisposeAsync().AsTask():guided!.DisposeAsync().AsTask();break;
                case "detach":f.Document.Detach();break;
                case "generation":await f.Session.ExecuteAsync(DocumentEdits.SetGrid(before.Settings.Grid with{SpacingMm=17}));break;
            }
            var current=f.Session.Snapshot;
            if(boundary=="generation"){Assert.NotSame(before,current);Assert.Equal(generation+1,f.Session.Generation);}
            else{Assert.Same(before,current);Assert.Equal(generation,f.Session.Generation);}
            CurvedConsumerTestScene.SameAuthors(before,current);
            if(afterNative)using(f.Assets.Acquire(kernel.Produced!.AssetId)){Assert.Equal(count+1,f.Assets.Count);}
            kernel.Release.TrySetResult();await pending;if(closing is not null)await closing;
            Assert.Equal(1,kernel.NativeCalls);Assert.NotNull(kernel.Produced);Assert.True(kernel.NativeVerified);
            Assert.Same(current,f.Session.Snapshot);Assert.Null(f.Document.PreviewScene);Assert.Equal(count,f.Assets.Count);
            if(parameter){Assert.False(parameters!.CanConfirm);Assert.False(parameters.ConfirmCommand.CanExecute(null));}
            else{Assert.False(guided!.CanConfirm);Assert.False(guided.ConfirmCommand.CanExecute(null));}
            if(boundary=="generation"){await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);}
            output.WriteLine($"kind={kind};reverse={reverse};tilted={tilted};parameter={parameter};afterNative={afterNative};boundary={boundary};nativeVolume={kernel.Produced.VolumeMm3:R}");
        }
        finally
        {
            kernel.Release.TrySetResult();if(pending is not null)await pending;
            if(parameters is not null)await parameters.DisposeAsync();if(guided is not null)await guided.DisposeAsync();
        }
    }
    public static IEnumerable<object[]> PreparedCases()=>from kind in CurvedConsumerTestScene.Kinds
        from reverse in new[]{false,true} from tilted in new[]{false,true} from parameter in new[]{false,true}
        select new object[]{kind,reverse,tilted,parameter};
    [Theory,MemberData(nameof(PreparedCases))]
    public async Task PreparedCurvedPreviewReleasesOnGenerationChangeAndCannotCommit(string kind,bool reverse,bool tilted,bool parameter)
    {
        await using var f=await CurvedConsumerTestScene.Create(kind,reverse,tilted,parameter);
        var kernel=new HeldKernel(f.Kernel,parameter?f.EditedVolume:f.OriginalVolume);var before=f.Session.Snapshot;int count=f.Assets.Count;
        GuidedEditorViewModel? guided=null;ParameterEditorViewModel? parameters=null;
        try
        {
            if(parameter){parameters=f.Parameters(kernel);await parameters.PreviewCommand.ExecuteAsync(null);Assert.True(parameters.CanConfirm,parameters.Status);}
            else{guided=f.Guided(kernel);await guided.PreviewCommand.ExecuteAsync(null);Assert.True(guided.CanConfirm,guided.Status);}
            Assert.NotNull(f.Document.PreviewScene);Assert.Equal(count+1,f.Assets.Count);Assert.Same(before,f.Session.Snapshot);Assert.True(kernel.NativeVerified);
            await f.Session.ExecuteAsync(DocumentEdits.SetGrid(before.Settings.Grid with{SpacingMm=17}));var current=f.Session.Snapshot;
            Assert.Null(f.Document.PreviewScene);Assert.Equal(count,f.Assets.Count);CurvedConsumerTestScene.SameAuthors(before,current);
            if(parameter){Assert.True(parameters!.IsStale);Assert.False(parameters.CanConfirm);Assert.False(parameters.ConfirmCommand.CanExecute(null));}
            else{Assert.True(guided!.IsStale);Assert.False(guided.CanConfirm);Assert.False(guided.ConfirmCommand.CanExecute(null));}
            Assert.Same(current,f.Session.Snapshot);await f.Session.UndoAsync();Assert.Same(before,f.Session.Snapshot);
        }
        finally{if(parameters is not null)await parameters.DisposeAsync();if(guided is not null)await guided.DisposeAsync();}
    }
    public static IEnumerable<object[]> InputCases()=>from kind in CurvedConsumerTestScene.Kinds
        from reverse in new[]{false,true} from tilted in new[]{false,true} from afterNative in new[]{false,true}
        select new object[]{kind,reverse,tilted,afterNative};
    [Theory,MemberData(nameof(InputCases))]
    public async Task PendingC1TextWhileCurvedNativeResultIsHeldInvalidatesThatResult(string kind,bool reverse,bool tilted,bool afterNative)
    {
        await using var f=await CurvedConsumerTestScene.Create(kind,reverse,tilted,true);
        var kernel=new HeldKernel(f.Kernel,f.EditedVolume){Hold=true,AfterNative=afterNative};var before=f.Session.Snapshot;int count=f.Assets.Count;
        await using var editor=f.Parameters(kernel);Task pending=editor.PreviewCommand.ExecuteAsync(null);
        try
        {
            await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));editor.Expression="0.05 mm";Assert.True(editor.HasPendingInput);
            kernel.Release.TrySetResult();await pending;Assert.True(kernel.NativeVerified);Assert.False(editor.CanConfirm);Assert.Null(f.Document.PreviewScene);
            Assert.Same(before,f.Session.Snapshot);Assert.Equal(count,f.Assets.Count);
            kernel.Hold=false;await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);Assert.Equal(1,kernel.NativeCalls);
            Assert.Same(before,f.Session.Snapshot);CurvedConsumerTestScene.SameAuthors(before,f.Session.Snapshot);
        }
        finally{kernel.Release.TrySetResult();await pending;}
    }
    private sealed class HeldKernel(OcctGeometryKernel native,double expectedVolume):IGeometryKernel,IExactSketchExpressionGeometry
    {
        internal bool Hold{get;set;}
        internal bool AfterNative{get;set;}
        internal int NativeCalls{get;private set;}
        internal GeometryAssetRef? Produced{get;private set;}
        internal bool NativeVerified{get;private set;}
        internal TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Version=>native.Version;
        public bool Supports(GeometryRecipe r)=>native.Supports(r);
        public async Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)
        {
            Assert.True(recipe is SweepRecipe or LoftRecipe,"This gate must exercise actual curved guided results");GeometryResult? result=null;
            try
            {
                if(Hold&&!AfterNative){Entered.TrySetResult();await Release.Task;}
                // Deliberately model native work whose completion cannot be
                // revoked. The production owner must reject/release it later.
                result=await native.EvaluateAsync(recipe,assets,CancellationToken.None);NativeCalls++;Produced=result.Geometry;
                CurvedConsumerTestScene.Verify(assets,result.Geometry,expectedVolume);NativeVerified=true;
                if(Hold&&AfterNative){Entered.TrySetResult();await Release.Task;}
                return result;
            }
            catch(Exception ex){result?.Dispose();Entered.TrySetException(ex);throw;}
        }
        public Task<LoadedDocument> ImportAsync(string p,IAssetStore a,CancellationToken t=default)=>native.ImportAsync(p,a,t);
        public Task ExportAsync(DocumentSnapshot d,IAssetStore a,string p,CancellationToken t=default)=>native.ExportAsync(d,a,p,t);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot d,IAssetStore a,string p,CadExportOptions o,CancellationToken t=default)=>native.ExportAsync(d,a,p,o,t);
        public ExactExpressionRegularity InspectExpressionRegularity(SketchCurveExpressionGeometry e)=>native.InspectExpressionRegularity(e);
        public SketchCurveExpressionGeometry PrepareExpression(SketchCurveExpressionGeometry e)=>native.PrepareExpression(e);
        public SketchExpressionEvaluation EvaluateExpression(SketchCurveExpressionGeometry e,double t)=>native.EvaluateExpression(e,t);
        public ImmutableArray<ExactSketchProjection> ProjectExpression(SketchCurveExpressionGeometry e,Point2d p)=>native.ProjectExpression(e,p);
        public ImmutableArray<ExactSketchIntersection> IntersectExpressions(SketchCurveExpressionGeometry a,SketchCurveExpressionGeometry b)=>native.IntersectExpressions(a,b);
        public ExactRegionInspection InspectExpressionRegion(ImmutableArray<SketchCurveExpressionGeometry> boundary)=>native.InspectExpressionRegion(boundary);
        public GeometryResult CreateExpressionEdge(SketchCurveExpressionGeometry e,RigidTransform3d p,IAssetStore a)=>native.CreateExpressionEdge(e,p,a);
    }
}
