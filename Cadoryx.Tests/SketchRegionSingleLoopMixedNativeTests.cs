using System.Collections.Immutable;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.IO;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Kernel.Occt;
using Cadoryx.Sketching;
using Xunit;
using Xunit.Abstractions;

namespace Cadoryx.Tests;

public sealed class SketchRegionSingleLoopMixedNativeTests(ITestOutputHelper output)
{
    private static readonly OcctGeometryKernel Kernel=new();
    public static IEnumerable<object[]> Cases()=>from kind in new[]{"arc-g1","spline-g1","spline-g2"} from mirror in new[]{false,true}
        from distance in new[]{.1,-.1} from style in Enum.GetValues<SketchRegionAuthorJoin>() select new object[]{kind,mirror,distance,style};
    internal static CadSketch NativeMixedBoundary(string kind,bool mirror,double distance,SketchRegionAuthorJoin join)
    {
        const double angle=.31;
        Point2d P(double x,double y){if(mirror)x=-x;return new(3+x*Math.Cos(angle)-y*Math.Sin(angle),-2+x*Math.Sin(angle)+y*Math.Cos(angle));}
        var draft=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Native mixed D",RigidTransform3d.Identity));
        draft.AddLine(P(-4,-2),P(4,-2));var bottom=draft.Value.Lines[0];
        SketchEntityId curved,end;
        if(kind=="arc-g1")
        {
            draft.AddArc(P(4,-2),P(6,0),P(4,2),bottom.End);var arc=draft.Value.Arcs[0];curved=arc.Id;end=arc.End;
        }
        else
        {
            // Clamped cubic with collinear first/last three controls. It has
            // exact horizontal end tangents and zero endpoint curvature for G2.
            draft.AddSpline([P(4,-2),P(5,-2),P(6,-2),P(6,0),P(6,2),P(5,2),P(4,2)],existingStart:bottom.End);
            var spline=draft.Value.Splines[^1];curved=spline.Id;end=spline.Controls[^1];
        }
        draft.AddLine(P(4,2),P(-4,2),end);var top=draft.Value.Lines[^1];
        draft.AddLine(P(-4,2),P(-4,-2),top.End,bottom.Start);
        draft.Change(s=>s with{Constraints=s.Constraints.AddRange(kind=="spline-g2"?new SketchConstraint[]{
            new EndpointCurvatureConstraint(SketchConstraintId.New(),bottom.Id,curved,bottom.End),
            new EndpointCurvatureConstraint(SketchConstraintId.New(),curved,top.Id,end)}:new SketchConstraint[]{
            new EndpointTangentConstraint(SketchConstraintId.New(),bottom.Id,curved,bottom.End),
            new EndpointTangentConstraint(SketchConstraintId.New(),curved,top.Id,end)})});
        return SketchRegionOffsetAuthoring.Create(draft.Value,[bottom.Id],distance,join,Kernel).Sketch;
    }

