using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Xunit;

namespace Cadoryx.Tests;

public sealed class TopologyHistoryCompositionTests
{
    private static TopologyHistory First(params TopologyHistoryEntry[] entries)=>new(GeometryRevisionId.New(),new(new string('a',64)),
        GeometryRevisionId.New(),new(new string('b',64)),"native",new string('1',64),new string('2',64),8,8,entries.ToImmutableArray());
    private static TopologyHistory Next(TopologyHistory before,params TopologyHistoryEntry[] entries)=>new(before.ResultRevision,before.ResultAsset,
        GeometryRevisionId.New(),new(new string('c',64)),"native",before.ResultFingerprint,new string('3',64),before.ResultCount,8,entries.ToImmutableArray());
    private static TopologyHistoryEntry Link(int source,int target,TopologyEvolution kind=TopologyEvolution.Unchanged)=>new(source,HistoryShapeKind.Face,kind,target,HistoryShapeKind.Face);
    [Fact]
    public void ExactAdjacentWitnessesPreserveUnchangedAndModifiedRelations()
    {
        var first=First(Link(0,2),Link(1,3,TopologyEvolution.Modified));var second=Next(first,Link(2,6),Link(3,5));
        var result=TopologyHistoryComposition.Compose([first,second],"composed");
        Assert.Equal(3,result.SchemaVersion);Assert.Equal(first.SourceAsset,result.SourceAsset);Assert.Equal(second.ResultAsset,result.ResultAsset);
        Assert.Equal(TopologyEvolution.Unchanged,result.Entries.Single(e=>e.SourceIndex==0).Evolution);
        Assert.Equal(TopologyEvolution.Modified,result.Entries.Single(e=>e.SourceIndex==1).Evolution);
        Assert.Equal(6,TopologyHistoryReduction.Resolve(result,0,HistoryShapeKind.Face).Target!.FullTopologyIndex);
    }
    [Theory][InlineData("revision")][InlineData("asset")][InlineData("fingerprint")][InlineData("count")]
    public void EveryIntermediateIdentityFieldMustMatch(string mutation)
    {
        var first=First(Link(0,2));var second=Next(first,Link(2,6));
        second=mutation switch{"revision"=>second with{SourceRevision=GeometryRevisionId.New()},"asset"=>second with{SourceAsset=new(new string('d',64))},
            "fingerprint"=>second with{SourceFingerprint=new string('4',64)},_=>second with{SourceCount=9}};
        Assert.Throws<CadValidationException>(()=>TopologyHistoryComposition.Compose([first,second],"composed"));
    }
    [Theory][InlineData("split",HistoryResolutionStatus.Ambiguous)][InlineData("merge",HistoryResolutionStatus.Ambiguous)]
    [InlineData("deleted",HistoryResolutionStatus.Deleted)][InlineData("generated",HistoryResolutionStatus.Generated)]
    [InlineData("unmapped",HistoryResolutionStatus.Unsupported)][InlineData("missing",HistoryResolutionStatus.Unsupported)]
    public void StoppedSourcesCannotReappearAfterLaterRecombination(string mutation,HistoryResolutionStatus status)
    {
        var first=mutation switch
        {
            "split"=>First(Link(0,2,TopologyEvolution.Modified),Link(0,3,TopologyEvolution.Modified)),
            "merge"=>First(Link(0,2,TopologyEvolution.Modified),Link(1,2,TopologyEvolution.Modified)),
            "deleted"=>First(new TopologyHistoryEntry(0,HistoryShapeKind.Face,TopologyEvolution.Deleted,null,null)),
            "generated"=>First(Link(0,2,TopologyEvolution.Generated)),
            "unmapped"=>First(new TopologyHistoryEntry(0,HistoryShapeKind.Face,TopologyEvolution.Unmapped,null,null)),
            _=>First(Link(0,2))
        };
        var second=mutation=="missing"?Next(first,Link(3,6)):Next(first,Link(2,6),Link(3,6));
        var result=TopologyHistoryComposition.Compose([first,second],"composed");
        var resolution=TopologyHistoryReduction.Resolve(result,0,HistoryShapeKind.Face);Assert.Equal(status,resolution.Status);Assert.Null(resolution.Target);
        var last=Next(result,Link(0,7));var again=TopologyHistoryComposition.Compose([result,last],"composed");
        Assert.Equal(status,TopologyHistoryReduction.Resolve(again,0,HistoryShapeKind.Face).Status);
    }
    [Fact]
    public void SecondaryToolOwnershipCannotBeDroppedBeforeComposition()
    {
        var first=First(Link(0,2),Link(0,2) with{SourceArgument=1}) with{SchemaVersion=2,AdditionalSources=[new(GeometryRevisionId.New(),new(new string('e',64)),new string('5',64),8)]};
        var second=Next(first,Link(2,6));var result=TopologyHistoryComposition.Compose([first,second],"composed");
        Assert.Empty(result.AdditionalSources);Assert.Equal(HistoryResolutionStatus.Ambiguous,TopologyHistoryReduction.Resolve(result,0,HistoryShapeKind.Face).Status);
    }
    [Fact]
    public void LegacySchemasRejectNewTerminalMarkersAndCancellationStopsBeforeWork()
    {
        var first=First(Link(0,2));var second=Next(first,Link(2,6));
        Assert.Throws<CadValidationException>(()=>(first with{Entries=[new(0,HistoryShapeKind.Face,TopologyEvolution.Ambiguous,null,null)]}).Validate());
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        Assert.Throws<OperationCanceledException>(()=>TopologyHistoryComposition.Compose([first,second],"composed",cancel.Token));
        Assert.Throws<CadValidationException>(()=>TopologyHistoryComposition.Compose([first],"composed"));
    }
}
