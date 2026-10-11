using System.Collections.Immutable;
using System.Reflection;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using MathNet.Numerics.LinearAlgebra;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchArcRadiusTests
{
    private static Point2d P(CadSketch s,SketchEntityId id)=>s.Points.Single(p=>p.Id==id).Position;
    internal static double Radius(CadSketch s,SketchArc a)=>SketchArcGeometry.Through(P(s,a.Start),P(s,a.Middle),P(s,a.End)).Radius;
    [Theory] [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void CircumradiusAndEqualRadiusJacobiansMatchCentralDifferences(bool equal,bool reversed)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Jacobian",RigidTransform3d.Identity));
        d.AddArc(reversed?new(7,6):new(1,2),new(3,7),reversed?new(1,2):new(7,6));d.AddCircle(new(12,8),4);
        SketchConstraint constraint=equal?new EqualRadiusConstraint(SketchConstraintId.New(),d.Value.Arcs[0].Id,d.Value.Circles[0].Id):new RadiusConstraint(SketchConstraintId.New(),d.Value.Arcs[0].Id,4);
        var sketch=d.Value with{Constraints=[constraint]};var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchEquationSystem")!;
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Instance;var system=Activator.CreateInstance(type,flags,null,[sketch,new SketchSolveOptions()],null)!;
        var x=(double[])type.GetProperty("Initial",flags)!.GetValue(system)!;var jac=(Matrix<double>)type.GetMethod("Jacobian",flags)!.Invoke(system,[x,CancellationToken.None])!;
        var residual=type.GetMethod("Residual",flags)!;
        for(int col=0;col<x.Length;col++)
        {
            var plus=(double[])x.Clone();var minus=(double[])x.Clone();const double h=1e-6;plus[col]+=h;minus[col]-=h;
            double numeric=(((double[])residual.Invoke(system,[plus])!)[0]-((double[])residual.Invoke(system,[minus])!)[0])/(2*h);
            Assert.InRange(Math.Abs(numeric-jac[0,col]),0,Math.Max(1,Math.Abs(numeric))*2e-6);
        }
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task RadiusDrivesMinorAndMajorArcsWithoutFlippingAuthoredWinding(bool major)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Arc dimension",RigidTransform3d.Identity));
        d.AddArc(new(0,0),new(2,major?8:1),new(4,0));var arc=d.Value.Arcs[0];
        d.Change(s=>s with{Constraints=[new FixPointConstraint(SketchConstraintId.New(),arc.Start,new(0,0)),
            new FixPointConstraint(SketchConstraintId.New(),arc.End,new(4,0)),new OffsetXConstraint(SketchConstraintId.New(),arc.Start,arc.Middle,2),
            new RadiusConstraint(SketchConstraintId.New(),arc.Id,3)]});
        var report=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(report.Succeeded,report.Diagnostic);
        Assert.Equal(3,Radius(report.Solution!,arc),6);Assert.Equal(0,report.DegreesOfFreedom);
        var geometry=SketchArcGeometry.Through(P(report.Solution!,arc.Start),P(report.Solution!,arc.Middle),P(report.Solution!,arc.End));
        Assert.Equal(major,Math.Abs(geometry.SweepAngle)>Math.PI);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task CircleArcAndArcArcEqualRadiusSolve(bool twoArcs)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Equal radius",RigidTransform3d.Identity));d.AddArc(new(0,0),new(2,1),new(4,0));
        SketchEntityId other;
        if(twoArcs){d.AddArc(new(10,0),new(13,3),new(16,0));other=d.Value.Arcs[1].Id;}
        else{d.AddCircle(new(13,0),3);other=d.Value.Circles[0].Id;}
        var arc=d.Value.Arcs[0];
        d.Change(s=>s with{Constraints=[..s.Constraints,new FixPointConstraint(SketchConstraintId.New(),arc.Start,new(0,0)),
            new FixPointConstraint(SketchConstraintId.New(),arc.End,new(4,0)),new OffsetXConstraint(SketchConstraintId.New(),arc.Start,arc.Middle,2),
            new RadiusConstraint(SketchConstraintId.New(),other,3),new EqualRadiusConstraint(SketchConstraintId.New(),other,arc.Id)]});
        var report=await new ManagedSketchConstraintSolver().SolveAsync(d.Value);Assert.True(report.Succeeded,report.Diagnostic);Assert.Equal(3,Radius(report.Solution!,arc),6);
    }
    [Fact] public async Task LockedArcRadiusConflictAndDuplicateRelationAreDiagnosed()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Conflict",RigidTransform3d.Identity));d.AddArc(new(0,0),new(2,1),new(4,0));var arc=d.Value.Arcs[0];
        var radius=new RadiusConstraint(SketchConstraintId.New(),arc.Id,Radius(d.Value,arc));
        d.Change(s=>s with{Constraints=[..s.Points.Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)),radius,radius with{Id=SketchConstraintId.New()}]});
        var solver=new ManagedSketchConstraintSolver();var report=await solver.SolveAsync(d.Value);Assert.True(report.Succeeded);Assert.NotEmpty(report.RedundantConstraints);
        var conflict=d.Value with{Constraints=d.Value.Constraints.Select(c=>c.Id==radius.Id?radius with{Radius=4}:c).ToImmutableArray()};
        Assert.False((await solver.SolveAsync(conflict)).Succeeded);
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task VersionTenMasqueradedArcRadiusEvenDisabledRejectsWhileCircleMigrates(bool enabled)
    {
        using var files=new TestFiles();var (initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var storage=new CadDocumentStorage();
        await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());var solver=new ManagedSketchConstraintSolver();
        var d=new SketchDraft(CadSketch.Create(part,"v11",RigidTransform3d.Identity));d.AddCircle(new(10,10),3);d.AddArc(new(0,0),new(2,1),new(4,0));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var legacy=session.Snapshot;
        d=new(legacy.Sketches[d.Value.Id]);d.Change(s=>s with{Constraints=s.Constraints.Add(new RadiusConstraint(SketchConstraintId.New(),s.Arcs[0].Id,Radius(s,s.Arcs[0]),enabled))});
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,solver));var path=files.PathFor("arc.cadoryx");await session.SaveAsync(storage,path);
        Assert.Equal(CadSectionMigrationRegistry.CurrentFormats["sketches"].Version,FormatEvolutionTests.Manifest(path).Sections.Single(s=>s.Kind=="sketches").SchemaVersion);
        Assert.Contains("cadoryx.sketch-arc-radius.1",FormatEvolutionTests.Manifest(path).RequiredCapabilities);
        using(var loaded=await storage.LoadAsync(path,assets))Assert.Equal(session.Snapshot.Sketches[d.Value.Id].Constraints.ToArray(),loaded.Snapshot.Sketches[d.Value.Id].Constraints.ToArray());
        SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=10}:s).ToImmutableArray()});
        await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));
        await storage.SaveAsync(legacy,assets,path);SketchLegacyPointFixture.Prepare(path);FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="sketches"?s with{SchemaVersion=10}:s).ToImmutableArray()});
        using var migrated=await storage.LoadAsync(path,assets);Assert.Equal(legacy.Sketches[d.Value.Id].Constraints.ToArray(),migrated.Snapshot.Sketches[d.Value.Id].Constraints.ToArray());
    }
}
