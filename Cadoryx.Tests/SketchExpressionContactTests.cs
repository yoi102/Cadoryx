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

public sealed class SketchExpressionContactTests
{
    internal static CadSketch Pair(int kind,bool opposite,FiniteContactContinuity continuity,FiniteContactParameterMode mode,bool pinAll=true)
    {
        var(source,a)=SketchExpressionDifferentialTests.Source(kind,opposite,true);
        var g=SketchFiniteParametricContact.EvaluateGeometry(source,a,.37,2);
        double sign=opposite?-1:1,k=g.CurvaturePerMm*sign;
        var tangent=new Point2d(g.Tangent.X*sign,g.Tangent.Y*sign);
        var draft=new SketchDraft(source);SketchEntityId b;
        if(kind==0)
        {draft.AddLine(new(g.Position.X-4*tangent.X,g.Position.Y-4*tangent.Y),new(g.Position.X+4*tangent.X,g.Position.Y+4*tangent.Y));b=draft.Value.Lines[^1].Id;}
        else
        {
            var second=new Point2d(-tangent.Y*k*64,tangent.X*k*64);
            var mid=new Point2d(g.Position.X+second.X/8,g.Position.Y+second.Y/8);
            draft.AddBezier(new(mid.X-4*tangent.X,mid.Y-4*tangent.Y),new(g.Position.X-second.X/8,g.Position.Y-second.Y/8),
                new(mid.X+4*tangent.X,mid.Y+4*tangent.Y));b=draft.Value.Beziers[^1].Id;
        }
        var s=draft.Value;
        var c=new FiniteParametricContactConstraint(SketchConstraintId.New(),a,b,.37,.5,
            opposite?FiniteContactTangentBranch.OppositeDirection:FiniteContactTangentBranch.SameDirection){Continuity=continuity,ParameterMode=mode};
        if(c.SolvesA)c=c with{ParameterA=.34,SlidingA=new(.34,.2,.6)};
        if(c.SolvesB)c=c with{ParameterB=.46,SlidingB=new(.46,.3,.7)};
        var constraints=(pinAll?s.Points:source.Points).Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)).ToImmutableArray();
        constraints=constraints.AddRange(s.Circles.Select(v=>(SketchConstraint)new RadiusConstraint(SketchConstraintId.New(),v.Id,v.Radius)));
        return s with{Constraints=constraints.Insert(Math.Min(1,constraints.Length),c)};
    }
    public static IEnumerable<object[]> Cases()
    {
        foreach(int kind in Enumerable.Range(0,10))foreach(bool opposite in new[]{false,true})
        foreach(var continuity in Enum.GetValues<FiniteContactContinuity>())foreach(var mode in Enum.GetValues<FiniteContactParameterMode>())
            yield return[kind,opposite,continuity,mode];
    }
    [Theory][MemberData(nameof(Cases))]
    public async Task EveryAuthorFamilySolvesDirectedFixedAndAutomaticG1G2WithoutReplacingAuthors(int kind,bool opposite,FiniteContactContinuity continuity,FiniteContactParameterMode mode)
    {
        var authored=Pair(kind,opposite,continuity,mode);authored.Validate();
        var report=await new ManagedSketchConstraintSolver().SolveAsync(authored);
        Assert.True(report.Succeeded,$"{kind}/{opposite}/{continuity}/{mode}: {report.Diagnostic}; iterations={report.Iterations}; residual={report.MaxNormalizedResidual:R}");
        var solved=report.Solution!;SketchContactSolutionVerification.Validate(authored,solved);SketchExpressionEntities.ValidateSolved(authored,solved);
        var contact=solved.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        Assert.Equal(authored.Points.Length*2+authored.Circles.Length+contact.ParameterVariableCount,report.VariableCount);
        Assert.Equal(authored.Constraints.Select(c=>c.Id),solved.Constraints.Select(c=>c.Id));
        foreach(var p in authored.Points)Near(p.Position,solved.Points.Single(q=>q.Id==p.Id).Position);
        CheckNative(solved,contact);
    }
    private static void Near(Point2d a,Point2d b)=>Assert.InRange(double.Hypot(a.X-b.X,a.Y-b.Y),0,1e-7);
    internal static void CheckNative(CadSketch solved,FiniteParametricContactConstraint contact)
    {
        var expression=solved.ExpressionCurves.Single(c=>c.Id==contact.A);var native=new OcctGeometryKernel().EvaluateExpression(expression.Geometry,contact.ParameterA);
        var b=SketchFiniteParametricContact.EvaluateGeometry(solved,contact.B,contact.ParameterB,2);double speed=double.Hypot(native.First.X,native.First.Y);
        Near(native.Point,b.Position);double dot=(native.First.X*b.Tangent.X+native.First.Y*b.Tangent.Y)/speed;
        Assert.InRange(Math.Abs((native.First.X*b.Tangent.Y-native.First.Y*b.Tangent.X)/speed),0,1e-9);
        Assert.True(contact.Branch==FiniteContactTangentBranch.SameDirection?dot>0:dot<0);
        if(contact.Continuity==FiniteContactContinuity.Curvature)
        {
            double k=(native.First.X*native.Second.Y-native.First.Y*native.Second.X)/(speed*speed*speed);
            Assert.InRange(Math.Abs(k-(contact.Branch==FiniteContactTangentBranch.SameDirection?1:-1)*b.CurvaturePerMm),0,1e-7);
        }
    }
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)][InlineData(8)][InlineData(9)]
    public void FullEquationJacobianIncludesSourceCoordinatesRadiusAndBothBoundedParameters(int kind)
    {
        var sketch=Pair(kind,true,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveBoth,false);
        const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var type=typeof(ManagedSketchConstraintSolver).Assembly.GetType("Cadoryx.Sketching.SketchEquationSystem")!;
        var system=Activator.CreateInstance(type,flags,null,[sketch,new SketchSolveOptions(LinearToleranceMm:1e-3,AngularTolerance:1e-3,CurvatureTolerancePerMm:1e-3)],null)!;
        var x=(double[])type.GetProperty("Initial",flags)!.GetValue(system)!;
        var jac=(Matrix<double>)type.GetMethod("Jacobian",flags)!.Invoke(system,[x,CancellationToken.None])!;var residual=type.GetMethod("Residual",flags)!;
        for(int col=0;col<x.Length;col++)
        {
            const double h=1e-6;var plus=(double[])x.Clone();var minus=(double[])x.Clone();plus[col]+=h;minus[col]-=h;
            var p=(double[])residual.Invoke(system,[plus])!;var m=(double[])residual.Invoke(system,[minus])!;
            for(int row=0;row<jac.RowCount;row++)Assert.InRange(Math.Abs((p[row]-m[row])/(2*h)-jac[row,col]),0,Math.Max(3e-5,Math.Abs(jac[row,col])*3e-6));
        }
    }
    [Theory][InlineData(1,false)][InlineData(1,true)][InlineData(3,false)][InlineData(3,true)][InlineData(4,false)][InlineData(4,true)]
    public async Task ContactEquationsMoveLiveSourceCoordinatesAndRadiusInsteadOfFreezingTheirCopies(int kind,bool opposite)
    {
        var truth=Pair(kind,opposite,FiniteContactContinuity.Curvature,FiniteContactParameterMode.Fixed);
        SketchEntityId movable=kind switch{1=>truth.Circles[0].Center,3=>truth.Beziers[0].Control,_=>truth.Splines[0].Controls[1]};
        var authored=truth with{Points=truth.Points.Select(p=>p.Id==movable?p with{Position=new(p.Position.X+.08,p.Position.Y-.06)}:p).ToImmutableArray(),
            Circles=truth.Circles.Select(c=>c with{Radius=c.Radius+.2}).ToImmutableArray(),
            Constraints=truth.Constraints.Where(c=>c is not RadiusConstraint&&c is not FixPointConstraint f||c is FixPointConstraint fp&&fp.Point!=movable).ToImmutableArray()};
        authored=SketchExpressionEntities.Refresh(authored);var before=authored.ExpressionCurves.Single().Geometry;
        var report=await new ManagedSketchConstraintSolver().SolveAsync(authored);Assert.True(report.Succeeded,$"{kind}/{opposite}: {report.Diagnostic}; {report.MaxNormalizedResidual:R}");
        var solved=report.Solution!;Near(truth.Points.Single(p=>p.Id==movable).Position,solved.Points.Single(p=>p.Id==movable).Position);
        if(kind==1)Assert.InRange(Math.Abs(truth.Circles[0].Radius-solved.Circles[0].Radius),0,1e-7);
        Assert.False(before.SameValues(solved.ExpressionCurves.Single().Geometry));SketchExpressionEntities.ValidateSolved(authored,solved);
        CheckNative(solved,solved.Constraints.OfType<FiniteParametricContactConstraint>().Single());
    }
    [Fact] public async Task AutomaticParametersKeepTheirOriginalBudgetBoundsAndCancellation()
    {
        var source=Pair(5,false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.SolveBoth);int basic=source.Points.Length*2+source.Circles.Length;
        var limited=await new ManagedSketchConstraintSolver().SolveAsync(source,new(MaxVariables:basic+1));Assert.Equal(SketchSolveStatus.LimitExceeded,limited.Status);Assert.Null(limited.Solution);
        using var cancel=new CancellationTokenSource();cancel.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new ManagedSketchConstraintSolver().SolveAsync(source,cancellationToken:cancel.Token));
        var c=source.Constraints.OfType<FiniteParametricContactConstraint>().Single();
        source=source with{Constraints=source.Constraints.Replace(c,c with{ParameterA=.22,SlidingA=new(.22,.2,.24)})};
        var report=await new ManagedSketchConstraintSolver().SolveAsync(source);Assert.False(report.Succeeded);Assert.Null(report.Solution);
        Assert.Equal(.22,source.Constraints.OfType<FiniteParametricContactConstraint>().Single().ParameterA);
    }
    [Fact] public void NativeLineFactoryRoundingKeepsFullAndReversedTrimmedAuthorEndpoints()
    {
        var kernel=new OcctGeometryKernel();
        for(int i=0;i<32;i++)
        {
            Point2d start=new(2,-3),end=new(10+1e-12*i,1-2e-12*i);
            var basis=SketchCurveExpressionGeometry.FromBoundary(new(start,end));
            foreach(var expression in new[]{basis.Offset(.04),basis.Offset(.04).Reverse().Offset(-.01).Trim(.1,.9)})
            {
                Assert.Same(expression,kernel.PrepareExpression(expression));
                foreach(double t in new[]{0d,.37,1d})
                {var native=kernel.EvaluateExpression(expression,t);var author=expression.Evaluate(t);Near(author.Point,native.Point);Near(author.First,native.First);}
            }
        }
    }
    private static CadSketch KnotContact(FiniteContactContinuity continuity,FiniteContactParameterMode mode)
    {
        var draft=new SketchDraft(CadSketch.Create(DefinitionId.New(),"Exact knot order",RigidTransform3d.Identity));
        draft.AddFreeform(SketchFreeformGeometryTests.Geometry(3));var a=draft.AddExactOffset(draft.Value.Freeforms[0].Id,.02);
        var g=SketchFiniteParametricContact.EvaluateGeometry(draft.Value,a,.5);var p=g.Position;var v=g.Tangent;
        draft.AddLine(new(p.X-v.X,p.Y-v.Y),new(p.X+v.X,p.Y+v.Y));var b=draft.Value.Lines[0].Id;
        var c=new FiniteParametricContactConstraint(SketchConstraintId.New(),a,b,.5,.5,FiniteContactTangentBranch.SameDirection){Continuity=continuity,ParameterMode=mode,
            SlidingA=mode==FiniteContactParameterMode.SolveA?new(.5,.4,.6):null};
        return draft.Value with{Constraints=draft.Value.Points.Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)).Append(c).ToImmutableArray()};
    }
    [Fact] public async Task FixedG1AtAC1OffsetKnotDoesNotDemandAnUnusedCurvatureDerivative()
    {
        var source=KnotContact(FiniteContactContinuity.Tangent,FiniteContactParameterMode.Fixed);source.Validate();
        var result=await new ManagedSketchConstraintSolver().SolveAsync(source);Assert.True(result.Succeeded,result.Diagnostic);
        SketchFiniteParametricContact.RequireSolvedRelations(result.Solution!);
    }
    [Theory][InlineData(FiniteContactContinuity.Curvature,FiniteContactParameterMode.Fixed)]
    [InlineData(FiniteContactContinuity.Tangent,FiniteContactParameterMode.SolveA)]
    public void HigherOrderContactAtTheSameKnotRequiresProvedContinuity(FiniteContactContinuity continuity,FiniteContactParameterMode mode)
    {
        var source=KnotContact(continuity,mode);var error=Assert.Throws<CadValidationException>(source.Validate);
        Assert.Equal("SlidingContactDerivativeAmbiguous",error.Data["SketchContact.Message"]);
    }
    [Fact] public async Task ALocallySolvedContactCannotCommitAnOffsetThatFoldsElsewhere()
    {
        var(initial,part)=SketchCommandTests.Document();var draft=new SketchDraft(CadSketch.Create(part,"Full segment proof",RigidTransform3d.Identity));
        draft.AddEllipse(new(new(0,0),new(5,0),new(0,1),0,Math.PI));var a=draft.AddExactOffset(draft.Value.Ellipses[0].Id,-.5);
        var g=SketchFiniteParametricContact.EvaluateGeometry(draft.Value,a,.5);var p=g.Position;var t=g.Tangent;
        draft.AddLine(new(p.X-t.X,p.Y-t.Y),new(p.X+t.X,p.Y+t.Y));
        var source=draft.Value with{Constraints=draft.Value.Points.Select(p=>(SketchConstraint)new FixPointConstraint(SketchConstraintId.New(),p.Id,p.Position)).Append(
            new FiniteParametricContactConstraint(SketchConstraintId.New(),a,draft.Value.Lines[^1].Id,.5,.5,FiniteContactTangentBranch.SameDirection)).ToImmutableArray()};
        var local=await new ManagedSketchConstraintSolver().SolveAsync(source);Assert.True(local.Succeeded,local.Diagnostic);
        var kernel=new OcctGeometryKernel();Assert.False(kernel.InspectExpressionRegularity(source.ExpressionCurves.Single().Geometry).IsCertified);
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(initial,assets,kernel,new InlineSessionDispatcher());long generation=session.Generation;
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(source,new ManagedSketchConstraintSolver())));
        Assert.Same(initial,session.Snapshot);Assert.Equal(generation,session.Generation);Assert.Equal(0,assets.Count);
    }
    [Theory][InlineData("radius")][InlineData("expression")][InlineData("parameter")][InlineData("point")]
    public async Task InjectableSolverCannotForgeLiveSourceOrContactOutput(string change)
    {
        var(initial,part)=SketchCommandTests.Document();var source=Pair(1,false,FiniteContactContinuity.Curvature,FiniteContactParameterMode.Fixed) with{PartId=part};
        var assets=new MemoryAssetStore();await using var session=new CadDocumentSession(initial,assets,new OcctGeometryKernel(),new InlineSessionDispatcher());
        await Assert.ThrowsAsync<CadValidationException>(()=>session.ExecuteAsync(new UpsertSketchCommand(source,new CorruptSolver(change))));Assert.Same(initial,session.Snapshot);Assert.Equal(0,assets.Count);
    }
    private sealed class CorruptSolver(string change):ISketchConstraintSolver
    {
        public string Version=>"unverified-expression-contact";
        public async Task<SketchSolveReport> SolveAsync(CadSketch source,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        {
            var report=await new ManagedSketchConstraintSolver().SolveAsync(source,options,cancellationToken);var s=report.Solution!;
            var expression=s.ExpressionCurves.Single();var contact=s.Constraints.OfType<FiniteParametricContactConstraint>().Single();
            s=change switch
            {
                "radius"=>SketchExpressionEntities.Refresh(s with{Circles=[s.Circles[0] with{Radius=s.Circles[0].Radius+.1}]}),
                "expression"=>s with{ExpressionCurves=[expression with{Geometry=expression.Geometry with{SignedOffset=expression.Geometry.SignedOffset+.1}}]},
                "parameter"=>s with{Constraints=s.Constraints.Replace(contact,contact with{ParameterA=contact.ParameterA+.01})},
                _=>s with{Points=s.Points.Select(p=>p.Id==s.Beziers[^1].Control?p with{Position=new(p.Position.X+.1,p.Position.Y)}:p).ToImmutableArray()}
            };
            return report with{Solution=s};
        }
    }
}
