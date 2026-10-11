using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;
using Xunit.Abstractions;

namespace Cadoryx.Tests;

public sealed class AssemblyConvergenceTests(ITestOutputHelper output)
{
    [Fact] public void SingularDistanceAtCoincidentOriginsDoesNotInventAnAxisOrPublishAPose()
    {
        var sample=Sample(2,Vector3d.Zero);var doc=Pair(sample,AssemblyConstraintKind.Distance,10);
        var plan=AssemblySolveCommands.Plan(doc);
        Assert.Same(doc,plan.Candidate);Assert.Equal(AssemblySolveFailureReason.SingularJacobian,plan.Report.FailureReason);
        Assert.Equal(AssemblySolveStatus.Conflict,plan.Report.Status);Assert.False(plan.Report.HasGlobalUniquenessProof);
        Assert.NotEmpty(plan.Report.Residuals);Assert.Equal(AssemblySolveReport.CurrentSolverVersion,plan.Report.SolverVersion);
    }

    [Fact] public void TwoInitialDistanceBranchesRemainDistinctAndOnlyClaimLocalSatisfaction()
    {
        var positive=Sample(2,new(5,0,0));var negative=Sample(2,new(-5,0,0));
        var a=AssemblySolveCommands.Plan(Pair(positive,AssemblyConstraintKind.Distance,10));
        var b=AssemblySolveCommands.Plan(Pair(negative,AssemblyConstraintKind.Distance,10));
        Assert.Equal(AssemblySolveFailureReason.None,a.Report.FailureReason);Assert.Equal(AssemblySolveFailureReason.None,b.Report.FailureReason);
        Assert.Equal(10,a.Candidate.EnumerateOccurrences().Single(o=>o.Path.Equals(positive.Paths[1])).WorldTransform.Translation.X,6);
        Assert.Equal(-10,b.Candidate.EnumerateOccurrences().Single(o=>o.Path.Equals(negative.Paths[1])).WorldTransform.Translation.X,6);
        Assert.Equal(AssemblySolveStatus.UnderConstrained,a.Report.Status);Assert.Equal(5,Assert.Single(a.Report.Components).LocalFreedom);
        Assert.False(a.Report.HasGlobalUniquenessProof);Assert.False(b.Report.HasGlobalUniquenessProof);
    }

    [Fact] public void ZeroDistanceReportsUncertainInfinitesimalRankInsteadOfFiniteFreedomProof()
    {
        var sample=Sample(2,Vector3d.Zero);var plan=AssemblySolveCommands.Plan(Pair(sample,AssemblyConstraintKind.Distance,0));
        Assert.Equal(AssemblySolveFailureReason.None,plan.Report.FailureReason);Assert.True(Assert.Single(plan.Report.Components).RankUncertain);
        Assert.Equal(AssemblySolveStatus.UnderConstrained,plan.Report.Status);Assert.False(plan.Report.HasGlobalUniquenessProof);
    }

    [Theory][InlineData(.001)][InlineData(1)][InlineData(100)]
    public void ScaledCoincidentAndRedundantRelationsPreserveRotationalNullspace(double scale)
    {
        var sample=Sample(2,new(5*scale,2*scale,0));var doc=Pair(sample,AssemblyConstraintKind.Coincident,0);
        var relation=doc.AssemblyConstraints.Values.Single(c=>c.Kind==AssemblyConstraintKind.Coincident);
        var duplicate=AssemblyConstraintId.New();doc=doc with{AssemblyConstraints=doc.AssemblyConstraints.Add(duplicate,relation with{Id=duplicate,Name="Duplicate"})};
        var plan=AssemblySolveCommands.Plan(doc);Assert.Equal(AssemblySolveFailureReason.None,plan.Report.FailureReason);
        var freedom=Assert.Single(plan.Report.Components);Assert.Equal(3,freedom.Rank);Assert.Equal(3,freedom.LocalFreedom);
        Assert.False(freedom.RankUncertain);Assert.Equal(3,freedom.NullspaceModes.Length);Assert.NotEmpty(plan.Report.RedundantRelations);
        Assert.All(freedom.NullspaceModes,mode=>Assert.All(mode,motion=>Assert.True(motion.TranslationMm.Length<1e-6)));
        Assert.All(plan.Report.Residuals,r=>Assert.Equal(AssemblyConstraintStatus.Satisfied,r.Status));
    }

