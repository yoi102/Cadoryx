using System.Globalization;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using OcctSharp;
using Xunit;

namespace Cadoryx.Tests;

// Independent staged tests. Original tests and current997 production sources remain unchanged.
[Collection("IsolatedNativeDeadlines")]
public sealed class GuidedOccurrencePolicyTests
{
    private static OcctGeometryKernel Preferred()=>new(null,null,GuidedDocumentEvidencePolicy.PreferCurrentOccurrences);
    public static IEnumerable<object[]> CurvedCases()=>SerializedGeometryReferenceTests.CurvedCases();
    [Theory,MemberData(nameof(CurvedCases))]
    public async Task ApplicationProducerPolicyRetainsActualGuidedEditorAndC1ResultsAcrossUndoAndFreshReopen(string kind,bool reverse,bool tilted)
    {
        using var files=new TestFiles();string path=files.PathFor("preferred-guided.cadoryx");
        var scene=await CurvedConsumerTestScene.Create(kind,reverse,tilted,false);
        var kernel=Preferred();FeatureId id;GeometryAssetRef final=null!;string report="";
        double expectedVolume=scene.EditedVolume;
        try
        {
            await using(var session=new CadDocumentSession(scene.Session.Snapshot,scene.Assets,kernel,new InlineSessionDispatcher()))
            {
                var document=new CadDocumentViewModel(session,kernel,new CadMessageLog()){SelectedTargetPart=scene.Part};
                try
                {
                    await session.ExecuteAsync(new AddBodyCommand(scene.Recipe,kind,scene.Part));
                    var created=Assert.Single(session.Snapshot.Features.Values);id=created.Id;
                    Assert.NotNull(created.Result.SerializedOccurrences);Assert.Null(created.Result.SerializedReferences);
                    CurvedConsumerTestScene.Verify(scene.Assets,created.Result,scene.OriginalVolume);
                    var beforeEditor=session.Snapshot;
                    await using(var editor=new GuidedEditorViewModel(document,kernel,scene.IsLoft?GuidedEditorKind.Loft:GuidedEditorKind.Sweep,id))
                    {
                        await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                        Assert.Same(beforeEditor,session.Snapshot);await editor.ConfirmCommand.ExecuteAsync(null);
                    }
                    var afterEditor=session.Snapshot;Assert.NotNull(afterEditor.Features[id].Result.SerializedOccurrences);
                    CurvedConsumerTestScene.Verify(scene.Assets,afterEditor.Features[id].Result,scene.OriginalVolume);
                    await session.UndoAsync();Assert.Same(beforeEditor,session.Snapshot);
                    await session.RedoAsync();Assert.Same(afterEditor,session.Snapshot);
                    await using(var parameters=new ParameterEditorViewModel(document,kernel))
                    {
                        parameters.ParameterName="section_offset";parameters.Expression=scene.EditedOffset.ToString("R",CultureInfo.InvariantCulture)+" mm";
                        parameters.ApplyParameterCommand.Execute(null);
                        var target=ParameterTarget.SketchCurve(scene.FirstSketch,scene.FirstExpression,"OffsetDistance",0);
                        parameters.DrivingTarget=parameters.Targets.Single(t=>t.Target==target);parameters.BindDimensionCommand.Execute(null);
                        Assert.Single(parameters.Bindings);Assert.False(parameters.HasPendingInput);
                        await parameters.PreviewCommand.ExecuteAsync(null);Assert.True(parameters.CanConfirm,parameters.Status);
                        Assert.Same(afterEditor,session.Snapshot);await parameters.ConfirmCommand.ExecuteAsync(null);
                    }
                    var current=session.Snapshot;var feature=current.Features[id];final=feature.Result;
                    Assert.NotNull(final.SerializedOccurrences);Assert.Null(final.SerializedReferences);Assert.Null(feature.TopologyHistory);
                    CurvedConsumerTestScene.Verify(scene.Assets,final,expectedVolume);
                    var actualReport=Assert.IsType<GuidedModelingReport>(feature.GuidedModelingReport);actualReport.ValidateFor(final);
                    Assert.Equal(GuidedModelingReport.RecipeHash(feature.Recipe),actualReport.BuiltRecipeSha256);
                    report=System.Text.Json.JsonSerializer.Serialize(actualReport,GuidedModelingReport.JsonOptions);
                    await session.UndoAsync();Assert.Same(afterEditor,session.Snapshot);
                    await session.RedoAsync();Assert.Same(current,session.Snapshot);
                    await session.SaveAsync(new CadDocumentStorage(),path);
                    var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal("0.7.9",manifest.ApplicationVersion);
                    Assert.True(Assert.Single(manifest.Sections,s=>s.Kind==OccurrenceReferenceSectionStorage.Kind).Required);
                    Assert.Contains(OccurrenceReferenceSectionStorage.Capability,manifest.RequiredCapabilities);
                    Assert.DoesNotContain("cadoryx.native-reference-tables.1",manifest.RequiredCapabilities);
                }
                finally{document.Detach();}
            }
        }
        finally{await scene.DisposeAsync();}
        Assert.Equal(0,scene.Assets.Count);
        using var fresh=new DiskAssetStore(files.PathFor("fresh"));
        using(var loaded=await new CadDocumentStorage(deferGeometryAssets:tilted).LoadAsync(path,fresh))
        {
            var feature=loaded.Snapshot.Features[id];Assert.Equal(final,feature.Result);
            Assert.Equal(report,System.Text.Json.JsonSerializer.Serialize(feature.GuidedModelingReport,GuidedModelingReport.JsonOptions));
            Assert.Same(feature.Result,loaded.Snapshot.Bodies[feature.OutputBodyId].Geometry);
            CurvedConsumerTestScene.Verify(fresh,feature.Result,expectedVolume);
            using var graph=OcctGeometryBridge.ReadOccurrenceShape(feature.Result,fresh);
            Assert.True(graph.Report.ProvesCompleteDecodedOccurrencePaths);Assert.False(graph.Report.NativeSourceCaptureProven);
            Assert.Null(graph.Report.SourceDomainId);Assert.False(graph.Report.ProvesComposedReferenceIdentity);
        }
        Assert.Equal(0,fresh.Count);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task ExplicitPolicyKeepsTransientSingleAssetAndTheOriginalLegacyDefault(bool preferred)
    {
        var assets=new MemoryAssetStore();var kernel=preferred?Preferred():new OcctGeometryKernel();
        var recipe=new SweepRecipe(new(SketchProfile.FromCircle(new(0,0),1),RigidTransform3d.Identity),
            new([new(new(0,0),new(0,10))],new(Vector3d.Zero,Quaterniond.FromEulerDegrees(90,0,0))));
        Assert.Equal(preferred?GuidedDocumentEvidencePolicy.PreferCurrentOccurrences:GuidedDocumentEvidencePolicy.LegacyMembers,kernel.GuidedDocumentEvidence);
        using(var transient=await kernel.EvaluateAsync(recipe,assets))
        {Assert.Null(transient.Geometry.SerializedOccurrences);Assert.Null(transient.Geometry.SerializedReferences);Assert.Equal(1,assets.Count);}
        Assert.Equal(0,assets.Count);
        using(var retained=await kernel.EvaluateForDocumentAsync(recipe,assets))
        {
            Assert.Equal(preferred,retained.Geometry.SerializedOccurrences is not null);
            Assert.Equal(!preferred,retained.Geometry.SerializedReferences is not null);Assert.Equal(2,assets.Count);
            Assert.InRange(Math.Abs(retained.Geometry.VolumeMm3-10*Math.PI),0,1e-7);
        }
        Assert.Equal(0,assets.Count);
    }

    [Fact]
    public void InvalidPolicyRejectsBeforeAnyModeling()=>Assert.Throws<ArgumentOutOfRangeException>(
        ()=>new OcctGeometryKernel(null,null,(GuidedDocumentEvidencePolicy)99));

    [Fact]
    public void OptionalCaptureRejectionKeepsOriginalLargeGeometryAndStrictCaptureStillRejects()
    {
        var assets=new MemoryAssetStore();var boxes=Enumerable.Range(0,8).Select(_=>ShapeFactory.CreateBox(1,2,3)).ToArray();
        try
        {
            using var compound=ShapeFactory.CreateCompound(boxes);
            Assert.Throws<ArgumentException>(()=>OcctGeometryBridge.StoreShapeWithOccurrences(compound,assets));Assert.Equal(0,assets.Count);
            int callbacks=0;
            using(var retained=OcctGeometryBridge.StoreShapeWithPreferredOccurrences(compound,assets,captureEvidence:_=>callbacks++))
            {
                Assert.Equal(0,callbacks);Assert.Null(retained.Geometry.SerializedOccurrences);Assert.Null(retained.Geometry.SerializedReferences);
                Assert.Equal(1,assets.Count);Assert.InRange(Math.Abs(retained.Geometry.VolumeMm3-48),0,1e-7);
                var diagnostic=Assert.Single(retained.Diagnostics);Assert.Equal("MODELING.OCCURRENCE_CAPTURE_UNAVAILABLE",diagnostic.Code);
                Assert.False(diagnostic.IsError);Assert.Contains("Complete occurrence traversal",diagnostic.Message);
                using var shape=OcctGeometryBridge.ReadShape(retained.Geometry,assets);Assert.True(shape.IsValid);
                Assert.InRange(Math.Abs(shape.InspectProperties(InspectionPropertyKind.Volume).Mass-48),0,1e-7);
            }
        }
        finally{foreach(var shape in boxes)shape.Dispose();}
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("table-argument")][InlineData("callback-argument")][InlineData("table-cancel")][InlineData("callback-cancel")]
    public void AssetAndCallbackFailuresNeverBecomeOptionalCaptureFallbacks(string fault)
    {
        using var box=ShapeFactory.CreateBox(10,20,30);using var cancellation=new CancellationTokenSource();
        var assets=new FaultStore(fault,cancellation);
        Action<BrepOccurrenceReport>? callback=fault switch
        {
            "callback-argument"=>_=>throw new ArgumentException("Owned callback argument failure"),
            "callback-cancel"=>_=>cancellation.Cancel(),_=>null
        };
        if(fault.EndsWith("cancel",StringComparison.Ordinal))
            Assert.Throws<OperationCanceledException>(()=>OcctGeometryBridge.StoreShapeWithPreferredOccurrences(box,assets,cancellation.Token,callback));
        else
        {
            var failure=Assert.Throws<ArgumentException>(()=>OcctGeometryBridge.StoreShapeWithPreferredOccurrences(box,assets,cancellation.Token,callback));
            Assert.Equal(fault=="table-argument"?"Owned table argument failure":"Owned callback argument failure",failure.Message);
        }
        Assert.Equal(0,assets.Inner.Count);
    }
    private sealed class FaultStore(string fault,CancellationTokenSource cancellation):IAssetStore
    {
        public MemoryAssetStore Inner{get;}=new();public IAssetLease Acquire(AssetId id)=>Inner.Acquire(id);
        public IAssetLease Stage(ReadOnlySpan<byte> bytes)
        {
            if(!bytes.StartsWith("OCROCC26"u8))return Inner.Stage(bytes);
            if(fault=="table-argument")throw new ArgumentException("Owned table argument failure");
            var lease=Inner.Stage(bytes);if(fault=="table-cancel")cancellation.Cancel();return lease;
        }
    }
}
