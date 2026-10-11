using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using MessagePack;
using Xunit;

namespace Cadoryx.Tests;

public sealed class SketchPointOriginTests
{
    [Fact] public void BorrowingAnIndependentPointDoesNotReinterpretItsOrigin()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Origins",RigidTransform3d.Identity));d.AddPoint(new(0,0));var point=d.Value.Points.Single();
        d.AddLine(new(0,0),new(1,0),point.Id);Assert.Equal(SketchPointOrigin.Independent,d.Value.Points.Single(p=>p.Id==point.Id).Origin);
        Assert.All(d.Value.Points.Where(p=>p.Id!=point.Id),p=>Assert.Equal(SketchPointOrigin.CurveGenerated,p.Origin));
        var id=d.AddExpression(SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(0,1))));d.AttachExpressionEndpoints(id,point.Id);
        Assert.Equal(SketchPointOrigin.Independent,d.Value.Points.Single(p=>p.Id==point.Id).Origin);d.Value.Validate();
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void ExactTrimPreservesIndependentEndpointAndItsUserConstraint(bool spline)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Borrowed point",RigidTransform3d.Identity));d.AddPoint(new(0,0));var point=d.Value.Points.Single();
        var fix=new FixPointConstraint(SketchConstraintId.New(),point.Id,point.Position);d.Change(s=>s with{Constraints=[fix]});
        if(spline)d.AddSpline([new(0,0),new(.3,1),new(.7,1),new(1,0)],existingStart:point.Id);else d.AddBezier(new(0,0),new(.5,1),new(1,0),existingStart:point.Id);
        var before=d.Value;if(spline)d.TrimSpline(before.Splines.Single().Id,.2,1);else d.TrimBezier(before.Beziers.Single().Id,.2,1);
        Assert.Contains(point,d.Value.Points);Assert.Contains(fix,d.Value.Constraints);d.Value.Validate();d.Undo();Assert.Same(before,d.Value);d.Redo();Assert.Contains(point,d.Value.Points);
    }
    [Theory][InlineData(false)][InlineData(true)]
    public void TrimmingAnotherPrimitiveNeverDeletesAnExpressionStructuralEndpoint(bool spline)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Shared expression point",RigidTransform3d.Identity));
        var id=d.AddExpression(SketchCurveExpressionGeometry.FromBoundary(new(new(0,0),new(0,1))));d.SetExpressionConstruction(id,false);
        var c=d.Value.ExpressionCurves.Single();var ends=c.Endpoints!;var point=d.Value.Points.Single(p=>p.Id==ends.Start);
        if(spline)d.AddSpline([new(0,0),new(.3,1),new(.7,1),new(1,0)],existingStart:point.Id);else d.AddBezier(new(0,0),new(.5,1),new(1,0),existingStart:point.Id);
        if(spline)d.TrimSpline(d.Value.Splines.Single().Id,.2,1);else d.TrimBezier(d.Value.Beziers.Single().Id,.2,1);
        Assert.Contains(point,d.Value.Points);Assert.Equal(ends,d.Value.ExpressionCurves.Single().Endpoints);SketchAssociativeEndpoints.RequireSolved(d.Value);d.Value.Validate();
    }
    [Fact] public void UnknownLegacyControlPointsArePreservedUntilTheirOriginIsExplicitlyKnown()
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Unknown points",RigidTransform3d.Identity));d.AddBezier(new(0,0),new(.5,1),new(1,0));
        var old=d.Value with{Points=d.Value.Points.Select(p=>p with{Origin=SketchPointOrigin.Unknown}).ToImmutableArray()};var edit=new SketchDraft(old);edit.TrimBezier(old.Beziers.Single().Id,.2,1);
        Assert.All(old.Points,p=>Assert.Contains(p,edit.Value.Points));edit.Value.Validate();
    }
    [Theory][InlineData(SketchPointOrigin.Unknown)][InlineData(SketchPointOrigin.CurveGenerated)]
    public void AnEditCannotTurnAnIndependentPointIntoAnAutomaticallyDeletablePoint(SketchPointOrigin origin)
    {
        var d=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Stable intent",RigidTransform3d.Identity));d.AddPoint(new(0,0));var before=d.Value;
        Assert.Throws<CadValidationException>(()=>d.Change(s=>s with{Points=[s.Points.Single() with{Origin=origin}]}));Assert.Same(before,d.Value);
    }
    [Fact] public async Task InjectableSolverCannotRewritePointOrigin()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Solver origins",RigidTransform3d.Identity));d.AddPoint(new(0,0));
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(d.Value,new CorruptOrigin())));Assert.Same(initial,session.Snapshot);Assert.Equal(0,assets.Count);
    }
    private sealed class CorruptOrigin:ISketchConstraintSolver
    {
        public string Version=>"corrupt-origin";
        public async Task<SketchSolveReport> SolveAsync(CadSketch sketch,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        {var result=await new ManagedSketchConstraintSolver().SolveAsync(sketch,options,cancellationToken);return result with{Solution=result.Solution! with{Points=[result.Solution.Points.Single() with{Origin=SketchPointOrigin.CurveGenerated}]}};}
    }
    private static async Task<(CadDocumentSession Session,MemoryAssetStore Assets,CadSketch Sketch)> Stored()
    {
        var(initial,part)=SketchCommandTests.Document();var assets=new MemoryAssetStore();var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        var d=new SketchDraft(CadSketch.Create(part,"Stored origins",RigidTransform3d.Identity));d.AddPoint(new(4,4));d.AddLine(new(0,0),new(1,0));
        await session.ExecuteAsync(new UpsertSketchCommand(d.Value,new ManagedSketchConstraintSolver()));return(session,assets,session.Snapshot.Sketches[d.Value.Id]);
    }
    [Fact] public async Task OriginValuesRoundtripAndRecoverWithoutChangingGeometryOrIdentities()
    {
        using var files=new TestFiles();var(session,assets,sketch)=await Stored();await using(session)
        {
            var storage=new CadDocumentStorage();var path=files.PathFor("origin.cadoryx");await session.SaveAsync(storage,path);
            using(var loaded=await storage.LoadAsync(path,assets))Assert.Equal(sketch.Points.ToArray(),loaded.Snapshot.Sketches[sketch.Id].Points.ToArray());
            var manifest=FormatEvolutionTests.Manifest(path);Assert.Equal("0.7.8",manifest.ApplicationVersion);Assert.Equal(60,manifest.Sections.Single(s=>s.Kind=="document").SchemaVersion);Assert.Equal(22,manifest.Sections.Single(s=>s.Kind=="sketches").SchemaVersion);
            RecoveryKey key;using(var recovery=new CadRecoveryStore(files.PathFor("recovery"),storage)){var id=Guid.NewGuid();key=new(recovery.RunId,id);await recovery.WriteAsync(id,session.Snapshot,assets,null);}
            using var reader=new CadRecoveryStore(files.PathFor("recovery"),storage);using var recovered=await reader.OpenAsync(key,assets);Assert.Equal(sketch.Points.ToArray(),recovered.Document.Snapshot.Sketches[sketch.Id].Points.ToArray());
        }Assert.Equal(0,assets.Count);
    }
    [Fact] public async Task ActualLegacyFourFieldPointsMigrateAsUnknownWithEveryOldValueRetained()
    {
        using var files=new TestFiles();var(session,assets,sketch)=await Stored();await using(session)
        {
            var storage=new CadDocumentStorage();var path=files.PathFor("legacy-origin.cadoryx");await session.SaveAsync(storage,path);
            FormatEvolutionTests.RewriteSection(path,"sketches",bytes=>
            {var root=JsonNode.Parse(MessagePackSerializer.ConvertToJson(bytes))!;foreach(var s in root[0]!.AsArray())foreach(var p in s![4]!.AsArray())p!.AsArray().RemoveAt(4);return MessagePackSerializer.ConvertFromJson(root.ToJsonString());});
            FormatEvolutionTests.RewriteManifest(path,m=>m with{ApplicationVersion="0.7.4",RequiredCapabilities=m.RequiredCapabilities.Where(c=>c!="cadoryx.sketch-point-origins.1").ToImmutableArray(),Sections=m.Sections.Select(s=>s.Kind=="document"?s with{SchemaVersion=56}:s.Kind=="sketches"?s with{SchemaVersion=21}:s).ToImmutableArray()});
            using var loaded=await storage.LoadAsync(path,assets);var actual=loaded.Snapshot.Sketches[sketch.Id];Assert.All(actual.Points,p=>Assert.Equal(SketchPointOrigin.Unknown,p.Origin));
            Assert.Equal(sketch.Points.Select(p=>(p.Id,p.Position,p.IsConstruction)),actual.Points.Select(p=>(p.Id,p.Position,p.IsConstruction)));Assert.Equal(sketch.Lines.ToArray(),actual.Lines.ToArray());
        }Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("enum")][InlineData("missing")][InlineData("extra")][InlineData("sketch-version")][InlineData("document-version")][InlineData("both-versions")]
    public async Task MalformedOrDowngradedOriginMetadataRejectsAtomically(string fault)
    {
        using var files=new TestFiles();var(session,assets,sketch)=await Stored();await using(session)
        {
            var storage=new CadDocumentStorage();var path=files.PathFor("bad-origin.cadoryx");await session.SaveAsync(storage,path);var before=session.Snapshot;
            if(fault is "enum" or "missing" or "extra")FormatEvolutionTests.RewriteSection(path,"sketches",bytes=>
            {var root=JsonNode.Parse(MessagePackSerializer.ConvertToJson(bytes))!;var point=root[0]![0]![4]![0]!.AsArray();if(fault=="enum")point[4]=3;else if(fault=="missing")point.RemoveAt(4);else point.Add(0);return MessagePackSerializer.ConvertFromJson(root.ToJsonString());});
            else FormatEvolutionTests.RewriteManifest(path,m=>m with{Sections=m.Sections.Select(s=>s.Kind=="document"&&fault is "document-version" or "both-versions"?s with{SchemaVersion=56}:s.Kind=="sketches"&&fault is "sketch-version" or "both-versions"?s with{SchemaVersion=21}:s).ToImmutableArray()});
            await Assert.ThrowsAsync<InvalidDataException>(()=>storage.LoadAsync(path,assets));Assert.Same(before,session.Snapshot);Assert.Equal(sketch.Points.ToArray(),session.Snapshot.Sketches[sketch.Id].Points.ToArray());Assert.Equal(0,assets.Count);
        }
    }
}