    [Fact] public void IterationAndTimeBudgetsAreDistinctAndReturnTheExactInput()
    {
        var sample=Sample(2,new(40,0,0));var doc=Pair(sample,AssemblyConstraintKind.Coincident,0);
        var iteration=AssemblySolveCommands.Plan(doc,options:new(MaxIterations:1));
        Assert.Equal(AssemblySolveFailureReason.IterationLimit,iteration.Report.FailureReason);Assert.Same(doc,iteration.Candidate);
        Assert.Equal(1,iteration.Report.Iterations);
        var time=AssemblySolveCommands.Plan(doc,options:new(TimeBudget:TimeSpan.FromTicks(1)));
        Assert.Equal(AssemblySolveFailureReason.TimeBudget,time.Report.FailureReason);Assert.Same(doc,time.Candidate);
        using var canceled=new CancellationTokenSource();canceled.Cancel();Assert.Throws<OperationCanceledException>(()=>AssemblySolveCommands.Plan(doc,canceled.Token));
        Assert.Throws<ArgumentOutOfRangeException>(()=>AssemblySolveCommands.Plan(doc,options:new(MaxInstances:33)));
        var satisfied=Pair(Sample(2,Vector3d.Zero),AssemblyConstraintKind.Coincident,0);
        var diagnosticBudget=AssemblySolveCommands.Plan(satisfied,options:new(TimeBudget:TimeSpan.FromTicks(1)));
        Assert.Equal(AssemblySolveFailureReason.TimeBudget,diagnosticBudget.Report.FailureReason);Assert.Same(satisfied,diagnosticBudget.Candidate);
    }

    [Fact] public void InstanceAndRelationLimitsRejectWithoutPartialChanges()
    {
        var sample=Sample(33,new(.1,.1,0));var doc=Ground(sample.Document,sample.Paths[0]);
        foreach(var path in sample.Paths.Skip(1))doc=AddPair(doc,sample.Paths[0],path,AssemblyConstraintKind.Coincident,0);
        var count=AssemblySolveCommands.Plan(doc);Assert.Equal(AssemblySolveFailureReason.LimitExceeded,count.Report.FailureReason);Assert.Same(doc,count.Candidate);
        sample=Sample(2,new(.1,.1,0));doc=Ground(sample.Document,sample.Paths[0]);
        for(int i=0;i<128;i++)doc=AddPair(doc,sample.Paths[0],sample.Paths[1],AssemblyConstraintKind.Coincident,0);
        var relations=AssemblySolveCommands.Plan(doc);Assert.Equal(AssemblySolveFailureReason.LimitExceeded,relations.Report.FailureReason);Assert.Same(doc,relations.Candidate);
    }

    [Fact] public void SupportedSharedDefinitionScaleConvergesWithCompleteLocalEvidence()
    {
        var sample=Sample(32,new(.1,.1,0));var doc=Ground(sample.Document,sample.Paths[0]);
        foreach(var path in sample.Paths.Skip(1))doc=AddPair(doc,sample.Paths[0],path,AssemblyConstraintKind.Coincident,0);
        var plan=AssemblySolveCommands.Plan(doc);
        output.WriteLine($"32 shared-definition instances: reason={plan.Report.FailureReason}; iterations={plan.Report.Iterations}; elapsedMs={plan.Report.ElapsedMs}; freedom={plan.Report.Components.Sum(c=>c.LocalFreedom)}");
        Assert.Equal(AssemblySolveFailureReason.None,plan.Report.FailureReason);Assert.Equal(32,plan.Report.Residuals.Length);
        Assert.All(plan.Report.Residuals,r=>Assert.Equal(AssemblyConstraintStatus.Satisfied,r.Status));
        Assert.Equal(93,Assert.Single(plan.Report.Components).LocalFreedom);
        Assert.Equal(doc.Definitions.Count,plan.Candidate.Definitions.Count);Assert.False(plan.Report.HasGlobalUniquenessProof);
    }

    [Fact] public void MaximumSupportedRelationCountConvergesAndReportsDuplicateRedundancy()
    {
        var sample=Sample(32,new(.1,.1,0));var doc=Ground(sample.Document,sample.Paths[0]);
        foreach(var path in sample.Paths.Skip(1))for(int duplicate=0;duplicate<4;duplicate++)
            doc=AddPair(doc,sample.Paths[0],path,AssemblyConstraintKind.Coincident,0);
        foreach(var path in sample.Paths.Skip(1).Take(3))doc=AddPair(doc,sample.Paths[0],path,AssemblyConstraintKind.Distance,0);
        Assert.Equal(128,doc.AssemblyConstraints.Count);var plan=AssemblySolveCommands.Plan(doc);
        output.WriteLine($"32 instances / 128 relations: reason={plan.Report.FailureReason}; iterations={plan.Report.Iterations}; elapsedMs={plan.Report.ElapsedMs}");
        Assert.Equal(AssemblySolveFailureReason.None,plan.Report.FailureReason);Assert.Equal(128,plan.Report.Residuals.Length);
        Assert.All(plan.Report.Residuals,r=>Assert.Equal(AssemblyConstraintStatus.Satisfied,r.Status));
        Assert.Equal(127,plan.Report.RedundantRelations.Length);Assert.True(Assert.Single(plan.Report.Components).RankUncertain);
    }

