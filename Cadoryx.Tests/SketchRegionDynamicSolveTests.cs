using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.IO;
using Cadoryx.Sketching;
using Cadoryx.ViewModels;
using Cadoryx.ViewModels.Services.Platform.Notifications;
using Xunit;
using Xunit.Abstractions;

namespace Cadoryx.Tests;

public sealed class SketchRegionDynamicSolveTests(ITestOutputHelper output)
{
    private static readonly OcctGeometryKernel Kernel=new();
    private static CadSketch Rectangle(SketchRegionAuthorJoin join,double distance=.25)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Dynamic region",RigidTransform3d.Identity));d.AddRectangle(new(0,0),new(8,4));
        return SketchRegionOffsetAuthoring.Create(d.Value,[d.Value.Lines[0].Id],distance,join,Kernel).Sketch;
    }
    [Theory][InlineData(SketchRegionAuthorJoin.Miter,.25)][InlineData(SketchRegionAuthorJoin.Round,.25)][InlineData(SketchRegionAuthorJoin.Bevel,.25)]
    [InlineData(SketchRegionAuthorJoin.Miter,-.25)][InlineData(SketchRegionAuthorJoin.Round,-.25)][InlineData(SketchRegionAuthorJoin.Bevel,-.25)]
    public async Task CoupledSourceChangeUpdatesCompleteGroupAndRetainsEveryIdentity(SketchRegionAuthorJoin join,double distance)
    {
        var s=Rectangle(join,distance);var ids=s.Lines.SelectMany(l=>new[]{l.Start,l.End}).Distinct().ToHashSet();
        s=s with{Constraints=s.Points.Where(p=>ids.Contains(p.Id)).Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,
            new(2+p.Position.X*1.2,-1+p.Position.Y*1.4))).ToImmutableArray()};s.Validate();
        var calls=new CountingProvider();var watch=System.Diagnostics.Stopwatch.StartNew();var result=await new ManagedSketchConstraintSolver(calls,Kernel).SolveAsync(s);watch.Stop();
        output.WriteLine($"join={join};distance={distance:R};milliseconds={watch.Elapsed.TotalMilliseconds:R};providerCalls={calls.Calls};iterations={result.Iterations};variables={result.VariableCount};equations={result.EquationCount}");
        Assert.True(result.Succeeded,$"{result.Diagnostic}: residual={result.MaxNormalizedResidual:R}");var solved=result.Solution!;
        new SketchRegionEvaluationContext(Kernel).ValidateSolved(s,solved);SketchAssociativeEndpoints.RequireSolved(solved);
        Assert.Equal(s.ExpressionCurves.Select(c=>c.Id),solved.ExpressionCurves.Select(c=>c.Id));
        Assert.Equal(s.ExpressionCurves.Select(c=>c.Endpoints),solved.ExpressionCurves.Select(c=>c.Endpoints));
        Assert.Equal(s.Points.Select(p=>p.Id),solved.Points.Select(p=>p.Id));Assert.Equal(s.RegionOffsets[0].Id,solved.RegionOffsets[0].Id);
        Assert.False(SketchRegionAuthorModel.SameTable(s,solved));Assert.InRange(result.Iterations,1,80);
        Assert.All(solved.Points.Where(p=>ids.Contains(p.Id)),p=>Assert.Equal(s.Constraints.OfType<FixPointConstraint>().Single(c=>c.Point==p.Id).Position,p.Position));
    }
    [Fact] public async Task MissingBackendRejectsAndCancellationDoesNotProducePartialOutput()
    {
        var s=Rectangle(SketchRegionAuthorJoin.Round);var report=await new ManagedSketchConstraintSolver().SolveAsync(s);
        Assert.False(report.Succeeded);Assert.Null(report.Solution);Assert.Equal("SKETCH.REGION_BACKEND_REQUIRED",report.Diagnostic);
        using var c=new CancellationTokenSource();c.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ManagedSketchConstraintSolver(Kernel).SolveAsync(s,cancellationToken:c.Token));
        Kernel.ConfirmExpressionRegions(s);
    }
    [Fact] public void CompleteSourceCacheReusesOnlyEqualRecipesAndUpdatesGeneratedPointsOnlyOnRequest()
    {
        var s=Rectangle(SketchRegionAuthorJoin.Round);var context=new SketchRegionEvaluationContext(Kernel);
        var a=context.Refresh(s,verify:false);var b=context.Refresh(s,verify:false);Assert.Equal(1,context.NativeEvaluations);
        var ids=s.Lines.SelectMany(l=>new[]{l.Start,l.End}).ToHashSet();
        var changed=s with{Points=s.Points.Select(p=>ids.Contains(p.Id)?p with{Position=new(p.Position.X+3,p.Position.Y-2)}:p).ToImmutableArray()};
        var trial=context.Refresh(changed,verify:false);Assert.Equal(2,context.NativeEvaluations);
        Assert.Equal(changed.Points,trial.Points);var synced=context.Refresh(changed,synchronizeGeneratedPoints:true);Assert.Equal(2,context.NativeEvaluations);
        SketchAssociativeEndpoints.RequireSolved(synced);Assert.Equal(a.ExpressionCurves.Select(c=>c.Endpoints),b.ExpressionCurves.Select(c=>c.Endpoints));
        Assert.NotEqual(trial.Points,synced.Points);
    }
    [Theory][InlineData(SketchRegionAuthorJoin.Miter)][InlineData(SketchRegionAuthorJoin.Round)][InlineData(SketchRegionAuthorJoin.Bevel)]
    public void NativeCandidateOracleChecksAllSourceColumnsAndEverySegmentDifferential(SketchRegionAuthorJoin join)
        =>CheckNativeColumns(Rectangle(join));
    [Theory][InlineData(SketchRegionAuthorJoin.Miter)][InlineData(SketchRegionAuthorJoin.Round)][InlineData(SketchRegionAuthorJoin.Bevel)]
    public void NonlinearBezierAndChordIntersectionsAndBridgesRetainAllAnalyticSourceColumns(SketchRegionAuthorJoin join)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Curved region",RigidTransform3d.Identity));d.AddBezier(new(0,0),new(4,-2),new(8,0));var b=d.Value.Beziers[0];
        d.AddLine(new(8,0),new(0,0),b.End,b.Start);
        var s=SketchRegionOffsetAuthoring.Create(d.Value,[b.Id],.1,join,Kernel).Sketch;CheckNativeColumns(s);
    }
    private static void CheckNativeColumns(CadSketch s)
    {
        var pointIndices=s.Points.Select((p,i)=>(p.Id,i:2*i)).ToDictionary(p=>p.Id,p=>p.i);
        var x=s.Points.SelectMany(p=>new[]{p.Position.X,p.Position.Y}).Append(.37).ToArray();
        var scalar=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchContactDifferentials")!.GetNestedType("Scalar",BindingFlags.NonPublic)!;
        var method=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchExpressionDifferentials")!.GetMethod("Evaluate",BindingFlags.Static|BindingFlags.NonPublic)!;
        object Prop(object v,string name)=>v.GetType().GetProperty(name)!.GetValue(v)!;
        double Value(object v)=>(double)Prop(v,"Value");double[] Gradient(object v)=>(double[])Prop(v,"Gradient");
        var grad=new double[x.Length];grad[^1]=1;var parameter=Activator.CreateInstance(scalar,[x[^1],grad])!;
        double[][] Oracle(double[] values)
        {
            var candidate=s with{Points=s.Points.Select(p=>p with{Position=new(values[pointIndices[p.Id]],values[pointIndices[p.Id]+1])}).ToImmutableArray()};
            candidate=new SketchRegionEvaluationContext(Kernel).Refresh(candidate,verify:false);
            return candidate.ExpressionCurves.Select(c=>
            {
                var g=c.Geometry.Evaluate(values[^1]);double speed=double.Hypot(g.First.X,g.First.Y);
                return new[]{g.Point.X,g.Point.Y,g.First.X/speed,g.First.Y/speed,(g.First.X*g.Second.Y-g.First.Y*g.Second.X)/(speed*speed*speed),speed};
            }).ToArray();
        }
        var actual=s.ExpressionCurves.Select(c=>
        {
            var g=method.Invoke(null,[s,c.Id,parameter,x,pointIndices,new Dictionary<SketchEntityId,int>(),default(Point2d),1d,3])!;
            return new[]{Prop(Prop(g,"Position"),"X"),Prop(Prop(g,"Position"),"Y"),Prop(Prop(g,"Unit"),"X"),Prop(Prop(g,"Unit"),"Y"),Prop(g,"Curvature"),Prop(g,"Speed")};
        }).ToArray();
        var reference=Oracle(x);for(int i=0;i<actual.Length;i++)for(int row=0;row<6;row++)Near(reference[i][row],Value(actual[i][row]),1e-8);
        var outputs=s.ExpressionCurves.SelectMany(c=>new[]{c.Endpoints!.Start,c.Endpoints.End}).ToHashSet();
        var sourceColumns=s.Points.Where(p=>!outputs.Contains(p.Id)).SelectMany(p=>new[]{pointIndices[p.Id],pointIndices[p.Id]+1}).Append(x.Length-1);
        foreach(int col in sourceColumns)
        {
            const double h=2e-6;var plus=(double[])x.Clone();var minus=(double[])x.Clone();plus[col]+=h;minus[col]-=h;
            var pa=Oracle(plus);var ma=Oracle(minus);
            for(int i=0;i<actual.Length;i++)for(int row=0;row<6;row++)Near((pa[i][row]-ma[i][row])/(2*h),Gradient(actual[i][row])[col],3e-5);
        }
        foreach(int col in Enumerable.Range(0,x.Length-1).Except(sourceColumns))
            Assert.All(actual.SelectMany(v=>v),v=>Assert.Equal(0,Gradient(v)[col]));
    }
    private static void Near(double expected,double actual,double tolerance)=>Assert.InRange(Math.Abs(expected-actual),0,tolerance*Math.Max(1,Math.Abs(expected)));

    [Theory][InlineData(SketchRegionAuthorJoin.Miter)][InlineData(SketchRegionAuthorJoin.Round)][InlineData(SketchRegionAuthorJoin.Bevel)]
    public void WholeDistanceChangeUpdatesEveryBridgeAndRejectsCorrespondenceChanges(SketchRegionAuthorJoin join)
    {
        var s=Rectangle(join);var d=new SketchDraft(s,Kernel);d.ChangeRegionDistance(s.RegionOffsets[0].Id,.5);var after=d.Value;
        Assert.Equal(.5,after.RegionOffsets[0].Distance);Assert.Equal(s.ExpressionCurves.Select(c=>c.Id),after.ExpressionCurves.Select(c=>c.Id));
        SketchAssociativeEndpoints.RequireSolved(after);Kernel.ConfirmExpressionRegions(after);d.Undo();Assert.Same(s,d.Value);d.Redo();Assert.Same(after,d.Value);
        if(join!=SketchRegionAuthorJoin.Miter)
        {Assert.Throws<CadValidationException>(()=>d.ChangeRegionDistance(s.RegionOffsets[0].Id,-.5));Assert.Same(after,d.Value);}
        d.FreezeRegion(s.RegionOffsets[0].Id,"Explicit frozen group");var frozen=d.Value;
        Assert.Throws<CadValidationException>(()=>d.ChangeRegionDistance(s.RegionOffsets[0].Id,.6));Assert.Same(frozen,d.Value);
    }
    [Theory][InlineData("en-US","OffsetLoop")][InlineData("zh-CN","OffsetLoop")][InlineData("ja-JP","OffsetLoop")]
    [InlineData("en-US","OffsetRegions")][InlineData("zh-CN","OffsetRegions")][InlineData("ja-JP","OffsetRegions")]
    public async Task ProductionTypedRegionToolsAndWholeGroupActionsPreviewCancelConfirmInThreeLanguages(string language,string tool)
    {
        var culture=System.Globalization.CultureInfo.CurrentUICulture;System.Globalization.CultureInfo.CurrentUICulture=System.Globalization.CultureInfo.GetCultureInfo(language);
        try
        {
            var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(initial,assets,Kernel,new InlineSessionDispatcher());
            var document=new CadDocumentViewModel(session,Kernel,new CadMessageLog());
            try
            {
                var d=new SketchDraft(CadSketch.Create(part,"Typed region tools",RigidTransform3d.Identity));d.AddEllipse(new(new(0,0),new(3,0),new(0,2),.26,Math.Tau));var source=d.Value.Ellipses[0].Id;
                await using var editor=new SketchEditorViewModel(document,Kernel,new ManagedSketchConstraintSolver(Kernel),d.Value,true){Tool=tool,OffsetDistance=.12};
                editor.Select(source);if(tool=="OffsetLoop")editor.EditCurve(source,new(3,0));else editor.ApplyRegionOffsetCommand.Execute(null);
                var group=Assert.Single(editor.Sketch.RegionOffsets);Assert.True(editor.HasSelectedRegion);Assert.False(editor.CanEditIndividualExpression);
                Assert.DoesNotContain("{0}",editor.ExpressionSourceStatus);Assert.Equal(.12,editor.ExpressionDistance);
                editor.FreezeExpressionCommand.Execute(null);Assert.True(editor.Sketch.RegionOffsets[0].IsFrozen);Assert.False(editor.HasSelectedOffsetExpression);
                editor.ResumeExpressionCommand.Execute(null);Assert.False(editor.Sketch.RegionOffsets[0].IsFrozen);
                editor.ExpressionDistance=.15;editor.ApplyExpressionDistanceCommand.Execute(null);Assert.Equal(.15,editor.Sketch.RegionOffsets[0].Distance);
                editor.UndoCommand.Execute(null);Assert.Equal(.12,editor.Sketch.RegionOffsets[0].Distance);editor.RedoCommand.Execute(null);Assert.Equal(.15,editor.Sketch.RegionOffsets[0].Distance);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);Assert.Same(initial,session.Snapshot);
                editor.CancelCommand.Execute(null);Assert.False(editor.CanConfirm);Assert.Same(initial,session.Snapshot);
                await editor.PreviewCommand.ExecuteAsync(null);Assert.True(editor.CanConfirm,editor.Status);await editor.ConfirmCommand.ExecuteAsync(null);
                var committed=session.Snapshot;Assert.Single(committed.Sketches[d.Value.Id].RegionOffsets);Kernel.ConfirmExpressionRegions(committed.Sketches[d.Value.Id]);
                await session.UndoAsync();Assert.Same(initial,session.Snapshot);await session.RedoAsync();Assert.Same(committed,session.Snapshot);
            }
            finally{document.Detach();}
            Assert.Equal(0,assets.Count);
        }
        finally{System.Globalization.CultureInfo.CurrentUICulture=culture;}
    }
    private sealed class CountingProvider:IExactSketchExpressionRegions
    {
        internal int Calls {get;private set;}
        public ExactExpressionRegionOffset OffsetExpressionRegions(ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> loops,double distance,ExactExpressionRegionJoin join)
        {Calls++;return Kernel.OffsetExpressionRegions(loops,distance,join);}
    }
    [Fact] public void SubToleranceSourceChangesStillInvalidateTheCompleteSourceValueCache()
    {
        var s=Rectangle(SketchRegionAuthorJoin.Miter);var context=new SketchRegionEvaluationContext(Kernel);context.Refresh(s,verify:false);
        var source=s.Lines[0].Start;var changed=s with{Points=s.Points.Select(p=>p.Id==source?p with{Position=new(p.Position.X+1e-10,p.Position.Y)}:p).ToImmutableArray()};
        var result=context.Refresh(changed,verify:false);Assert.Equal(2,context.NativeEvaluations);
        Assert.False(SketchRegionAuthorModel.SameTable(s,result));Kernel.ConfirmExpressionRegions(result);
    }
    [Fact] public void GroupSegmentsDoNotAdvertiseOrAcceptIndependentC1DistanceTargets()
    {
        var(initial,part)=SketchCommandTests.Document();var s=Rectangle(SketchRegionAuthorJoin.Round) with{PartId=part};var document=initial with{Sketches=initial.Sketches.Add(s.Id,s)};
        Assert.Empty(SketchExpressionParameters.All(s));var c=s.ExpressionCurves.First(c=>c.Geometry.Kind==SketchCurveExpressionKind.Offset);
        var target=ParameterTarget.SketchCurve(s.Id,c.Id,"OffsetDistance",0);
        Assert.Throws<CadValidationException>(()=>SketchCurveParameterTargets.Read(document,target));
        Assert.DoesNotContain(SketchCurveParameterTargets.All(document),t=>t.Curve==c.Id.Value);
    }
    [Fact] public async Task TangentIntersectionWithoutInvertibleBranchReturnsFailureAndKeepsTheWholeAuthor()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Tangent source chart",RigidTransform3d.Identity));d.AddLine(new(0,-2),new(8,-2));var first=d.Value.Lines[0];
        d.AddArc(new(8,-2),new(10,0),new(8,2),first.End);var arc=d.Value.Arcs[0];
        d.AddLine(new(8,2),new(0,2),arc.End);var last=d.Value.Lines[^1];
        d.AddArc(new(0,2),new(-2,0),new(0,-2),last.End,existingEnd:first.Start);
        var s=SketchRegionOffsetAuthoring.Create(d.Value,[first.Id],.1,SketchRegionAuthorJoin.Miter,Kernel).Sketch;
        var result=await new ManagedSketchConstraintSolver(Kernel).SolveAsync(s);Assert.False(result.Succeeded);Assert.Null(result.Solution);
        Assert.Equal("SKETCH.SINGULAR_EVALUATION",result.Diagnostic);Kernel.ConfirmExpressionRegions(s);
    }

    [Fact] public async Task DenseBudgetRejectsBeforeAnyNativeEvaluationAndTwoGroupChainSolvesAnalytically()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Two region groups",RigidTransform3d.Identity),Kernel);d.AddCircle(new(0,0),3);
        var circle=d.Value.Circles[0];d.Change(s=>s with{Constraints=s.Constraints.Add(new FixPointConstraint(SketchConstraintId.New(),circle.Center,new(0,0)))});
        d.OffsetExpressionRegions([circle.Id],.2,SketchRegionAuthorJoin.Miter,Kernel);var parent=d.Value.ExpressionCurves[0].Id;
        d.OffsetExpressionRegions([parent],.1,SketchRegionAuthorJoin.Miter,Kernel);
        d.Change(s=>s with{Constraints=s.Constraints.Select(c=>c is RadiusConstraint r?r with{Radius=4}:c).ToImmutableArray()});
        var calls=new CountingProvider();var solver=new ManagedSketchConstraintSolver(calls,Kernel);
        var limited=await solver.SolveAsync(d.Value,new(MaxVariables:1));Assert.Equal(SketchSolveStatus.LimitExceeded,limited.Status);Assert.Equal(0,calls.Calls);
        var watch=System.Diagnostics.Stopwatch.StartNew();var result=await solver.SolveAsync(d.Value);watch.Stop();Assert.True(result.Succeeded,result.Diagnostic);
        var solved=result.Solution!;Assert.Equal(2,solved.RegionOffsets.Length);new SketchRegionEvaluationContext(Kernel).ValidateSolved(d.Value,solved);
        var p=solved.ExpressionCurves[^1].Geometry.At(.37);Assert.InRange(Math.Abs(double.Hypot(p.X,p.Y)-4.3),0,1e-8);
        Assert.Equal(4,SketchAssociativeEndpoints.Equations(solved).Count());Assert.Equal(11,result.EquationCount);SketchAssociativeEndpoints.RequireSolved(solved);
        output.WriteLine($"twoGroups;milliseconds={watch.Elapsed.TotalMilliseconds:R};providerCalls={calls.Calls};iterations={result.Iterations}");
    }
    [Fact] public async Task CommandRejectsForgedSolverThatDropsTheGroupWithoutPublishingAnything()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(initial,assets,Kernel,new InlineSessionDispatcher());
        var s=Rectangle(SketchRegionAuthorJoin.Round) with{PartId=part};var before=session.Snapshot;int count=assets.Count;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(s,new DroppingSolver())));
        Assert.Same(before,session.Snapshot);Assert.Equal(count,assets.Count);Assert.False(session.CanUndo);
    }
    private sealed class DroppingSolver:ISketchConstraintSolver
    {
        public string Version=>"forged-group-test";
        public async Task<SketchSolveReport> SolveAsync(CadSketch s,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        {var r=await new ManagedSketchConstraintSolver(Kernel).SolveAsync(s,options,cancellationToken);return r with{Solution=r.Solution! with{RegionOffsets=[]}};}
    }
    [Fact] public async Task RecoveryReopensActualGroupPayloadAndCanContinueSourceEditing()
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var s=Rectangle(SketchRegionAuthorJoin.Round) with{PartId=part};
        var snapshot=initial with{Sketches=initial.Sketches.Add(s.Id,s)};var assets=new MemoryAssetStore();var storage=new CadDocumentStorage(regionVerifier:Kernel);
        var path=files.PathFor("recovery");var sessionId=Guid.NewGuid();RecoveryKey key;
        using(var writer=new CadRecoveryStore(path,storage))
        {key=new(writer.RunId,sessionId);await writer.WriteAsync(sessionId,snapshot,assets,null);}
        using var reader=new CadRecoveryStore(path,storage);Assert.Single((await reader.ScanAsync()).Entries);
        using(var loaded=await reader.OpenAsync(key,assets))
        {
            var restored=loaded.Document.Snapshot.Sketches[s.Id];Assert.True(SketchRegionAuthorModel.SameTable(s,restored));
            Assert.True(SketchExpressionEntities.SameTable(s,restored));var d=new SketchDraft(restored,Kernel);var sourceIds=s.Lines.SelectMany(l=>new[]{l.Start,l.End}).ToHashSet();
            d.Change(v=>v with{Points=v.Points.Select(p=>sourceIds.Contains(p.Id)?p with{Position=new(p.Position.X+1,p.Position.Y)}:p).ToImmutableArray()});
            SketchAssociativeEndpoints.RequireSolved(d.Value);Kernel.ConfirmExpressionRegions(d.Value);Assert.False(SketchRegionAuthorModel.SameTable(s,d.Value));
        }
        Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData(SketchRegionAuthorJoin.Miter)][InlineData(SketchRegionAuthorJoin.Round)][InlineData(SketchRegionAuthorJoin.Bevel)]
    public void DraftSourceEditAndWholeFreezeResumeDetachDeleteHaveOneUndoStep(SketchRegionAuthorJoin join)
    {
        var s=Rectangle(join);var d=new SketchDraft(s,Kernel);var sourceIds=s.Lines.SelectMany(l=>new[]{l.Start,l.End}).ToHashSet();
        d.Change(v=>v with{Points=v.Points.Select(p=>sourceIds.Contains(p.Id)?p with{Position=new(p.Position.X+2,p.Position.Y+1)}:p).ToImmutableArray()});
        var moved=d.Value;SketchAssociativeEndpoints.RequireSolved(moved);Kernel.ConfirmExpressionRegions(moved);
        d.Undo();Assert.Same(s,d.Value);d.Redo();Assert.Same(moved,d.Value);
        var g=moved.RegionOffsets[0];d.FreezeRegion(g.Id,"Explicit frozen source");var frozen=d.Value;
        d.Change(v=>v with{Points=v.Points.Select(p=>sourceIds.Contains(p.Id)?p with{Position=new(p.Position.X+1,p.Position.Y)}:p).ToImmutableArray()});
        Assert.True(SketchExpressionEntities.SameTable(frozen,d.Value));d.ResumeRegion(g.Id);Assert.False(d.Value.RegionOffsets[0].IsFrozen);
        SketchAssociativeEndpoints.RequireSolved(d.Value);var resumed=d.Value;
        d.DetachRegion(g.Id);Assert.Empty(d.Value.RegionOffsets);Assert.True(SketchExpressionEntities.SameTable(resumed,d.Value));
        d.Undo();Assert.Same(resumed,d.Value);d.RemoveRegion(g.Id);Assert.Empty(d.Value.RegionOffsets);Assert.Empty(d.Value.ExpressionCurves);
        Assert.Equal(s.Points.Length-s.ExpressionCurves.SelectMany(c=>new[]{c.Endpoints!.Start,c.Endpoints.End}).Distinct().Count(),d.Value.Points.Length);
        d.Undo();Assert.Same(resumed,d.Value);d.Redo();Assert.Empty(d.Value.RegionOffsets);
    }
    [Theory][InlineData("trim")][InlineData("extend")][InlineData("offset")][InlineData("remove")][InlineData("point")][InlineData("recipe")]
    public void IndividualOutputMutationsRejectWithoutChangingDraftHistory(string operation)
    {
        var s=Rectangle(SketchRegionAuthorJoin.Round);var d=new SketchDraft(s,Kernel);var c=s.ExpressionCurves[0];
        Assert.Throws<CadValidationException>(()=>
        {
            if(operation=="trim")d.TrimExpression(c.Id,.1,.9,Kernel);
            if(operation=="extend")d.ExtendExpression(c.Id,true,.01,Kernel);
            if(operation=="offset")d.OffsetExactCurve(c.Id,.01,Kernel);
            if(operation=="remove")d.RemoveExpression(c.Id);
            if(operation=="point")d.Change(v=>v with{Points=v.Points.Select(p=>p.Id==c.Endpoints!.Start?p with{Position=new(5,5)}:p).ToImmutableArray()});
            if(operation=="recipe")d.Change(v=>v with{RegionOffsets=[v.RegionOffsets[0] with{Distance=.5}]});
        });
        Assert.Same(s,d.Value);Assert.False(d.CanUndo);
    }
    [Fact] public void OutputCountOrContainmentChangeAndForgedBridgeProviderRejectAtomically()
    {
        var s=Rectangle(SketchRegionAuthorJoin.Round);var inward=Rectangle(SketchRegionAuthorJoin.Miter,-.25);
        var ids=inward.Lines.SelectMany(l=>new[]{l.Start,l.End}).ToHashSet();
        var collapse=inward with{Points=inward.Points.Select(p=>ids.Contains(p.Id)?p with{Position=new(p.Position.X,.01*p.Position.Y)}:p).ToImmutableArray()};
        Assert.Throws<CadValidationException>(()=>new SketchRegionEvaluationContext(Kernel).Refresh(collapse));Kernel.ConfirmExpressionRegions(s);
        Assert.Throws<CadValidationException>(()=>new SketchRegionEvaluationContext(new ForgedBridgeProvider(),Kernel).Refresh(s));
    }
    private sealed class ForgedBridgeProvider:IExactSketchExpressionRegions
    {
        public ExactExpressionRegionOffset OffsetExpressionRegions(ImmutableArray<ImmutableArray<ExactExpressionRegionSource>> loops,double distance,ExactExpressionRegionJoin join)
        {
            var result=Kernel.OffsetExpressionRegions(loops,distance,join);var loop=result.Loops[0];
            int index=Enumerable.Range(0,loop.Segments.Length).First(i=>loop.Segments[i].Kind==ExactExpressionRegionSegmentKind.RoundBridge);
            var segment=loop.Segments[index];var ellipse=segment.Geometry.Primitive!.Ellipse!.Value;
            ellipse=ellipse with{Center=new(ellipse.Center.X+.001,ellipse.Center.Y)};
            var geometry=SketchCurveExpressionGeometry.FromBoundary(new(ellipse.At(0),ellipse.At(1)){Ellipse=ellipse});
            return result with{Loops=result.Loops.SetItem(0,loop with{Segments=loop.Segments.SetItem(index,segment with{Geometry=geometry})})};
        }
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task C1SourceRadiusRecomputesFourLayerExactSolidAndRoundtripWithAtomicUndo(bool reverseSeeds)
    {
        using var files=new TestFiles();var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(initial,assets,Kernel,new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Four layer region",RigidTransform3d.Identity),Kernel);
        foreach(double radius in new[]{8d,6d,4d,2d})d.AddCircle(new(0,0),radius);
        d.Change(s=>s with{Constraints=s.Constraints.AddRange(s.Circles.Select(c=>new FixPointConstraint(SketchConstraintId.New(),c.Center,new(0,0))))});
        var seeds=d.Value.Circles.Select(c=>c.Id);var group=d.OffsetExpressionRegions(reverseSeeds?seeds.Reverse():seeds,.2,SketchRegionAuthorJoin.Miter,Kernel);
        var solver=new ManagedSketchConstraintSolver(Kernel);await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var s=session.Snapshot.Sketches[d.Value.Id];
        var root=Assert.Single(SketchRegionAuthorModel.Roots(s,group));var reference=new SketchProfileReference(s.Id,s.Revision,[]){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
        await session.ExecuteAsync(new AddBodyCommand(reference.Resolve(session.Snapshot,part,new ExtrudeRecipe(new([]),3,s.Plane)),"Offset material",part,sketchSource:reference));
        var feature=Assert.Single(session.Snapshot.Features.Values);var circle=s.Circles[0];var constraint=s.Constraints.OfType<RadiusConstraint>().Single(c=>c.Circle==circle.Id);
        var parameter=new CadParameter(Guid.NewGuid(),"outerRadius",ParameterDimension.Length,"10 mm");
        var before=session.Snapshot;await session.ExecuteAsync(new SetParametersCommand(new([parameter],[new(Guid.NewGuid(),parameter.Id,ParameterTarget.Sketch(s.Id,constraint.Id))])));var after=session.Snapshot;
        double area=Math.PI*(10.2*10.2-5.8*5.8+4.2*4.2-1.8*1.8);Assert.InRange(Math.Abs(after.Features[feature.Id].Result.VolumeMm3-3*area),0,1e-6);
        var updated=after.Sketches[s.Id];Assert.Equal(s.ExpressionCurves.Select(c=>c.Id),updated.ExpressionCurves.Select(c=>c.Id));
        Kernel.ConfirmExpressionRegions(updated);SketchAssociativeEndpoints.RequireSolved(updated);after.Validate();
        var storage=new CadDocumentStorage(regionVerifier:Kernel);var path=files.PathFor("dynamic-regions.cadoryx");await session.SaveAsync(storage,path);
        using(var loaded=await storage.LoadAsync(path,assets))
        {Assert.True(SketchRegionAuthorModel.SameTable(updated,loaded.Snapshot.Sketches[s.Id]));Assert.Equal(after.Features[feature.Id].Result.VolumeMm3,loaded.Snapshot.Features[feature.Id].Result.VolumeMm3);}
        await session.UndoAsync();Assert.Same(before,session.Snapshot);await session.RedoAsync();Assert.Same(after,session.Snapshot);
    }
}
