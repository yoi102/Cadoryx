using System.Collections.Immutable;
using System.Globalization;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchExpressionDistanceDrivenEditorTests
{
    private static readonly OcctGeometryKernel Kernel=new();
    public static IEnumerable<object[]> Cases()=>from language in new[]{"en-US","zh-CN","ja-JP"}
        from region in new[]{false,true} from mode in new[]{"free","reference","driven"}
        select new object[]{language,region,mode};

    [Theory,MemberData(nameof(Cases))]
    public async Task CompleteRegionAndIndividualOffsetRespectDrivingIdentityWithoutMakingReferencesReadOnly(
        string language,bool region,string mode)
    {
        var previous=CultureInfo.CurrentUICulture;CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);
        try
        {
            var(doc,part)=SketchCommandTests.Document();var draft=new SketchDraft(CadSketch.Create(part,"Distance editor",RigidTransform3d.Identity),Kernel);
            SketchEntityId curve;ParameterTarget target;
            if(region)
            {
                draft.AddEllipse(new(new(0,0),new(3,0),new(0,2),.26,Math.Tau));
                var group=draft.OffsetExpressionRegions([draft.Value.Ellipses[0].Id],.12,SketchRegionAuthorJoin.Miter,Kernel);
                curve=draft.Value.RegionOffsets.Single().Loops[0].Outputs[0].Curve;target=ParameterTarget.SketchRegion(draft.Value.Id,group);
            }
            else
            {
                draft.AddLine(new(0,0),new(8,0));curve=draft.AddExactOffset(draft.Value.Lines[0].Id,.12);
                target=ParameterTarget.SketchCurve(draft.Value.Id,curve,"OffsetDistance",0);
            }
            doc=doc with{Sketches=doc.Sketches.Add(draft.Value.Id,draft.Value)};var assets=new MemoryAssetStore();
            await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
            if(mode!="free")
            {
                var parameter=new CadParameter(Guid.NewGuid(),"offset",ParameterDimension.Length,mode=="driven"?".12 mm":"",mode=="reference"?target:null);
                await session.ExecuteAsync(new SetParametersCommand(new([parameter],mode=="driven"?[new(Guid.NewGuid(),parameter.Id,target)]:[]),new ManagedSketchConstraintSolver(Kernel)));
            }
            var document=new CadDocumentViewModel(session,Kernel,new CadMessageLog());
            try
            {
                await using var editor=new SketchEditorViewModel(document,Kernel,new ManagedSketchConstraintSolver(Kernel),session.Snapshot.Sketches[draft.Value.Id],false);
                var notifications=new List<string?>();editor.PropertyChanged+=(_,e)=>notifications.Add(e.PropertyName);
                editor.Select(curve);Assert.Equal(region,editor.HasSelectedRegion);Assert.True(editor.HasSelectedOffsetExpression);
                Assert.Equal(mode!="driven",editor.CanEditExpressionDistance);Assert.Equal(mode!="driven",editor.ApplyExpressionDistanceCommand.CanExecute(null));
                Assert.Contains(nameof(editor.CanEditExpressionDistance),notifications);
                var baseline=session.Snapshot;var original=editor.Sketch;editor.ExpressionDistance=.22;
                if(mode=="driven")
                {
                    Assert.Equal(.12,editor.ExpressionDistance);editor.ApplyExpressionDistanceCommand.Execute(null);
                    Assert.Same(original,editor.Sketch);Assert.False(editor.Draft.CanUndo);Assert.Same(baseline,session.Snapshot);
                    await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                    Assert.Equal(.12,region?editor.Sketch.RegionOffsets.Single().Distance:editor.Sketch.ExpressionCurves.Single(e=>e.Id==curve).Geometry.SignedOffset);
                    editor.CancelCommand.Execute(null);Assert.Same(baseline,session.Snapshot);
                }
                else
                {
                    editor.ApplyExpressionDistanceCommand.Execute(null);var changed=editor.Sketch;
                    Assert.Equal(.22,region?changed.RegionOffsets.Single().Distance:changed.ExpressionCurves.Single(e=>e.Id==curve).Geometry.SignedOffset);
                    editor.UndoCommand.Execute(null);Assert.Same(original,editor.Sketch);editor.RedoCommand.Execute(null);Assert.Same(changed,editor.Sketch);
                    await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(baseline,session.Snapshot);
                    editor.CancelCommand.Execute(null);Assert.Same(baseline,session.Snapshot);
                }
                editor.Select(null);Assert.False(editor.CanEditExpressionDistance);Assert.False(editor.ApplyExpressionDistanceCommand.CanExecute(null));
                editor.Select(curve);await session.ExecuteAsync(new SetParametersCommand(new([new(Guid.NewGuid(),"unrelated",ParameterDimension.Length,"2 mm")],[])));
                Assert.True(editor.IsStale);Assert.False(editor.CanEditExpressionDistance);Assert.False(editor.ApplyExpressionDistanceCommand.CanExecute(null));
            }
            finally{document.Detach();}
        }
        finally{CultureInfo.CurrentUICulture=previous;}
    }
}