    [Fact] public async Task ConvergedPoseRoundtripsWithOneUndoWhileImpossibleFixedDriversRemainAtomic()
    {
        var sample=Sample(3,new(5,2,0));var doc=Pair(sample,AssemblyConstraintKind.Coincident,0);
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var command=AssemblySolveCommands.Solve();await session.ExecuteAsync(command);Assert.Equal(AssemblySolveFailureReason.None,command.Report!.FailureReason);
        using var files=new TestFiles();var path=files.PathFor("solver.cadoryx");await session.SaveAsync(new CadDocumentStorage(),path);
        using(var reopened=await new CadDocumentStorage().LoadAsync(path,assets))Assert.All(reopened.Snapshot.AssemblyConstraints.Values,c=>Assert.Equal(AssemblyConstraintStatus.Satisfied,c.Evaluate(reopened.Snapshot).Status));
        await session.UndoAsync();Assert.Same(doc,session.Snapshot);
        doc=Ground(doc,sample.Paths[2]);doc=AddPair(doc,sample.Paths[2],sample.Paths[1],AssemblyConstraintKind.Coincident,0);
        var failed=AssemblySolveCommands.Plan(doc);Assert.Same(doc,failed.Candidate);Assert.Equal(AssemblySolveFailureReason.NoConvergence,failed.Report.FailureReason);
        Assert.Contains(failed.Report.Residuals,r=>r.Status==AssemblyConstraintStatus.Unsatisfied);
    }

    [Fact] public async Task PreparedSolveCommitsTheDisplayedPoseAndRejectsUndoBackToTheSameSnapshot()
    {
        var sample=Sample(2,new(5,2,0));var doc=Pair(sample,AssemblyConstraintKind.Coincident,0);
        var assets=new MemoryAssetStore();var kernel=new OcctGeometryKernel();
        await using var session=new CadDocumentSession(doc,assets,kernel,new InlineSessionDispatcher());
        var prepared=await AssemblySolveCommands.PreparePreviewAsync(new(doc,session.Generation,assets,kernel));
        Assert.Same(doc,session.Snapshot);await session.ExecuteAsync(prepared);
        Assert.Equal(prepared.Candidate.Definitions,session.Snapshot.Definitions);
        await session.UndoAsync();Assert.Same(doc,session.Snapshot);
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(prepared));Assert.Same(doc,session.Snapshot);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>AssemblySolveCommands.PreparePreviewAsync(new(doc,session.Generation,assets,kernel),cancel.Token));
    }

    private sealed record Instances(DocumentSnapshot Document,OccurrencePath[] Paths);
    private static Instances Sample(int count,Vector3d spacing)
    {
        var doc=DocumentSnapshot.Create("Convergence");var part=DefinitionId.New();var root=(AssemblyDefinition)doc.Definitions[doc.RootAssemblyId];
        var slots=Enumerable.Range(0,count).Select(i=>new ComponentSlot(ComponentSlotId.New(),part,"Part "+i,new RigidTransform3d(spacing*i,Quaterniond.Identity))).ToImmutableArray();
        doc=doc with{Definitions=doc.Definitions.Add(part,new PartDefinition(part,"Shared part",[],[])).SetItem(root.Id,root with{Children=slots})};
        return new(doc,slots.Select(s=>new OccurrencePath(doc.Id,[s.Id])).ToArray());
    }
    private static DocumentSnapshot Ground(DocumentSnapshot doc,OccurrencePath path)
    {
        var occurrence=doc.EnumerateOccurrences().Single(o=>o.Path.Equals(path));var id=AssemblyConstraintId.New();
        return doc with{AssemblyConstraints=doc.AssemblyConstraints.Add(id,new(id,"Ground",AssemblyConstraintKind.Fixed,path,occurrence.DefinitionId,FixedWorld:occurrence.WorldTransform))};
    }
    private static DocumentSnapshot Pair(Instances sample,AssemblyConstraintKind kind,double target)=>
        AddPair(Ground(sample.Document,sample.Paths[0]),sample.Paths[0],sample.Paths[1],kind,target);
    private static DocumentSnapshot AddPair(DocumentSnapshot doc,OccurrencePath a,OccurrencePath b,AssemblyConstraintKind kind,double target)
    {
        var occurrences=doc.EnumerateOccurrences().ToDictionary(o=>o.Path);var id=AssemblyConstraintId.New();
        return doc with{AssemblyConstraints=doc.AssemblyConstraints.Add(id,new(id,"Relation",kind,a,occurrences[a].DefinitionId,b,occurrences[b].DefinitionId,TargetDistanceMm:target))};
    }
}
