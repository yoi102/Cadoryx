using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Xunit;

namespace Cadoryx.Tests;

public sealed class HistoryPathTests
{
    [Fact] public void UniqueRouteRetainsEachOperandSlotAndIgnoresUnrelatedFeatures()
    {
        var g=new Graph();var source=g.Add();var side=g.Add();var first=g.Add(source,side);var last=g.Add(side,first);
        g.Add(FeatureId.New()); // Outside the target ancestry, so not inspected.
        var plan=TopologyHistoryPaths.Plan(g.Snapshot,source,last);
        Assert.Null(plan.Failure);Assert.Equal(new[]{new HistoryPathStep(first,0),new HistoryPathStep(last,1)},plan.Steps.ToArray());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void BranchesRejoiningRemainAmbiguousRegardlessOfInputOrder(bool reverse)
    {
        var g=new Graph();var source=g.Add();var left=g.Add(source);var right=g.Add(source);
        var join=reverse?g.Add(right,left):g.Add(left,right);var last=g.Add(join);
        Failure(g,source,last,HistoryResolutionStatus.Ambiguous,"MULTIPLE_PATHS");
    }
    [Fact] public void ExponentiallyManyRoutesAreCountedWithoutEnumeratingPaths()
    {
        var g=new Graph();var source=g.Add();var left=source;var right=g.Add();
        for(int i=0;i<30;i++){var a=g.Add(left,right);var b=g.Add(left,right);left=a;right=b;}
        Failure(g,source,g.Add(left,right),HistoryResolutionStatus.Ambiguous,"MULTIPLE_PATHS");
    }
    [Theory][InlineData("missing")][InlineData("cycle")][InlineData("duplicate")][InlineData("foreign")][InlineData("no-path")]
    public void InvalidOrAbsentRoutesDoNotChooseAnAvailableAlternative(string mutation)
    {
        var g=new Graph();var source=g.Add();var side=g.Add();var target=g.Add(source,side);
        switch(mutation)
        {
            case "missing":g.Set(side,[FeatureId.New()]);break;
            case "cycle":g.Set(side,[target]);break;
            case "duplicate":g.Set(target,[source,source]);break;
            case "foreign":g.Snapshot=g.Snapshot with{Features=g.Snapshot.Features.SetItem(side,g.Snapshot.Features[side] with{PartId=DefinitionId.New()})};break;
            default:g.Set(target,[side]);break;
        }
        string code=mutation switch{"missing"=>"MISSING_FEATURE","cycle"=>"CYCLIC_PATH","duplicate"=>"DUPLICATE_INPUT","foreign"=>"FOREIGN_PART",_=>"NO_PATH"};
        Failure(g,source,target,mutation=="missing"?HistoryResolutionStatus.Missing:mutation=="no-path"?HistoryResolutionStatus.Unsupported:HistoryResolutionStatus.WrongContext,code);
    }
    [Theory][InlineData(32,true)][InlineData(33,false)]
    public void PathBudgetHasAnExactBoundary(int length,bool permitted)
    {
        var g=new Graph();var source=g.Add();var target=source;for(int i=0;i<length;i++)target=g.Add(target);
        var plan=TopologyHistoryPaths.Plan(g.Snapshot,source,target);
        if(permitted){Assert.Null(plan.Failure);Assert.Equal(length,plan.Steps.Length);}
        else Failure(g,source,target,HistoryResolutionStatus.LimitExceeded,"PATH_LENGTH_LIMIT");
    }
    [Theory][InlineData("depth")][InlineData("nodes")][InlineData("edges")]
    public void GraphBudgetsAlsoIncludeSideInputs(string budget)
    {
        var g=new Graph();var source=g.Add();FeatureId target;
        if(budget=="depth")
        {var side=g.Add();for(int i=0;i<129;i++)side=g.Add(side);target=g.Add(source,side);}
        else if(budget=="nodes")target=g.Add([source,..Enumerable.Range(0,1024).Select(_=>g.Add())]);
        else
        {var leaves=Enumerable.Range(0,80).Select(_=>g.Add()).ToArray();var parents=Enumerable.Range(0,70).Select(_=>g.Add(leaves)).ToArray();target=g.Add([source,..parents]);}
        Failure(g,source,target,HistoryResolutionStatus.LimitExceeded,"GRAPH_"+(budget=="nodes"?"NODE":budget=="edges"?"EDGE":"DEPTH")+"_LIMIT");
    }
    [Fact] public void CancelledPlannerNeverStartsTraversal()
    {
        var g=new Graph();var source=g.Add();using var cts=new CancellationTokenSource();cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(()=>TopologyHistoryPaths.Plan(g.Snapshot,source,g.Add(source),cts.Token));
    }
    private static void Failure(Graph g,FeatureId source,FeatureId target,HistoryResolutionStatus status,string code)
    {
        var plan=TopologyHistoryPaths.Plan(g.Snapshot,source,target);Assert.Empty(plan.Steps);
        Assert.Equal(status,plan.Failure!.Status);Assert.Equal("HISTORY."+code,plan.Failure.Diagnostic);Assert.Null(plan.Failure.Target);
    }
    // Only graph structure is under test; synthetic geometry never reaches a native resolver.
    private sealed class Graph
    {
        public DocumentSnapshot Snapshot=DocumentSnapshot.Create("Path graph");private readonly DefinitionId part=DefinitionId.New();
        public FeatureId Add(params FeatureId[] inputs)
        {
            var id=FeatureId.New();var geometry=new GeometryAssetRef(new(new string('a',64)),GeometryRevisionId.New(),BodyKind.Solid,new(new(0,0,0),new(1,1,1)),1);
            var f=new FeatureDefinition(id,part,"Node",new BoxRecipe(1,1,1,RigidTransform3d.Identity),[..inputs],BodyId.New(),geometry);
            Snapshot=Snapshot with{Features=Snapshot.Features.Add(id,f)};return id;
        }
        public void Set(FeatureId id,ImmutableArray<FeatureId> inputs)=>Snapshot=Snapshot with{Features=Snapshot.Features.SetItem(id,Snapshot.Features[id] with{Inputs=inputs})};
    }
}
