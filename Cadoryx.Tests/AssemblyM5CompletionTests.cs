using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Xunit;

namespace Cadoryx.Tests;

public sealed class AssemblyM5CompletionTests
{
    private static (DocumentSnapshot Document,OccurrencePath A,OccurrencePath B,OccurrencePath C) Three()
    {
        var document=DocumentSnapshot.Create("M5 relations");
        var root=(AssemblyDefinition)document.Definitions[document.RootAssemblyId];
        var a=DefinitionId.New();var b=DefinitionId.New();var c=DefinitionId.New();
        var sa=ComponentSlotId.New();var sb=ComponentSlotId.New();var sc=ComponentSlotId.New();
        document=document with{Definitions=document.Definitions
            .Add(a,new PartDefinition(a,"A",[],[]))
            .Add(b,new PartDefinition(b,"B",[],[]))
            .Add(c,new PartDefinition(c,"C",[],[]))
            .SetItem(root.Id,root with{Children=[
                new(sa,a,"A",RigidTransform3d.Identity),
                new(sb,b,"B",RigidTransform3d.Translate(10,0,5)),
                new(sc,c,"C",RigidTransform3d.Translate(20,0,8))]})};
        return (document,new(document.Id,[sa]),new(document.Id,[sb]),new(document.Id,[sc]));
    }

    [Theory]
    [InlineData(AssemblyConstraintKind.AngleAxes,45)]
    [InlineData(AssemblyConstraintKind.PlanarMate,0)]
    public async Task DatumAdjustmentUndoAndStorageRoundtrip(AssemblyConstraintKind kind,double degrees)
    {
        var seed=Three();using var files=new TestFiles();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(seed.Document,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var id=AssemblyConstraintId.New();
        await session.ExecuteAsync(AssemblyConstraintCommands.AddAxisPair(id,"Datum",kind,seed.A,seed.B,
            Vector3d.Zero,Vector3d.Zero,Vector3d.UnitZ,Vector3d.UnitZ,degrees*Math.PI/180));
        Assert.Equal(AssemblyConstraintStatus.Unsatisfied,session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
        await session.ExecuteAsync(AssemblyConstraintCommands.AdjustPair(id));
        Assert.Equal(AssemblyConstraintStatus.Satisfied,session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
        await session.UndoAsync();
        Assert.Equal(AssemblyConstraintStatus.Unsatisfied,session.Snapshot.AssemblyConstraints[id].Evaluate(session.Snapshot).Status);
        await session.RedoAsync();
        var path=files.PathFor("datum.cadoryx");var storage=new CadDocumentStorage();
        await session.SaveAsync(storage,path);
        using var reopened=await storage.LoadAsync(path,assets);
        Assert.Equal(kind,reopened.Snapshot.AssemblyConstraints[id].Kind);
        Assert.Equal(AssemblyConstraintStatus.Satisfied,reopened.Snapshot.AssemblyConstraints[id].Evaluate(reopened.Snapshot).Status);
    }

    [Fact] public async Task DirectedSolveCommitsBothEdgesAndReportsFreeRoot()
    {
        var seed=Three();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(seed.Document,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var ab=AssemblyConstraintId.New();var bc=AssemblyConstraintId.New();
        await session.ExecuteAsync(AssemblyConstraintCommands.AddPair(ab,"AB",AssemblyConstraintKind.Coincident,
            seed.A,seed.B,Vector3d.Zero,Vector3d.Zero,0));
        await session.ExecuteAsync(AssemblyConstraintCommands.AddPair(bc,"BC",AssemblyConstraintKind.Coincident,
            seed.B,seed.C,Vector3d.Zero,Vector3d.Zero,0));
        var plan=AssemblySolveCommands.Plan(session.Snapshot);
        Assert.Equal(AssemblySolveStatus.UnderConstrained,plan.Report.Status);
        Assert.Equal(2,plan.Report.Adjusted.Length);
        Assert.Contains(seed.A,plan.Report.UnanchoredRoots);
        var command=AssemblySolveCommands.Solve();
        await session.ExecuteAsync(command);
        Assert.All(session.Snapshot.AssemblyConstraints.Values,c=>Assert.Equal(
            AssemblyConstraintStatus.Satisfied,c.Evaluate(session.Snapshot).Status));
        await session.UndoAsync();
        Assert.Equal(AssemblyConstraintStatus.Unsatisfied,session.Snapshot.AssemblyConstraints[ab].Evaluate(session.Snapshot).Status);
    }

    [Fact] public async Task CompetingDriversCanSolveTogetherWhenGraphHasFreeMotion()
    {
        var seed=Three();var assets=new MemoryAssetStore();
        await using var session=new CadDocumentSession(seed.Document,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        await session.ExecuteAsync(AssemblyConstraintCommands.AddPair(AssemblyConstraintId.New(),"AB",
            AssemblyConstraintKind.Coincident,seed.A,seed.B,Vector3d.Zero,Vector3d.Zero,0));
        await session.ExecuteAsync(AssemblyConstraintCommands.AddPair(AssemblyConstraintId.New(),"CB",
            AssemblyConstraintKind.Coincident,seed.C,seed.B,Vector3d.Zero,Vector3d.Zero,0));
        var before=session.Snapshot;
        var plan=AssemblySolveCommands.Plan(before);
        Assert.Equal(AssemblySolveStatus.UnderConstrained,plan.Report.Status);
        Assert.All(plan.Candidate.AssemblyConstraints.Values,c=>
            Assert.Equal(AssemblyConstraintStatus.Satisfied,c.Evaluate(plan.Candidate).Status));
        Assert.Same(before,session.Snapshot);
        await session.ExecuteAsync(AssemblySolveCommands.Solve());
        Assert.NotSame(before,session.Snapshot);
    }
}