    [Theory,MemberData(nameof(Cases))]
    public async Task SameLoopNaturalAndNativeConnectionsMoveInOneOriginalBudgetAndBuildTheExactSolid(string kind,bool mirror,double distance,SketchRegionAuthorJoin join)
    {
        var original=NativeMixedBoundary(kind,mirror,distance,join);var group=original.RegionOffsets[0];
        Assert.Equal(2,group.Loops[0].Joints.Count(j=>j.Kind==SketchRegionJointKind.SourceEndpoints));
        Assert.Equal(2,group.Loops[0].Joints.Count(j=>j.Kind!=SketchRegionJointKind.SourceEndpoints));
        var ids=original.Lines.SelectMany(l=>new[]{l.Start,l.End}).Concat(original.Arcs.SelectMany(a=>new[]{a.Start,a.Middle,a.End}))
            .Concat(original.Splines.SelectMany(s=>s.Controls)).ToHashSet();
        const double factor=1.12;
        var authored=original with{Constraints=original.Constraints.AddRange(original.Points.Where(p=>ids.Contains(p.Id))
            .Select(p=>new FixPointConstraint(SketchConstraintId.New(),p.Id,new(2+factor*p.Position.X,-1+factor*p.Position.Y))))};
        authored.Validate();var result=await new ManagedSketchConstraintSolver(Kernel).SolveAsync(authored);
        output.WriteLine($"kind={kind};mirror={mirror};distance={distance:R};join={join};iterations={result.Iterations};diagnostic={result.Diagnostic};residual={result.MaxNormalizedResidual:R};variables={result.VariableCount};equations={result.EquationCount}");
        Assert.True(result.Succeeded,result.Diagnostic);var solved=result.Solution!;
        Assert.Equal(authored.Points.Length*2,result.VariableCount);
        Assert.Equal(ids.Count*2+(kind=="spline-g2"?4:2)+authored.ExpressionCurves.Length*4,result.EquationCount);
        Assert.InRange(result.Iterations,1,80);Assert.InRange(result.VariableCount,1,256);Assert.InRange(result.EquationCount,1,512);
        Assert.Equal(original.Points.Select(p=>p.Id),solved.Points.Select(p=>p.Id));
        Assert.Equal(original.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),solved.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
        Assert.True(SketchRegionJointAuthors.SameStructure(group.Loops[0].Joints,solved.RegionOffsets[0].Loops[0].Joints));
        new SketchRegionEvaluationContext(Kernel).ValidateSolved(authored,solved);SketchAssociativeEndpoints.RequireSolved(solved);Kernel.ConfirmExpressionRegions(solved);
        foreach(var fix in authored.Constraints.OfType<FixPointConstraint>())
        {
            var actual=solved.Points.Single(p=>p.Id==fix.Point).Position;Assert.InRange(double.Hypot(actual.X-fix.Position.X,actual.Y-fix.Position.Y),0,1e-7);
        }
        var(doc,part)=SketchCommandTests.Document();solved=solved with{PartId=part};doc=doc with{Sketches=doc.Sketches.Add(solved.Id,solved)};
        var root=Assert.Single(SketchRegionAuthorModel.Roots(solved,solved.RegionOffsets[0].Id));
        var profile=new SketchProfileReference(solved.Id,solved.Revision,[]){MixedBoundaryIds=root.MixedCurveIds,Regions=root.Children};
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(doc,assets,Kernel,new InlineSessionDispatcher());
        await session.ExecuteAsync(new AddBodyCommand(profile.Resolve(doc,part,new ExtrudeRecipe(new([]),3,solved.Plane)),"Native mixed D solid",part,sketchSource:profile));
        double area=0,perimeter=0;
        foreach(var source in solved.RegionOffsets[0].Sources[0])
        {
            var geometry=SketchExpressionEntities.Author(solved,source.Entity).Geometry;
            area+=Integrate(t=>{var v=geometry.Evaluate(t);return (v.Point.X*v.First.Y-v.Point.Y*v.First.X)/2;});
            perimeter+=Integrate(t=>{var v=geometry.Evaluate(t);return double.Hypot(v.First.X,v.First.Y);});
        }
        area=Math.Abs(area);
        if(kind=="arc-g1")
        {
            double r=2*factor,w=8*factor;Assert.InRange(Math.Abs(area-(2*w*r+Math.PI*r*r/2)),0,1e-9);
            Assert.InRange(Math.Abs(perimeter-(2*w+2*r+Math.PI*r)),0,1e-9);
        }
        double corner=distance<0||join==SketchRegionAuthorJoin.Miter?2:join==SketchRegionAuthorJoin.Round?Math.PI/2:1;
        double expected=3*(area+distance*perimeter+(Math.PI/2+corner)*distance*distance);
        Assert.InRange(Math.Abs(Assert.Single(session.Snapshot.Features.Values).Result.VolumeMm3-expected),0,1e-7);
        var accepted=session.Snapshot;await session.UndoAsync();Assert.Empty(session.Snapshot.Features);await session.RedoAsync();Assert.Same(accepted,session.Snapshot);
        using var files=new TestFiles();var storage=new CadDocumentStorage(regionVerifier:Kernel);string saved=files.PathFor("mixed.cadoryx");
        await session.SaveAsync(storage,saved);int assetCount=assets.Count;
        using(var reopened=await storage.LoadAsync(saved,assets))
        {
            var restored=reopened.Snapshot.Sketches[solved.Id];Assert.True(SketchRegionAuthorModel.SameTable(solved,restored));
            Assert.Equal(solved.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)),restored.ExpressionCurves.Select(c=>(c.Id,c.Endpoints)));
            Assert.Equal(SketchRegionSectionStorage.EncodeJson([solved]),SketchRegionSectionStorage.EncodeJson([restored]));
            Kernel.ConfirmExpressionRegions(restored);Assert.InRange(Math.Abs(reopened.Snapshot.Features.Values.Single().Result.VolumeMm3-expected),0,1e-7);
            string step=files.PathFor("mixed.step");await Kernel.ExportAsync(reopened.Snapshot,assets,step,new());Assert.True(File.Exists(step));Assert.True(new FileInfo(step).Length>0);
        }
        Assert.Equal(assetCount,assets.Count);
        await session.DisposeAsync();Assert.Equal(0,assets.Count);
    }

    [Theory][InlineData("arc-g1",0)][InlineData("arc-g1",1)][InlineData("spline-g1",0)][InlineData("spline-g1",1)][InlineData("spline-g2",0)][InlineData("spline-g2",1)]
    public async Task MissingNaturalProofStillRejectsTheMixedLoopWithoutChangingItsNativeSnapshot(string kind,int index)
    {
        var s=NativeMixedBoundary(kind,false,.1,SketchRegionAuthorJoin.Round);var before=s;
        var c=s.Constraints.Where(c=>c is EndpointTangentConstraint or EndpointCurvatureConstraint).ElementAt(index);
        var authored=s with{Constraints=s.Constraints.Replace(c,c with{IsEnabled=false})};
        var report=await new ManagedSketchConstraintSolver(Kernel).SolveAsync(authored);
        Assert.False(report.Succeeded);Assert.Null(report.Solution);Assert.Equal("SKETCH.SINGULAR_EVALUATION",report.Diagnostic);
        Assert.Equal(before.RegionOffsets,authored.RegionOffsets);Assert.Equal(before.ExpressionCurves,authored.ExpressionCurves);Kernel.ConfirmExpressionRegions(s);
    }

    // Independent source Green integral and speed integral. The native output
    // and solver gradients are not used. Each error budget is below 1e-10.
    private static double Integrate(Func<double,double> f)
    {
        double sum=0;
        for(int part=0;part<16;part++)
        {
            double a=part/16d,b=(part+1)/16d,c=(a+b)/2;
            double fa=f(a),fb=f(b),fc=f(c),whole=(b-a)*(fa+4*fc+fb)/6;
            sum+=Refine(a,b,fa,fb,fc,whole,1e-11/16,20);
        }
        return sum;
        double Refine(double a,double b,double fa,double fb,double fc,double whole,double tolerance,int depth)
        {
            double c=(a+b)/2,l=(a+c)/2,r=(c+b)/2,fl=f(l),fr=f(r);
            double left=(c-a)*(fa+4*fl+fc)/6,right=(b-c)*(fc+4*fr+fb)/6,difference=left+right-whole;
            if(Math.Abs(difference)<=15*tolerance)return left+right+difference/15;
            if(depth==0)throw new InvalidOperationException("Independent source integration did not meet the original accuracy budget.");
            return Refine(a,c,fa,fc,fl,left,tolerance/2,depth-1)+Refine(c,b,fc,fb,fr,right,tolerance/2,depth-1);
        }
    }
}
