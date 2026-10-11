using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cadoryx.CommandLine;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Toolboxes;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Strings=Cadoryx.Lang.Strings.Strings;
using Xunit;

namespace Cadoryx.Tests;

public sealed partial class SketchRegionReselectionTests
{
    [Theory][InlineData(false)][InlineData(true)]
    public void PreparedWholeMappingAppliesWithoutNativeWorkAndRejectsChangedDraftsAtomically(bool stale)
    {
        var(s,id,slots)=Circles();var provider=new WorkflowKernel();
        var prepared=SketchRegionSourceReselection.Prepare(s,id,slots,provider);
        Assert.True(provider.Calls>0);Assert.True(provider.Verifications>0);
        var draft=new SketchDraft(s,provider);
        if(stale)draft.AddPoint(new(70,90));var before=draft.Value;provider.Calls=provider.Verifications=0;
        if(stale)
        {
            Assert.Throws<CadValidationException>(()=>draft.ApplyPreparedRegionSources(prepared));Assert.Same(before,draft.Value);
            draft.Undo();Assert.Same(s,draft.Value);Assert.False(draft.CanUndo);Assert.True(draft.CanRedo);
        }
        else
        {
            draft.ApplyPreparedRegionSources(prepared);Assert.Same(prepared.Candidate,draft.Value);
            draft.Undo();Assert.Same(s,draft.Value);Assert.False(draft.CanUndo);
            draft.Redo();Assert.Same(prepared.Candidate,draft.Value);Assert.False(draft.CanRedo);
        }
        Assert.Equal(0,provider.Calls);Assert.Equal(0,provider.Verifications);
    }

    [Theory][InlineData("count")][InlineData("role")][InlineData("slot")][InlineData("identity")]
    [InlineData("depth")][InlineData("parent")][InlineData("distance")][InlineData("direction")][InlineData("bridge")]
    public void UntrustedProviderCannotInventCorrespondenceOrBypassIndependentReplay(string fault)
    {
        var draft=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Untrusted map",RigidTransform3d.Identity));
        draft.AddRectangle(new(0,0),new(8,4));var seed=draft.Value.Lines[0].Id;
        draft.AddRectangle(new(20,0),new(30,6));var slots=draft.Value.Lines.Skip(4).Select(l=>l.Id).ToImmutableArray();
        var created=SketchRegionOffsetAuthoring.Create(draft.Value,[seed],.25,SketchRegionAuthorJoin.Round,Kernel);
        var editor=new SketchDraft(created.Sketch,Kernel);var provider=new WorkflowKernel{Fault=fault};
        Assert.Throws<CadValidationException>(()=>editor.ReselectRegionSources(created.Group,[slots],provider));
        Assert.Same(created.Sketch,editor.Value);Assert.False(editor.CanUndo);Assert.False(editor.CanRedo);
        Kernel.ConfirmExpressionRegions(editor.Value);
    }

    [Fact] public void ProviderWithoutCompleteVerifierIsRejectedBeforeNativeEvaluation()
    {
        var(s,id,slots)=Circles();var provider=new UnverifiedProvider();var d=new SketchDraft(s,Kernel);
        Assert.Throws<NotSupportedException>(()=>d.ReselectRegionSources(id,slots,provider));Assert.Equal(0,provider.Calls);
        Assert.Same(s,d.Value);Assert.False(d.CanUndo);
    }
    private sealed class UnverifiedProvider:IExactSketchExpressionRegions
    {
        public int Calls;
        public ExactExpressionRegionOffset OffsetExpressionRegions(ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> loops,double distance,ExactExpressionRegionJoin join)
        {Calls++;return Kernel.OffsetExpressionRegions(loops,distance,join);}
    }

