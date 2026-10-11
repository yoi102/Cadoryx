using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Cadoryx.AI.Contracts;
using Cadoryx.Commands;
using Cadoryx.CommandLine;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Cadoryx.ViewModels.Toolboxes;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchSlidingContactAuthoringTests
{
    [Theory][InlineData("en-US")][InlineData("zh-CN")][InlineData("ja-JP")]
    public async Task EditorRetainsSeparateAuthorsInvalidatesPreviewAndRejectsInvalidBoundsInEachLanguage(string language)
    {
        var previous=CultureInfo.CurrentUICulture;CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(language);
        try
        {
            var(initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
            await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
            var source=SketchSlidingContactSolveTests.Pair("Ellipse",false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveBoth) with{PartId=part};
            await session.ExecuteAsync(new UpsertSketchCommand(source,new ManagedSketchConstraintSolver()));
            var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
            try
            {
                var s=session.Snapshot.Sketches[source.Id];var c=s.Constraints.OfType<FiniteParametricContactConstraint>().Single();
                await using var editor=new SketchEditorViewModel(document,kernel,new ManagedSketchConstraintSolver(),s,false);
                editor.SelectedConstraint=editor.Constraints.Single(v=>v.Id==c.Id);
                Assert.Equal(FiniteContactParameterMode.SolveBoth,editor.ContactParameterMode);Assert.Equal(.34,editor.ContactSeedA);Assert.Equal(.46,editor.ContactSeedB);
                Assert.Equal(c.ParameterA,editor.ContactParameterA);Assert.Equal(c.ParameterB,editor.ContactParameterB);
                Assert.False(editor.ContactParameterAWritable);Assert.False(editor.ContactParameterBWritable);
                Assert.Equal(4,editor.FiniteContactParameterModes.Count);Assert.All(editor.FiniteContactParameterModes,v=>Assert.False(string.IsNullOrWhiteSpace(v.Label)));
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                editor.ContactSeedA=.36;Assert.False(editor.CanConfirm);editor.ApplyFiniteContactCommand.Execute(null);
                Assert.Equal(.36,editor.Sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single().SlidingA!.Seed);
                editor.UndoCommand.Execute(null);Assert.Equal(.34,editor.ContactSeedA);Assert.Equal(FiniteContactParameterMode.SolveBoth,editor.ContactParameterMode);
                editor.RedoCommand.Execute(null);Assert.Equal(.36,editor.ContactSeedA);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);
                var solved=editor.Sketch.Constraints.OfType<FiniteParametricContactConstraint>().Single();
                Assert.Equal(solved.ParameterA,editor.ContactParameterA);Assert.Equal(solved.ParameterB,editor.ContactParameterB);
                Assert.Equal(.36,editor.ContactSeedA);Assert.Equal(.46,editor.ContactSeedB);
                Assert.Equal(new SlidingContactParameter(.36,.2,.6),solved.SlidingA);
                Assert.Equal(new SlidingContactParameter(.46,.3,.7),solved.SlidingB);
                Assert.NotEqual(editor.ContactSeedA,editor.ContactParameterA);
                var before=editor.Sketch;editor.ContactLowerA=.4;Assert.False(editor.CanConfirm);editor.ApplyFiniteContactCommand.Execute(null);
                Assert.Same(before,editor.Sketch);Assert.Equal(Cadoryx.Lang.Strings.Strings.SlidingContactInvalidInterval,editor.Status);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.False(editor.CanConfirm);Assert.Same(before,editor.Sketch);
                Assert.Equal(Cadoryx.Lang.Strings.Strings.SlidingContactInvalidInterval,editor.Status);
            }
            finally{document.Detach();}
            Assert.Equal(0,assets.Count);
        }
        finally{CultureInfo.CurrentUICulture=previous;}
    }
    [Theory][InlineData(FiniteContactParameterMode.SolveA)][InlineData(FiniteContactParameterMode.SolveB)][InlineData(FiniteContactParameterMode.SolveBoth)]
    public async Task TerminalAndAgentUseCompleteAuthorsRetainModeOnNoopAndRejectReadonlyOrHiddenFields(FiniteContactParameterMode mode)
    {
        var(initial,part)=SketchCommandTests.Document();var kernel=new OcctGeometryKernel();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());
        var source=SketchSlidingContactSolveTests.Pair("Ellipse",false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.Fixed) with{PartId=part};
        var prototype=source.Constraints.OfType<FiniteParametricContactConstraint>().Single();source=source with{Constraints=source.Constraints.Remove(prototype)};
        await session.ExecuteAsync(new UpsertSketchCommand(source,new ManagedSketchConstraintSolver()));var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var service=new CadCommandLineService();var tools=new CadAgentToolset(service,document);
            bool a=mode is FiniteContactParameterMode.SolveA or FiniteContactParameterMode.SolveBoth,b=mode is FiniteContactParameterMode.SolveB or FiniteContactParameterMode.SolveBoth;
            Dictionary<string,object> Args(bool edit)
            {
                var args=new Dictionary<string,object>{["action"]="set_sketch_finite_contact",["id"]=source.Id.ToString(),["revision"]=session.Snapshot.Sketches[source.Id].Revision,
                    ["curveA"]=prototype.A.ToString(),["curveB"]=prototype.B.ToString(),["direction"]="SameDirection",["continuity"]="G2",["parameterMode"]=mode.ToString()};
                if(a){args["seedA"]=.34;args["lowerA"]=.2;args["upperA"]=.6;}else args["parameterA"]=.37;
                if(b){args["seedB"]=.46;args["lowerB"]=.3;args["upperB"]=.7;}else args["parameterB"]=.5;
                if(edit)args["constraintId"]=session.Snapshot.Sketches[source.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single().Id.ToString();
                return args;
            }
            var created=await service.ExecuteAsync("SKETCH_FINITE_CONTACT "+JsonSerializer.Serialize(Args(false)),new CadCommandContext(document));Assert.True(created.Success,created.Message);
            var before=session.Snapshot;var c=before.Sketches[source.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single();Assert.Equal(mode,c.ParameterMode);
            var noop=Args(true);noop.Remove("parameterMode");var result=await service.ExecuteAsync("SKETCH_FINITE_CONTACT "+JsonSerializer.Serialize(noop),new CadCommandContext(document));
            Assert.True(result.Success,result.Message);Assert.Same(before,session.Snapshot);Assert.Equal(mode,session.Snapshot.Sketches[source.Id].Constraints.OfType<FiniteParametricContactConstraint>().Single().ParameterMode);
            foreach(string change in new[]{"readonly","missing-seed","extra","invalid-mode"})
            {
                var args=Args(true);
                if(change=="readonly")args[a?"parameterA":"parameterB"]=.37;
                else if(change=="missing-seed")args.Remove(a?"seedA":"seedB");
                else if(change=="extra")args["resultA"]=.37;else args["parameterMode"]="Automatic";
                var rejected=await service.ExecuteAsync("SKETCH_FINITE_CONTACT "+JsonSerializer.Serialize(args),new CadCommandContext(document));Assert.False(rejected.Success);Assert.Same(before,session.Snapshot);
            }
            using(var read=JsonDocument.Parse(await tools.ExecuteAsync(new AiToolCall("read","cad_inspect",JsonSerializer.Serialize(new{area="sketch_geometry",id=source.Id.ToString()})),default)))
            {
                var item=read.RootElement.GetProperty("items")[0];
                var detail=item.GetProperty("constraints").EnumerateArray().Single(v=>v.GetProperty("id").GetString()==c.Id.ToString()).GetProperty("finiteContact");
                Assert.Equal(mode.ToString(),detail.GetProperty("parameterMode").GetString());Assert.Equal(a,detail.GetProperty("parameterAReadOnly").GetBoolean());
                Assert.Equal(b,detail.GetProperty("parameterBReadOnly").GetBoolean());Assert.True(detail.GetProperty("verified").GetBoolean());
                var state=item.GetProperty("constraintState");Assert.True(state.GetProperty("readOnly").GetBoolean());
                Assert.Equal(0,state.GetProperty("iterations").GetInt32());Assert.Equal("current_actual_parameters",state.GetProperty("evaluation").GetString());
                Assert.True(state.GetProperty("localLinearizationOnly").GetBoolean());
                var analysis=state.GetProperty("analysis");Assert.True(analysis.GetProperty("isSatisfied").GetBoolean());
                Assert.Equal(0,analysis.GetProperty("degreesOfFreedom").GetInt32());
                Assert.Equal(source.Points.Length*2+c.ParameterVariableCount,analysis.GetProperty("variableCount").GetInt32());
                Assert.Contains("seedA/B",item.GetProperty("finiteContactContract").GetString());
            }
            var agentArgs=Args(true);agentArgs["documentId"]=before.Id.ToString();agentArgs["stateId"]=before.StateId.ToString();
            using(var response=JsonDocument.Parse(await tools.ExecuteAsync(new AiToolCall("noop","cad_edit",JsonSerializer.Serialize(agentArgs)),default)))
            {Assert.True(response.RootElement.GetProperty("success").GetBoolean(),response.RootElement.ToString());Assert.False(response.RootElement.GetProperty("changed").GetBoolean());}
            Assert.Same(before,session.Snapshot);Assert.Equal(0,assets.Count);
        }
        finally{document.Detach();}
    }
}
