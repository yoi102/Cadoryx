using System.Collections.Immutable;
using Cadoryx.Db;
using MathNet.Numerics.LinearAlgebra;

namespace Cadoryx.Sketching;

/// <summary>Bounded local least-squares solver. Rank/DOF are local; nonlinear failure is not proof of inconsistency.</summary>
public sealed class ManagedSketchConstraintSolver : ISketchConstraintSolver
{
    public string Version=>"cadoryx.sketch.lm-svd.1";
    public Task<SketchSolveReport> SolveAsync(CadSketch sketch,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        =>Task.Run(()=>Solve(sketch,options??new(),cancellationToken),cancellationToken);

    private static SketchSolveReport Solve(CadSketch sketch,SketchSolveOptions options,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();options.Validate();
        try{sketch.Validate();}
        catch(ArgumentException ex){return Failure(SketchSolveStatus.InvalidInput,ex.Message);}
        if(sketch.Points.Length*2+sketch.Circles.Length>options.MaxVariables||sketch.Constraints.Where(c=>c.IsEnabled).Sum(c=>c is FixPointConstraint or CoincidentConstraint?2:1)>options.MaxEquations)
            return Failure(SketchSolveStatus.LimitExceeded,"Dense solver budget exceeded.");
        SketchEquationSystem system;
        try{system=new(sketch,options);}
        catch(NotSupportedException ex){return Failure(SketchSolveStatus.LimitExceeded,ex.Message);}
        int n=system.Initial.Length,m=system.EquationCount,iterations=0;var x=(double[])system.Initial.Clone();var residual=system.Residual(x);
        if(m==0)return new(n==0?SketchSolveStatus.FullyConstrained:SketchSolveStatus.UnderConstrained,sketch,n,0,n,0,0,0,[],[],[],"SKETCH.LOCAL_DOF");
        double damping=system.IsAffine?0:1e-4;
        while(Max(residual)>1&&iterations<options.MaxIterations)
        {
            token.ThrowIfCancellationRequested();iterations++;
            var jacobian=system.Jacobian(x,token);
            if(!Finite(jacobian)||!residual.All(double.IsFinite))break;
            var step=Step(jacobian,residual,damping);var next=x.Zip(step,(a,b)=>a+b).ToArray();
            var nextResidual=system.Residual(next);
            if(system.Admissible(next)&&nextResidual.All(double.IsFinite)&&Cost(nextResidual)<Cost(residual))
            {
                x=next;residual=nextResidual;damping=system.IsAffine?0:Math.Max(1e-14,damping*0.2);
            }
            else
            {
                if(system.IsAffine)break;
                damping*=10;if(damping>1e10)break;
            }
        }
        token.ThrowIfCancellationRequested();
        if(!residual.All(double.IsFinite)||Max(residual)>1||!system.Admissible(x))
        {
            var conflicts=system.IsAffine?AffineConflict(system,token):ImmutableArray<SketchConstraintId>.Empty;
            return Report(conflicts.IsEmpty?SketchSolveStatus.DidNotConverge:SketchSolveStatus.Inconsistent,null,null,null,conflicts,[],
                conflicts.IsEmpty?"SKETCH.NO_CONVERGENCE":"SKETCH.AFFINE_CONFLICT");
        }
        var candidate=system.Apply(x);
        try{candidate.Validate();}
        catch(ArgumentException){return Report(SketchSolveStatus.DidNotConverge,null,null,null,[],[],"SKETCH.DEGENERATE_SOLUTION");}
        // Validate the coordinates actually returned to the domain, including denormalization roundoff.
        x=system.Coordinates(candidate);residual=system.Residual(x);
        if(!residual.All(double.IsFinite)||Max(residual)>1)return Report(SketchSolveStatus.DidNotConverge,null,null,null,[],[],"SKETCH.OUTPUT_PRECISION");
        var finalJacobian=system.Jacobian(x,token);
        if(!Finite(finalJacobian))return Report(SketchSolveStatus.DidNotConverge,null,null,null,[],[],"SKETCH.SINGULAR_EVALUATION");
        int rank=Rank(finalJacobian);var redundant=ImmutableArray.CreateBuilder<SketchConstraintId>();
        if(rank<m)
        {
            var rows=new List<int>();int previous=0;
            foreach(var group in system.Groups)
            {
                token.ThrowIfCancellationRequested();rows.AddRange(Enumerable.Range(group.Start,group.Count));
                int next=Rank(Rows(finalJacobian,rows));if(next-previous<group.Count)redundant.Add(group.Constraint.Id);previous=next;
            }
        }
        return Report(rank==n?SketchSolveStatus.FullyConstrained:SketchSolveStatus.UnderConstrained,candidate,n-rank,rank,[],redundant.ToImmutable(),"SKETCH.LOCAL_DOF");

        SketchSolveReport Report(SketchSolveStatus status,CadSketch? solution,int? dof,int? rank,
            ImmutableArray<SketchConstraintId> conflicts,ImmutableArray<SketchConstraintId> redundant,string diagnostic)
            =>new(status,solution,dof,rank,n,m,iterations,Max(residual),conflicts,redundant,
                system.Groups.Select(g=>new SketchConstraintResidual(g.Constraint.Id,Max(residual.Skip(g.Start).Take(g.Count)))).ToImmutableArray(),diagnostic);
    }
    private static SketchSolveReport Failure(SketchSolveStatus status,string message)=>new(status,null,null,null,0,0,0,double.PositiveInfinity,[],[],[],message);
    private static double Max(IEnumerable<double> residual)=>residual.Select(Math.Abs).DefaultIfEmpty(0).Max();
    private static double Cost(double[] residual)=>residual.Sum(v=>v*v);
    private static bool Finite(Matrix<double> matrix)=>matrix.Enumerate().All(double.IsFinite);

    // Singular modes below the relative threshold are left unchanged; this preserves unconstrained seed motion.
    private static double[] Step(Matrix<double> matrix,double[] residual,double damping)
    {
        if(matrix.RowCount==0)return new double[matrix.ColumnCount];
        var svd=matrix.Svd(true);double largest=svd.S.Count==0?0:svd.S[0];var step=new double[matrix.ColumnCount];
        for(int i=0;i<svd.S.Count;i++)
        {
            double s=svd.S[i];if(s<=largest*1e-12||s==0)continue;
            double projected=0;for(int row=0;row<residual.Length;row++)projected+=svd.U[row,i]*residual[row];
            double coefficient=-projected*s/(s*s+damping*largest*largest);
            for(int col=0;col<step.Length;col++)step[col]+=svd.VT[i,col]*coefficient;
        }
        return step;
    }
    private static int Rank(Matrix<double> matrix)
    {
        if(matrix.RowCount==0||matrix.ColumnCount==0)return 0;
        var normalized=matrix.Clone();
        for(int i=0;i<normalized.RowCount;i++)
        {double norm=normalized.Row(i).L2Norm();if(norm>0)for(int j=0;j<normalized.ColumnCount;j++)normalized[i,j]/=norm;}
        var s=normalized.Svd(false).S;double threshold=Math.Max(1e-10,s[0]*1e-8);return s.Count(v=>v>threshold);
    }
    private static Matrix<double> Rows(Matrix<double> matrix,IReadOnlyList<int> rows)
        =>Matrix<double>.Build.Dense(rows.Count,matrix.ColumnCount,(i,j)=>matrix[rows[i],j]);
    private static ImmutableArray<SketchConstraintId> AffineConflict(SketchEquationSystem system,CancellationToken token)
    {
        var groups=system.Groups.ToList();var jacobian=system.Jacobian((double[])system.Initial.Clone(),token);
        bool Inconsistent(IReadOnlyList<SketchEquationSystem.EquationGroup> trial)
        {
            if(trial.Count==0)return false;
            var rows=trial.SelectMany(g=>Enumerable.Range(g.Start,g.Count)).ToArray();var x=(double[])system.Initial.Clone();
            for(int refine=0;refine<3;refine++)
            {
                token.ThrowIfCancellationRequested();var residual=system.Residual(x);var step=Step(Rows(jacobian,rows),rows.Select(i=>residual[i]).ToArray(),0);
                for(int j=0;j<x.Length;j++)x[j]+=step[j];
            }
            var remaining=system.Residual(x);return Max(rows.Select(i=>remaining[i]))>1;
        }
        // An iteration budget or rejected candidate must not by itself prove inconsistency.
        if(!Inconsistent(groups))return [];
        // Bound diagnostic work. Above 64 constraints the whole inconsistent affine set is reported.
        if(groups.Count<=64)
        {
            foreach(var candidate in system.Groups)
            {
                token.ThrowIfCancellationRequested();var trial=groups.Where(g=>g!=candidate).ToList();
                if(Inconsistent(trial))groups=trial;
            }
        }
        return groups.Select(g=>g.Constraint.Id).ToImmutableArray();
    }
}