    [Theory][InlineData("branch")][InlineData("open")][InlineData("order")][InlineData("containment")]
    public void ExtraBranchesOpenChainsWrongSlotOrderAndChangedContainmentHaveNoPartialAcceptance(string fault)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Invalid sources",RigidTransform3d.Identity));
        ImmutableArray<ImmutableArray<SketchEntityId>> slots;SketchRegionOffsetAuthoring.Result created;
        if(fault=="containment")
        {
            d.AddCircle(new(0,0),8);d.AddCircle(new(0,0),3);var originals=d.Value.Circles.Select(c=>c.Id).ToArray();
            d.AddCircle(new(20,0),5);d.AddCircle(new(40,0),2);slots=d.Value.Circles.Skip(2).Select(c=>ImmutableArray.Create(c.Id)).ToImmutableArray();
            created=SketchRegionOffsetAuthoring.Create(d.Value,originals,.1,SketchRegionAuthorJoin.Miter,Kernel);
        }
        else
        {
            d.AddRectangle(new(0,0),new(8,4));var seed=d.Value.Lines[0].Id;d.AddRectangle(new(20,0),new(30,6));
            var lines=d.Value.Lines.Skip(4).ToArray();slots=[lines.Select(l=>l.Id).ToImmutableArray()];
            if(fault=="branch")d.AddLine(new(20,0),new(19,-1),lines[0].Start);
            if(fault=="open")d.SetConstruction([lines[3].Id],true);
            if(fault=="order")slots=[ImmutableArray.Create(lines[0].Id,lines[2].Id,lines[1].Id,lines[3].Id)];
            created=SketchRegionOffsetAuthoring.Create(d.Value,[seed],.1,SketchRegionAuthorJoin.Miter,Kernel);
        }
        var editor=new SketchDraft(created.Sketch,Kernel);var before=editor.Value;
        Assert.Throws<CadValidationException>(()=>editor.ReselectRegionSources(created.Group,slots,Kernel));
        Assert.Same(before,editor.Value);Assert.False(editor.CanUndo);Assert.False(editor.CanRedo);
    }

    [Fact] public async Task MultiSegmentReselectionCanChangeALineToBezierWithIndependentSteinerVolume()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Mixed mapping",RigidTransform3d.Identity));
        d.AddRectangle(new(0,0),new(8,4));var seed=d.Value.Lines[0].Id;
        d.AddBezier(new(20,0),new(25,-1),new(30,0));var b=d.Value.Beziers[0];
        d.AddLine(new(30,0),new(30,6),b.End);var right=d.Value.Lines[^1];
        d.AddLine(new(30,6),new(20,6),right.End);var top=d.Value.Lines[^1];
        d.AddLine(new(20,6),new(20,0),top.End,b.Start);var left=d.Value.Lines[^1];
        var authored=SketchRegionOffsetAuthoring.Create(d.Value,[seed],.2,SketchRegionAuthorJoin.Round,Kernel);
        var updated=SketchRegionSourceReselection.Apply(authored.Sketch,authored.Group,[[b.Id,right.Id,top.Id,left.Id]],Kernel);
        Assert.Equal(8,updated.ExpressionCurves.Length);Assert.Equal(SketchExpressionSourceKind.Bezier,updated.RegionOffsets[0].Sources[0][0].Kind);
        Assert.Equal(authored.Sketch.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),updated.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
        var(doc,part)=SketchCommandTests.Document();updated=updated with{PartId=part};doc=doc with{Sketches=doc.Sketches.Add(updated.Id,updated)};
        var root=Assert.Single(SketchRegionAuthorModel.Roots(updated,authored.Group));
        var profile=new SketchProfileReference(updated.Id,updated.Revision,[]){MixedBoundaryIds=root.MixedCurveIds};var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(doc,part,new ExtrudeRecipe(new([]),2,updated.Plane)),"Mixed result",part,sketchSource:profile));
        // x=20+10t, y=-2t(1-t); independent exact area and Simpson arc length.
        const int count=16384;double sum=0;
        for(int i=0;i<=count;i++){double t=(double)i/count;sum+=(i==0||i==count?1:i%2==0?2:4)*Math.Sqrt(100+Math.Pow(4*t-2,2));}
        double area=60+10d/3,perimeter=22+sum/(3*count);
        Assert.InRange(Math.Abs(Assert.Single(session.Snapshot.Features.Values).Result.VolumeMm3-2*(area+.2*perimeter+.04*Math.PI)),0,1e-6);
    }

    [Fact] public async Task SelectionRequiresAllExplicitSlotsAndOneApplyProducesOneDraftUndo()
    {
        var(s,id,slots)=Circles();var(doc,part)=SketchCommandTests.Document();s=s with{PartId=part};doc=doc with{Sketches=doc.Sketches.Add(s.Id,s)};
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        var document=new CadDocumentViewModel(session,Kernel,new CadMessageLog());
        try
        {
            await using var owner=new SketchEditorViewModel(document,Kernel,new ManagedSketchConstraintSolver(Kernel),s,false);
            await using var chooser=new SketchRegionSourceSelectionViewModel(owner,id);
            Assert.All(chooser.Rows,row=>Assert.Null(row.Replacement));Assert.False(chooser.CanApply);
            await chooser.PreviewCommand.ExecuteAsync(null);Assert.Equal(Strings.RegionReselectIncomplete,chooser.Status);Assert.Same(s,owner.Sketch);
            var row=Assert.Single(chooser.Rows);Assert.Equal(s.Circles[0].Id.Value.ToString("D"),row.OriginalIdentity);
            Assert.DoesNotContain(row.Choices,c=>s.ExpressionCurves.Any(e=>e.Id==c.Id));
            row.Replacement=row.Choices.Single(c=>c.Id==slots[0][0]);await chooser.PreviewCommand.ExecuteAsync(null);
            Assert.True(chooser.CanApply,chooser.Status+chooser.Details);Assert.Same(s,owner.Sketch);Assert.Same(doc,session.Snapshot);
            chooser.ApplyCommand.Execute(null);var after=owner.Sketch;Assert.NotSame(s,after);Assert.Same(doc,session.Snapshot);
            owner.UndoCommand.Execute(null);Assert.Same(s,owner.Sketch);Assert.False(owner.Draft.CanUndo);owner.RedoCommand.Execute(null);Assert.Same(after,owner.Sketch);
        }
        finally{document.Detach();}
    }

    [Theory][InlineData("cancel")][InlineData("owner")][InlineData("rows")]
    public async Task LateNativePreparationCannotPublishAfterCancellationOrChangedMapping(string change)
    {
        var(s,id,slots)=Circles();var(doc,part)=SketchCommandTests.Document();s=s with{PartId=part};doc=doc with{Sketches=doc.Sketches.Add(s.Id,s)};
        using var kernel=new WorkflowKernel{Gate=true};var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(doc,assets,kernel,new InlineSessionDispatcher());
        var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            await using var owner=new SketchEditorViewModel(document,kernel,new ManagedSketchConstraintSolver(Kernel),s,false);
            await using var chooser=new SketchRegionSourceSelectionViewModel(owner,id);var row=Assert.Single(chooser.Rows);
            row.Replacement=row.Choices.Single(c=>c.Id==slots[0][0]);var previous=chooser.PreviewEditor;
            var pending=chooser.PreviewCommand.ExecuteAsync(null);
            try
            {
                await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));Assert.True(chooser.IsWorking);Assert.False(chooser.CanApply);
                if(change=="cancel")chooser.CancelCommand.Execute(null);
                if(change=="owner")owner.IsStale=true;
                if(change=="rows")row.Replacement=row.Choices.Single(c=>c.Id==s.Circles[0].Id);
            }
            finally{kernel.Release.TrySetResult();await pending;}
            Assert.False(chooser.CanApply);Assert.Same(previous,chooser.PreviewEditor);Assert.Same(s,owner.Sketch);Assert.Same(doc,session.Snapshot);Assert.False(owner.Draft.CanUndo);
            if(change=="owner")Assert.Equal(Strings.RegionReselectStale,chooser.Status);
            if(change=="rows")Assert.Equal(Strings.RegionReselectPreviewOutdated,chooser.Status);
        }
        finally{document.Detach();}
    }

    [Theory][InlineData(false,false)][InlineData(true,false)][InlineData(false,true)][InlineData(true,true)]
    public async Task RetainedRegionParametersRejectFamilyOrDirectionChangesEvenWhenSourceIdentityIsUnchanged(bool reference,bool direction)
    {
        var(doc,part)=SketchCommandTests.Document();var d=new SketchDraft(CadSketch.Create(part,"Same source identity",RigidTransform3d.Identity));
        if(direction)d.AddEllipse(new(new(0,0),new(5,0),new(0,2),0,Math.Tau));else d.AddCircle(new(0,0),3);
        var source=direction?d.Value.Ellipses[0].Id:d.Value.Circles[0].Id;
        var created=SketchRegionOffsetAuthoring.Create(d.Value,[source],.2,SketchRegionAuthorJoin.Miter,Kernel);var s=created.Sketch;
        var target=ParameterTarget.SketchRegion(s.Id,created.Group);var parameter=new CadParameter(Guid.NewGuid(),"distance",ParameterDimension.Length,reference?"":".2 mm",reference?target:null);
        doc=doc with{Sketches=doc.Sketches.Add(s.Id,s),Parameters=new([parameter],reference?[]:[new(Guid.NewGuid(),parameter.Id,target)])};
        var edited=s with{RegionOffsets=[s.RegionOffsets[0] with{IsFrozen=true,FrozenReason="Explicit source edit"}]};
        if(direction)edited=edited with{Ellipses=[edited.Ellipses[0] with{Geometry=edited.Ellipses[0].Geometry with{SweepAngle=-Math.Tau}}]};
        else edited=edited with{Circles=[],Ellipses=[new(source,new(new(20,0),new(5,0),new(0,2),0,Math.Tau))],Constraints=edited.Constraints.Where(c=>c is not RadiusConstraint radius||radius.Circle!=source).ToImmutableArray()};
        var candidate=SketchRegionSourceReselection.Apply(edited,created.Group,[[source]],Kernel);
        Assert.Equal(source,candidate.RegionOffsets[0].Sources[0][0].Entity);
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(candidate,new ManagedSketchConstraintSolver(Kernel))));Assert.Same(doc,session.Snapshot);
        await session.ExecuteAsync(new SetParametersCommand(ParameterBook.Empty));await session.ExecuteAsync(new UpsertSketchCommand(candidate,new ManagedSketchConstraintSolver(Kernel)));
        Kernel.ConfirmExpressionRegions(session.Snapshot.Sketches[s.Id]);
    }

    [Fact] public async Task CompleteReselectionRefreshesDescendantGroupsAndKeepsOldSourceDriversIndependent()
    {
        var(s,id,slots)=Circles();var(doc,part)=SketchCommandTests.Document();s=s with{PartId=part};var d=new SketchDraft(s,Kernel);
        var child=d.OffsetExpressionRegions([s.ExpressionCurves[0].Id],.1,SketchRegionAuthorJoin.Miter,Kernel);var before=d.Value;
        var prepared=SketchRegionSourceReselection.Prepare(before,id,slots,Kernel);d.ApplyPreparedRegionSources(prepared);var after=d.Value;
        Assert.Equal(before.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),after.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
        Assert.Equal(before.RegionOffsets.Single(g=>g.Id==child).Sources.SelectMany(l=>l).Select(v=>v.Entity),after.RegionOffsets.Single(g=>g.Id==child).Sources.SelectMany(l=>l).Select(v=>v.Entity));
        Kernel.ConfirmExpressionRegions(after);SketchAssociativeEndpoints.RequireSolved(after);
        var original=s.Circles[0].Id;var parameter=new CadParameter(Guid.NewGuid(),"oldRadius",ParameterDimension.Length,"4 mm");
        doc=doc with{Sketches=doc.Sketches.Add(after.Id,after)};var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        var radius=after.Constraints.OfType<RadiusConstraint>().Single(c=>c.Circle==original);
        await session.ExecuteAsync(new SetParametersCommand(new([parameter],[new(Guid.NewGuid(),parameter.Id,ParameterTarget.Sketch(after.Id,radius.Id))])));
        var driven=session.Snapshot.Sketches[after.Id];Assert.Equal(4,driven.Circles.Single(c=>c.Id==original).Radius);
        Assert.True(SketchRegionAuthorModel.SameTable(after,driven));
        Assert.True(after.ExpressionCurves.Zip(driven.ExpressionCurves).All(p=>p.First.Geometry.SameValues(p.Second.Geometry)));
        d.Undo();Assert.Same(before,d.Value);d.Redo();Assert.Same(after,d.Value);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task TerminalAndAgentUseTheSameExplicitMapAndOneDocumentUndo(bool agent)
    {
        var(s,id,slots)=Circles();var(doc,part)=SketchCommandTests.Document();s=s with{PartId=part};doc=doc with{Sketches=doc.Sketches.Add(s.Id,s)};
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());var document=new CadDocumentViewModel(session,Kernel,new CadMessageLog());
        try
        {
            var command=new CadCommandLineService();var tools=new CadAgentToolset(command,document);var args=SourceArguments(s,id,slots);
            string response;
            if(agent)
            {
                args["documentId"]=doc.Id.ToString();args["stateId"]=doc.StateId.ToString();
                response=await tools.ExecuteAsync(new("explicit-map","cad_edit",args.ToJsonString()),default);
            }
            else
            {
                var result=await command.ExecuteAsync("SKETCH_REGION_SOURCES "+args.ToJsonString(),new CadCommandContext(document));
                Assert.True(result.Success,result.Message);response=result.Message;
            }
            using var parsed=JsonDocument.Parse(response);Assert.True(parsed.RootElement.GetProperty("success").GetBoolean(),response);var after=session.Snapshot;
            Assert.Equal(slots[0][0],after.Sketches[s.Id].RegionOffsets[0].Sources[0][0].Entity);
            Assert.Equal(s.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),after.Sketches[s.Id].ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
            using var read=JsonDocument.Parse(await tools.ExecuteAsync(new("map-read","cad_inspect",JsonSerializer.Serialize(new{area="sketch_geometry",id=s.Id.Value})),default));
            var groupRead=read.RootElement.GetProperty("items")[0].GetProperty("regionAuthors")[0];
            Assert.Equal(slots[0][0].Value,groupRead.GetProperty("sources")[0][0].GetProperty("entity").GetGuid());
            Assert.Equal(s.ExpressionCurves[0].Id.Value,groupRead.GetProperty("loops")[0].GetProperty("outputs")[0].GetProperty("curve").GetGuid());
            Assert.Equal(s.ExpressionCurves[0].Endpoints!.Start.Value,groupRead.GetProperty("loops")[0].GetProperty("outputs")[0].GetProperty("endpoints").GetProperty("start").GetGuid());
            Kernel.ConfirmExpressionRegions(after.Sketches[s.Id]);
            await session.UndoAsync();Assert.Same(doc,session.Snapshot);Assert.False(session.CanUndo);await session.RedoAsync();Assert.Same(after,session.Snapshot);
        }
        finally{document.Detach();}
    }
    [Theory][InlineData("empty")][InlineData("foreign")][InlineData("self")][InlineData("revision")][InlineData("group")]
    [InlineData("unexpected")][InlineData("duplicate")][InlineData("state")][InlineData("document")][InlineData("driver")][InlineData("reference")]
    public async Task AgentRejectsUnprovedRegionMapsAndStaleOrParameterBoundAuthorsAtomically(string fault)
    {
        var(s,id,slots)=Circles();var(doc,part)=SketchCommandTests.Document();s=s with{PartId=part};doc=doc with{Sketches=doc.Sketches.Add(s.Id,s)};
        if(fault is "driver" or "reference")
        {
            var target=ParameterTarget.SketchRegion(s.Id,id);var p=new CadParameter(Guid.NewGuid(),"distance",ParameterDimension.Length,fault=="reference"?"":".2 mm",fault=="reference"?target:null);
            doc=doc with{Parameters=new([p],fault=="reference"?[]:[new(Guid.NewGuid(),p.Id,target)])};
        }
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());var document=new CadDocumentViewModel(session,Kernel,new CadMessageLog());
        try
        {
            var tools=new CadAgentToolset(new CadCommandLineService(),document);var args=SourceArguments(s,id,slots);args["documentId"]=doc.Id.ToString();args["stateId"]=doc.StateId.ToString();
            switch(fault)
            {
                case "empty":args["sources"]=new JsonArray();break;
                case "foreign":args["sources"]=JsonSerializer.SerializeToNode(new[]{new[]{Guid.NewGuid()}});break;
                case "self":args["sources"]=JsonSerializer.SerializeToNode(new[]{new[]{s.ExpressionCurves[0].Id.Value}});break;
                case "revision":args["revision"]=Guid.NewGuid().ToString("D");break;
                case "group":args["id"]=Guid.NewGuid().ToString("D");break;
                case "unexpected":args["guessNearest"]=true;break;
                case "state":args["stateId"]=Guid.NewGuid().ToString("D");break;
                case "document":args["documentId"]=Guid.NewGuid().ToString("D");break;
            }
            string json=args.ToJsonString();if(fault=="duplicate")json=json[..^1]+",\"sources\":"+args["sources"]!.ToJsonString()+"}";
            var response=await tools.ExecuteAsync(new("bad-map","cad_edit",json),default);using var parsed=JsonDocument.Parse(response);
            Assert.False(parsed.RootElement.GetProperty("success").GetBoolean(),response);Assert.Same(doc,session.Snapshot);Assert.False(session.CanUndo);Assert.Equal(0,assets.Count);
        }
        finally{document.Detach();}
    }
    [Theory][InlineData(false,false)][InlineData(true,false)][InlineData(false,true)][InlineData(true,true)]
    public async Task TerminalAndAgentDiscardPreparedMapsWhenCancellationOrStateChangeArrivesDuringNativeWork(bool agent,bool stateChange)
    {
        var(s,id,slots)=Circles();var(doc,part)=SketchCommandTests.Document();s=s with{PartId=part};doc=doc with{Sketches=doc.Sketches.Add(s.Id,s)};
        using var kernel=new WorkflowKernel{Gate=true};using var cancel=new CancellationTokenSource();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(doc,assets,kernel,new InlineSessionDispatcher());var document=new CadDocumentViewModel(session,kernel,new CadMessageLog());
        try
        {
            var commands=new CadCommandLineService();var args=SourceArguments(s,id,slots);Task<string> pending;
            if(agent)
            {
                args["documentId"]=doc.Id.ToString();args["stateId"]=doc.StateId.ToString();
                pending=new CadAgentToolset(commands,document).ExecuteAsync(new("raced-map","cad_edit",args.ToJsonString()),cancel.Token);
            }
            else
            {
                async Task<string> Terminal()
                {
                    var response=await commands.ExecuteAsync("SKETCH_REGION_SOURCES "+args.ToJsonString(),new CadCommandContext(document),cancel.Token);
                    if(response.IsCancelled)throw new OperationCanceledException(cancel.Token);
                    return JsonSerializer.Serialize(new{success=response.Success});
                }
                pending=Terminal();
            }
            try
            {
                await kernel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
                if(stateChange)await session.ExecuteAsync(DocumentEdits.SetGrid(doc.Settings.Grid with{SpacingMm=12}));else cancel.Cancel();
            }
            finally{kernel.Release.TrySetResult();}
            if(stateChange)
            {
                using var parsed=JsonDocument.Parse(await pending);Assert.False(parsed.RootElement.GetProperty("success").GetBoolean());
                var changed=session.Snapshot;Assert.NotSame(doc,changed);Assert.Same(s,changed.Sketches[s.Id]);await session.UndoAsync();Assert.Same(doc,session.Snapshot);Assert.False(session.CanUndo);
            }
            else{await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);Assert.Same(doc,session.Snapshot);Assert.False(session.CanUndo);}
            Assert.Equal(0,assets.Count);
        }
        finally{document.Detach();}
    }
    private static JsonObject SourceArguments(CadSketch s,Guid group,ImmutableArray<ImmutableArray<SketchEntityId>> slots)
        =>new(){["action"]="reselect_region_sources",["sketchId"]=s.Id.ToString(),["revision"]=s.Revision.ToString("D"),["id"]=group.ToString("D"),
            ["sources"]=JsonSerializer.SerializeToNode(slots.Select(l=>l.Select(id=>id.Value).ToArray()).ToArray())};

    private sealed class WorkflowKernel:IGeometryKernel,IExactSketchExpressionRegions,ISketchExpressionRegionVerifier,IDisposable
    {
        public int Calls,Verifications;public string? Fault;public bool Gate;
        public TaskCompletionSource Entered {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExactExpressionRegionOffset OffsetExpressionRegions(ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> loops,double distance,ExactExpressionRegionJoin join)
        {
            if(Interlocked.Increment(ref Calls)==1&&Gate){Entered.TrySetResult();Release.Task.GetAwaiter().GetResult();}
            var result=Kernel.OffsetExpressionRegions(loops,distance,join);if(Fault is null)return result;
            var loop=result.Loops[0];var segment=loop.Segments[0];
            switch(Fault)
            {
                case "count":return result with{Loops=[]};
                case "depth":loop=loop with{Depth=loop.Depth+1};break;
                case "parent":loop=loop with{ParentLoopIndex=0};break;
                case "direction":loop=loop with{InputTraversalReversed=!loop.InputTraversalReversed};break;
                case "distance":return result with{OutwardDistance=distance+.01};
                case "role":segment=segment with{Kind=ExactExpressionRegionSegmentKind.BevelBridge};break;
                case "slot":segment=segment with{OriginalCurveIndices=[1]};break;
                case "identity":segment=segment with{SourceIds=[SketchEntityId.New()]};break;
                case "bridge":
                    int bridge=Array.FindIndex(loop.Segments.ToArray(),v=>v.Kind==ExactExpressionRegionSegmentKind.RoundBridge);
                    loop=loop with{Segments=loop.Segments.SetItem(bridge,loop.Segments[bridge] with{Geometry=SketchCurveExpressionGeometry.FromBoundary(new(new(100,100),new(101,100)))})};
                    return result with{Loops=result.Loops.SetItem(0,loop)};
            }
            if(Fault is "role" or "slot" or "identity")loop=loop with{Segments=loop.Segments.SetItem(0,segment)};
            return result with{Loops=result.Loops.SetItem(0,loop)};
        }
        public void ConfirmExpressionRegions(CadSketch sketch,CancellationToken token=default){Verifications++;Kernel.ConfirmExpressionRegions(sketch,token);}
        public void Dispose()=>Release.TrySetResult();
        public string Version=>Kernel.Version;public bool Supports(GeometryRecipe recipe)=>Kernel.Supports(recipe);
        public Task<GeometryResult> EvaluateAsync(GeometryRecipe recipe,IAssetStore assets,CancellationToken token=default)=>Kernel.EvaluateAsync(recipe,assets,token);
        public Task<LoadedDocument> ImportAsync(string path,IAssetStore assets,CancellationToken token=default)=>Kernel.ImportAsync(path,assets,token);
        public Task ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CancellationToken token=default)=>Kernel.ExportAsync(snapshot,assets,path,token);
        public Task<CadExportReport> ExportAsync(DocumentSnapshot snapshot,IAssetStore assets,string path,CadExportOptions options,CancellationToken token=default)=>Kernel.ExportAsync(snapshot,assets,path,options,token);
    }
}
