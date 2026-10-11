using System.Globalization;
using System.Collections.Immutable;
using System.IO;
using Antelcat.I18N.WPF;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Rendering;
using Cadoryx.ViewModels;
using OcctSharp;
using static Cadoryx.wpf.Diagnostics.SketchEditorSmokeRunner;

namespace Cadoryx.wpf.Diagnostics;

internal static partial class RepetitionEditorSmokeRunner
{
    private static async Task RunPresentationAsync(MainWindowViewModel vm,CadDocumentViewModel document,
        IDocumentStorage storage,IGeometryKernel kernel,string output,Action<string> progress)
    {
        using(var native=XdeDocument.Create())
        using(var shape=ShapeFactory.CreateBox(4,3,2))
        {
            XdeLabel root;
            using(var edit=native.BeginTransaction("Production multicopy fixture"))
            {
                root=native.AddShape(shape,"Styled source");
                root.SetPresentationColor(XdeColorChannel.General,new(.2,.4,.8));
                root.SetPresentationColor(XdeColorChannel.Surface,new(.1,.3,.7));
                root.SetPresentationColor(XdeColorChannel.Curve,new(.5,.5,.5));
                root.AddLayer("Manufacturing");root.Material=new("Alloy","Density",2.7,"g/cm3","density");
                root.UpdateValidationPropertiesFromShape();
                using var members=new RepairDocumentSession(native,root);int face=0;
                foreach(var slot in members.Source.Topology.Where(t=>t.Kind==ShapeKind.Face))
                {
                    var label=members.GetOrCreateSubshapeLabel(slot.Selection);
                    label.SetPresentationColor(XdeColorChannel.Surface,new(face/6.0,1-face/6.0,.2));
                    label.SetPresentationColor(XdeColorChannel.Curve,new(0,.2,.9));label.AddLayer("Face "+face);
                    label.VisualMaterial=new("Coating",new(face/6.0,1-face/6.0,.2),.4,.6,new(0,0,0));label.IsVisible=face!=3;face++;
                }
                edit.Commit();
            }
            var file=Path.Combine(output,"native-styled-source.xbf");native.Save(file);
            using var context=document.Session.Assets.Stage(File.ReadAllBytes(file));
            using var stored=OcctGeometryBridge.StoreShape(shape,document.Session.Assets);
            var geometry=stored.Geometry with{Source=new(context.Id,root.Entry,AssetFormatPolicy.CurrentXde)};
            await document.Session.ExecuteAsync(new AddBodyCommand(new ImportedRecipe(geometry),"Styled source"));
        }
        var source=document.Session.Snapshot.Features.Values.Single(f=>f.Name=="Styled source");
        await document.Session.ExecuteAsync(DocumentEdits.SetAppearance(source.OutputBodyId,new(0xFF86ACC5,PreserveSourceStyles:true)));
        var baseline=document.Session.Snapshot;int assets=((IAssetStoreStatistics)document.Session.Assets).Count;
        foreach(string language in new[]{"en-US","zh-CN","ja-JP"})
        {
            CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);I18NExtension.Culture=CultureInfo.CurrentUICulture;
            await Operate(()=>vm.EditRepetitionsCommand.ExecuteAsync(null),async editor=>
            {
                progress($"C3 {language}: native presentation and fused conflict");editor.Activate();await Idle();
                editor.SourcePicker.SelectedItem=editor.Editor.Sources.Single(s=>s.Body==source.OutputBodyId);
                editor.KindPicker.SelectedValue=RepetitionKind.Mirror;editor.OriginXInput.Value=2;editor.RotationYInput.Value=90;
                editor.OutputPicker.SelectedValue=RepetitionOutput.Fused;
                Check(editor.StylePolicyPicker.IsEnabled&&editor.Editor.StylePolicy==RepetitionStylePolicy.VerifiedInheritance,"Production default verified inheritance");
                await Preview(editor);Check(!editor.Editor.CanConfirm,"Conflicting fused contributors reject confirmation");
                Check(ReferenceEquals(baseline,document.Session.Snapshot),"Rejected presentation keeps document");
                editor.StylePolicyPicker.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"styles-conflict-{language}.png"));
                editor.StylePolicyPicker.SelectedValue=RepetitionStylePolicy.WholeBody;await Preview(editor);
                Check(editor.Editor.CanConfirm,editor.Editor.Status);Check(!string.IsNullOrEmpty(editor.Editor.PresentationSummary),"Production presentation report");
                Check(document.PreviewScene!.Items.Any(i=>Math.Abs(i.Geometry.VolumeMm3-24)<1e-5),"Whole-body policy preserves exact fused result");
                editor.StylePolicyPicker.BringIntoView();await Idle();Save(editor,Path.Combine(output,$"styles-whole-body-{language}.png"));
                await ClickButton(editor.CancelButton);
            });
            Check(ReferenceEquals(baseline,document.Session.Snapshot),"Presentation preview cancel is atomic");
            Check(((IAssetStoreStatistics)document.Session.Assets).Count==assets,"Presentation cancel releases geometry and context");
        }
        CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("en-US");I18NExtension.Culture=CultureInfo.CurrentUICulture;
        await Operate(()=>vm.EditRepetitionsCommand.ExecuteAsync(null),async editor=>
        {
            editor.SourcePicker.SelectedItem=editor.Editor.Sources.Single(s=>s.Body==source.OutputBodyId);
            editor.NameInput.Text="Styled copies";editor.CountInput.Value=3;editor.StepInput.Value=8;await Preview(editor);
            Check(editor.Editor.CanConfirm,editor.Editor.Status);Save(editor,Path.Combine(output,"styles-verified-en-US.png"));
            await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
        });
        var feature=document.Session.Snapshot.Features.Values.Single(f=>f.Name=="Styled copies");
        var recipe=(RepetitionRecipe)feature.Recipe;var identities=recipe.CopyIds;
        Check(feature.RepetitionPresentation is {StyledSourceSlots:6,CopiedResultSlots:18,CompleteHistory:true},"All eighteen marked faces copied");
        CheckPresentation(source.Result,feature,document.Session.Assets);
        await Operate(()=>{document.EditFeature(feature.Id);return Task.CompletedTask;},async editor=>
        {
            Check(!editor.Editor.IsNew&&editor.Editor.StylePolicy==RepetitionStylePolicy.VerifiedInheritance,"Tree retains presentation policy");
            editor.CountInput.Value=4;await Preview(editor);Check(editor.Editor.CanConfirm,editor.Editor.Status);
            await ClickButton(editor.ConfirmButton);if(editor.Editor.ConfirmCommand.ExecutionTask is {} task)await task;
        });
        feature=document.Session.Snapshot.Features[feature.Id];recipe=(RepetitionRecipe)feature.Recipe;
        Check(recipe.CopyIds.Take(3).SequenceEqual(identities),"Presentation edit retains surviving CopyIds");
        Check(feature.RepetitionPresentation is {CopiedResultSlots:24},"Edited copies retain all marked faces");
        var accepted=Path.Combine(output,"accepted-styles.cadoryx");await document.Session.SaveAsync(storage,accepted);
        using(var reopened=await storage.LoadAsync(accepted,document.Session.Assets))
        {
            var loaded=reopened.Snapshot.Features[feature.Id];reopened.Snapshot.Validate();
            Check(((RepetitionRecipe)loaded.Recipe).CopyIds.SequenceEqual(recipe.CopyIds),"Native presentation identities survive reopening");
            CheckPresentation(source.Result,loaded,document.Session.Assets);
        }
        var reference=feature.Result.Source!;using var lease=document.Session.Assets.Acquire(reference.ContextAssetId);
        File.WriteAllBytes(Path.Combine(output,"native-styled-copies.xbf"),lease.Content.ToArray());
        var selected=document.Session.Snapshot with{Bodies=document.Session.Snapshot.Bodies.ToImmutableDictionary(p=>p.Key,p=>p.Value with{IsVisible=p.Key==feature.OutputBodyId})};
        await kernel.ExportAsync(selected,document.Session.Assets,Path.Combine(output,"styled-copies.step"),new CadExportOptions());
        await Idle();var viewport=WindowSmokeRunner.Host(document).Viewport!;var scene=document.Scene;
        try
        {
            viewport.SetScene(scene with{Items=scene.Items.Where(i=>i.BodyId==feature.OutputBodyId).ToImmutableArray()});
            foreach(var projection in new[]{CadProjection.Axonometric,CadProjection.Top,CadProjection.Front})
            {
                viewport.SetProjection(projection);viewport.FitAll();await Idle();
                viewport.SaveScreenshot(Path.Combine(output,$"styles-native-{projection}.png"));
            }
        }
        finally{viewport.SetScene(scene);}
        progress("C3 presentation save/reopen and exact marked-face inspection PASS");
    }

    private static void CheckPresentation(GeometryAssetRef source,FeatureDefinition feature,IAssetStore assets)
    {
        var a=source.Source!;var b=feature.Result.Source!;
        using var original=OcctGeometryBridge.ReadContext(a.ContextAssetId,assets,a.Format);
        using var result=OcctGeometryBridge.ReadContext(b.ContextAssetId,assets,b.Format);
        var left=original.GetLabel(a.DefinitionEntry);var right=result.GetLabel(b.DefinitionEntry);
        Check(left.GetPresentationColors()==right.GetPresentationColors()&&left.Material==right.Material&&left.Layers.SequenceEqual(right.Layers),"Root presentation preserved");
        Check(!right.ValidationProperties.HasAny,"Source validation attributes not copied to transformed result");
        using var sourceMap=new RepairDocumentSession(original,left);using var targetMap=new RepairDocumentSession(result,right);
        using var first=original.BeginTransaction("Read native source assignments");using var second=result.BeginTransaction("Read native result assignments");
        foreach(var row in feature.TopologyHistory!.Entries.Where(e=>e.SourceKind==HistoryShapeKind.Face&&e.ResultKind==HistoryShapeKind.Face&&e.ResultIndex.HasValue))
        {
            var expected=sourceMap.GetOrCreateSubshapeLabel(sourceMap.Source.Select(row.SourceIndex));
            var actual=targetMap.GetOrCreateSubshapeLabel(targetMap.Source.Select(row.ResultIndex!.Value));
            Check(expected.GetPresentationColors()==actual.GetPresentationColors()&&expected.Layers.SequenceEqual(actual.Layers)&&expected.Material==actual.Material&&
                expected.VisualMaterial==actual.VisualMaterial,"Native face presentation follows the actual copy history");
        }
    }
}
