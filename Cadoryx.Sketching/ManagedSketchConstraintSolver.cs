using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using MathNet.Numerics.LinearAlgebra;

namespace Cadoryx.Sketching;

/// <summary>Bounded local least-squares solver. Rank/DOF are local; nonlinear failure is not proof of inconsistency.</summary>
public sealed partial class ManagedSketchConstraintSolver : ISketchConstraintSolver
{
    private readonly IExactSketchExpressionRegions? regions;
    private readonly ISketchExpressionRegionVerifier? regionVerifier;
    public ManagedSketchConstraintSolver():this(null){}
    public ManagedSketchConstraintSolver(IExactSketchExpressionRegions? regions,ISketchExpressionRegionVerifier? regionVerifier=null)
    {this.regions=regions;this.regionVerifier=regionVerifier;}
    public string Version=>"cadoryx.sketch.lm-svd.21";
    public Task<SketchSolveReport> SolveAsync(CadSketch sketch,SketchSolveOptions? options=null,CancellationToken cancellationToken=default)
        =>Task.Run(()=>Solve(sketch,options??new(),cancellationToken),cancellationToken);

    private SketchSolveReport Solve(CadSketch sketch,SketchSolveOptions options,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();options.Validate();
        try{sketch.Validate();}
        catch(ArgumentException ex){return Failure(SketchSolveStatus.InvalidInput,ex.Message);}
        // With no enabled equations, the authored coordinates are already the exact solution.
        // Do not allocate a dense system just to retain a large unconstrained author curve.
        if(!sketch.Constraints.Any(c=>c.IsEnabled)&&!sketch.ExpressionCurves.Any(c=>c.Endpoints is not null))
        {
            int variables=sketch.Points.Length*2+sketch.Circles.Length;token.ThrowIfCancellationRequested();
            return new(variables==0?SketchSolveStatus.FullyConstrained:SketchSolveStatus.UnderConstrained,sketch,
                variables,0,variables,0,0,0,[],[],[],"SKETCH.LOCAL_DOF");
        }
        if(CountVariables(sketch)>options.MaxVariables||CountEquations(sketch)>options.MaxEquations)
            return Failure(SketchSolveStatus.LimitExceeded,"Dense solver budget exceeded.");
        SketchRegionEvaluationContext? regionContext=null;
        if(!sketch.RegionOffsets.IsEmpty)
        {
            if(regions is null)return Failure(SketchSolveStatus.InvalidInput,"SKETCH.REGION_BACKEND_REQUIRED");
            try{regionContext=new(regions,regionVerifier);regionContext.Refresh(sketch,token);}
            catch(CadValidationException ex){return Failure(SketchSolveStatus.InvalidInput,ex.Message);}
            catch(NotSupportedException ex){return Failure(SketchSolveStatus.InvalidInput,ex.Message);}
        }
        SketchEquationSystem system;
        try{system=new(sketch,options,regionContext,token);}
        catch(NotSupportedException ex){return Failure(SketchSolveStatus.LimitExceeded,ex.Message);}
        int n=system.Initial.Length,m=system.EquationCount,iterations=0;var x=(double[])system.Initial.Clone();system.PrescribeCoordinates(x);var residual=Evaluate(x);
        if(!residual.All(double.IsFinite))return Report(SketchSolveStatus.DidNotConverge,null,null,null,[],[],"SKETCH.SINGULAR_EVALUATION");
        if(!system.Admissible(x))return Report(SketchSolveStatus.DidNotConverge,null,null,null,[],[],"SKETCH.TANGENCY_BRANCH");
        if(m==0)return new(n==0?SketchSolveStatus.FullyConstrained:SketchSolveStatus.UnderConstrained,sketch,n,0,n,0,0,0,[],[],[],"SKETCH.LOCAL_DOF");
        double damping=system.IsAffine?0:1e-4;
        while(Max(residual)>1&&iterations<options.MaxIterations)
        {
            token.ThrowIfCancellationRequested();iterations++;
            var jacobian=system.IterationJacobian(x,token);
            if(!Finite(jacobian)||!residual.All(double.IsFinite))break;
            var step=Step(jacobian,residual,damping,system.RequiresRowEquilibration);
            double[] next=x,nextResidual=residual;bool accepted=false;
            double currentCost=LinearizedCost(residual,jacobian,system.RequiresRowEquilibration);
            // Use the same fixed row metric as this iteration's equilibrated step.
            // Backtracking changes only the local coordinate step, never the allowed
            // interval, branch, iteration limit or final physical tolerances.
            for(int trial=0;trial<(system.IsAffine?1:12);trial++)
            {
                token.ThrowIfCancellationRequested();double fraction=Math.ScaleB(1,-trial);
                next=x.Zip(step,(a,b)=>a+b*fraction).ToArray();nextResidual=Evaluate(next);
                if(system.Admissible(next)&&nextResidual.All(double.IsFinite)&&
                   LinearizedCost(nextResidual,jacobian,system.RequiresRowEquilibration)<currentCost){accepted=true;break;}
            }
            if(accepted)
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
        CadSketch candidate;
        try
        {
            var values=system.RestorePrescribedValues(system.Apply(x));
            candidate=regionContext is null?SketchExpressionEntities.Refresh(values):regionContext.Refresh(values,token);
            if(regionContext is not null)regionContext.ValidateSolved(sketch,candidate,token);
        }
        catch(CadValidationException){return Report(SketchSolveStatus.DidNotConverge,null,null,null,[],[],"SKETCH.EXPRESSION_SOURCE_OUTPUT");}
        try{candidate.Validate();}
        catch(ArgumentException){return Report(SketchSolveStatus.DidNotConverge,null,null,null,[],[],"SKETCH.DEGENERATE_SOLUTION");}
        // Validate the coordinates actually returned to the domain, including denormalization roundoff.
        x=system.Coordinates(candidate);residual=Evaluate(x);
        if(!residual.All(double.IsFinite)||Max(residual)>1||!system.Admissible(x))return Report(SketchSolveStatus.DidNotConverge,null,null,null,[],[],"SKETCH.OUTPUT_PRECISION");
        foreach(var contact in candidate.Constraints.OfType<CircularContactConstraint>().Where(c=>c.IsEnabled))
        {
            token.ThrowIfCancellationRequested();
            if(!SketchCircularContact.TryContactPoint(candidate,contact,options.LinearToleranceMm,out _,out var diagnostic))
                return Report(SketchSolveStatus.DidNotConverge,null,null,null,[],[],diagnostic);
        }
        foreach(var relation in candidate.Constraints.OfType<EndpointCurvatureConstraint>().Where(c=>c.IsEnabled))
        {
            token.ThrowIfCancellationRequested();
            var a=SketchEndpointCurvature.Evaluate(candidate,relation.A,relation.Joint);
            var b=SketchEndpointCurvature.Evaluate(candidate,relation.B,relation.Joint);
            if(Math.Abs(a.Tangent.X*b.Tangent.Y-a.Tangent.Y*b.Tangent.X)>options.AngularTolerance||
                a.Tangent.X*b.Tangent.X+a.Tangent.Y*b.Tangent.Y>=0||Math.Abs(a.CurvaturePerMm+b.CurvaturePerMm)>options.CurvatureTolerancePerMm)
                return Report(SketchSolveStatus.DidNotConverge,null,null,null,[],[],"SKETCH.CURVATURE_OUTPUT");
        }
        var finalJacobian=system.Jacobian(x,token);
        try
        {
            SketchFiniteParametricContact.RequireSolvedRelations(candidate,options.LinearToleranceMm,options.AngularTolerance,options.CurvatureTolerancePerMm);
            SketchAssociativeEndpoints.RequireSolved(candidate,options.LinearToleranceMm);
        }
        catch(CadValidationException){return Report(SketchSolveStatus.DidNotConverge,null,null,null,[],[],"SKETCH.FINITE_CONTACT_OUTPUT");}
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
        double[] Evaluate(double[] coordinates)
        {
            try{return system.Residual(coordinates);}
            catch(CadValidationException){return Enumerable.Repeat(double.PositiveInfinity,m).ToArray();}
        }
    }
    private static SketchSolveReport Failure(SketchSolveStatus status,string message)=>new(status,null,null,null,0,0,0,double.PositiveInfinity,[],[],[],message);
    private static double Max(IEnumerable<double> residual)=>residual.Select(Math.Abs).DefaultIfEmpty(0).Max();
    private static double Cost(double[] residual)=>residual.Sum(v=>v*v);
    private static double LinearizedCost(double[] residual,Matrix<double> jacobian,bool equilibrate)
    {
        if(!equilibrate)return Cost(residual);
        double cost=0;
        for(int row=0;row<residual.Length;row++)
        {
            double norm=jacobian.Row(row).L2Norm(),value=norm>0?residual[row]/norm:residual[row];cost+=value*value;
        }
        return cost;
    }
    private static bool Finite(Matrix<double> matrix)=>matrix.Enumerate().All(double.IsFinite);

    // Singular modes below the relative threshold are left unchanged; this preserves unconstrained seed motion.
    private static double[] Step(Matrix<double> matrix,double[] residual,double damping,bool equilibrateRows=false)
    {
        if(matrix.RowCount==0)return new double[matrix.ColumnCount];
        if(equilibrateRows)
        {
            // Curvature has inverse-length units. Equilibrate the linearized rows
            // so damping cannot suppress fixed-point equations on small curves.
            // Acceptance and final verification still use the original tolerances.
            matrix=matrix.Clone();residual=(double[])residual.Clone();
            for(int row=0;row<matrix.RowCount;row++)
            {
                double norm=matrix.Row(row).L2Norm();if(norm==0)continue;
                for(int column=0;column<matrix.ColumnCount;column++)matrix[row,column]/=norm;
                residual[row]/=norm;
            }
        }
        var svd=matrix.Svd(true);double largest=svd.S.Count==0?0:svd.S[0];var step=new double[matrix.ColumnCount];
        for(int i=0;i<svd.S.Count;i++)
        {
            double s=svd.S[i];if(s<=largest*1e-12||s==0)continue;
            double projected=0;for(int row=0;row<residual.Length;row++)projected+=svd.U[row,i]*residual[row];
            double coefficient=-projected*s/(s*s+damping*largest*largest);
            for(int col=0;col<step.Length;col++)step[col]+=svd.VT[i,col]*coefficient;
        }
        PreserveIndependentCoordinates(matrix,residual,step);
        return step;
    }
    private static void PreserveIndependentCoordinates(Matrix<double> matrix,double[] residual,double[] step)
    {
        // A disconnected Jacobian block with an exactly zero right-hand side has
        // an exactly zero minimum-norm step. SVD roundoff can otherwise move its
        // already satisfied dimensions (and even columns with no equations).
        // Follow nonzero coefficients transitively: a currently satisfied row
        // connected to a changing dimension must still transmit that change.
        var rows=new bool[matrix.RowCount];var columns=new bool[matrix.ColumnCount];var pending=new Queue<int>();
        for(int row=0;row<residual.Length;row++)if(residual[row]!=0){rows[row]=true;pending.Enqueue(row);}
        while(pending.TryDequeue(out int row))
            for(int col=0;col<columns.Length;col++)
            {
                if(columns[col]||matrix[row,col]==0)continue;
                columns[col]=true;
                for(int other=0;other<rows.Length;other++)
                    if(!rows[other]&&matrix[other,col]!=0){rows[other]=true;pending.Enqueue(other);}
            }
        for(int col=0;col<step.Length;col++)if(!columns[col])step[col]=0;
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
