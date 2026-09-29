using System.Collections.Immutable;
using System.Diagnostics;
using Cadoryx.Db;

namespace Cadoryx.Commands;

/// <summary>Bounded simultaneous rigid-pose solve on an isolated document candidate.</summary>
internal static class AssemblyCycleSolver
{
    public static AssemblySolvePlan Plan(DocumentSnapshot document,CancellationToken cancellationToken=default)
    {
        var relations=document.AssemblyConstraints.Values.Where(c=>c.IsEnabled).OrderBy(c=>c.Id.Value).ToArray();
        var invalid=relations.Where(c=>c.Evaluate(document).Status is AssemblyConstraintStatus.MissingInstance or
            AssemblyConstraintStatus.DefinitionChanged or AssemblyConstraintStatus.TopologyStale or
            AssemblyConstraintStatus.TopologyAnchorUnsupported).Select(c=>c.Id).ToImmutableArray();
        if(!invalid.IsEmpty)return Fail(invalid,AssemblySolveStatus.StaleOrUnsupported,"Reselect stale assembly paths and topology anchors.");
        var paths=relations.SelectMany(c=>c.SecondaryPath is {} b?new[]{c.PrimaryPath,b}:new[]{c.PrimaryPath})
            .Distinct().OrderBy(p=>p.ToString()).ToArray();
        if(paths.Length>32||relations.Length>128)
            return Fail(relations.Select(c=>c.Id).ToImmutableArray(),AssemblySolveStatus.Conflict,
                "The 32-instance / 128-relation solver limit was exceeded.");
        var fixedPaths=relations.Where(c=>c.Kind==AssemblyConstraintKind.Fixed).Select(c=>c.PrimaryPath).ToHashSet();
        var groups=Groups(paths,relations);
        var roots=groups.Where(g=>!g.Any(fixedPaths.Contains))
            .Select(g=>g.FirstOrDefault(p=>!relations.Any(c=>c.SecondaryPath?.Equals(p)==true))??g[0])
            .ToImmutableArray();
        var pinned=fixedPaths.Concat(roots).ToHashSet();
        // Moving an ancestor would also move a supposedly fixed or gauge-pinned descendant.
        foreach(var path in pinned.ToArray())
            for(int length=1;length<path.Slots.Length;length++)
                pinned.Add(new OccurrencePath(path.DocumentId,path.Slots.Take(length)));
        var movable=paths.Where(p=>!pinned.Contains(p)).OrderBy(p=>p.Slots.Length).ThenBy(p=>p.ToString()).ToArray();
        foreach(var path in movable)
            if(!OccurrencePlacement.Resolve(document,path).CanMoveIndependently)
                return Fail(relations.Select(c=>c.Id).ToImmutableArray(),AssemblySolveStatus.Conflict,
                    "Make a shared parent assembly independent before solving.");
        var initial=document.EnumerateOccurrences().ToDictionary(o=>o.Path,o=>o.WorldTransform);
        DocumentSnapshot Candidate(double[] x)
        {
            var result=document;
            for(int i=0;i<movable.Length;i++)
            {
                var path=movable[i];var old=initial[path];int k=i*6;
                var rv=new Vector3d(x[k+3],x[k+4],x[k+5]);
                var rotation=rv.Length>1e-14?Quaterniond.FromAxisAngle(rv/rv.Length,rv.Length)*old.Rotation:old.Rotation;
                var target=new RigidTransform3d(old.Translation+new Vector3d(x[k],x[k+1],x[k+2]),rotation);
                var parent=new OccurrencePath(path.DocumentId,path.Slots.RemoveAt(path.Slots.Length-1));
                var parentWorld=parent.Slots.IsEmpty?RigidTransform3d.Identity:
                    result.EnumerateOccurrences().Single(o=>o.Path.Equals(parent)).WorldTransform;
                var placement=OccurrencePlacement.Resolve(result,path);
                var owner=(AssemblyDefinition)result.Definitions[placement.OwnerId];
                var index=owner.Children.FindIndex(s=>s.Id==placement.Slot.Id);
                result=result with{Definitions=result.Definitions.SetItem(owner.Id,owner with
                    {Children=owner.Children.SetItem(index,placement.Slot with{LocalTransform=parentWorld.Inverse()*target})})};
            }
            return result;
        }
        double[] Residual(DocumentSnapshot snapshot)
        {
            var occurrences=snapshot.EnumerateOccurrences().ToDictionary(o=>o.Path);
            var values=new List<double>();
            static void Add(List<double> v,Vector3d p){v.Add(p.X);v.Add(p.Y);v.Add(p.Z);}
            foreach(var c in relations)
            {
                if(c.Kind==AssemblyConstraintKind.Fixed)continue;
                var a=occurrences[c.PrimaryPath].WorldTransform;
                var b=occurrences[c.SecondaryPath!].WorldTransform;
                var delta=b.Apply(c.SecondaryLocalPoint)-a.Apply(c.PrimaryLocalPoint);
                if(c.Kind==AssemblyConstraintKind.Coincident){Add(values,delta);continue;}
                if(c.Kind==AssemblyConstraintKind.Distance){values.Add(delta.Length-c.TargetDistanceMm);continue;}
                var u=a.Rotation.Rotate(c.PrimaryLocalAxis);var v=b.Rotation.Rotate(c.SecondaryLocalAxis);
                if(c.Kind==AssemblyConstraintKind.AngleAxes)
                {
                    if(c.TargetAngleRad<1e-8)Add(values,u-v);
                    else if(c.TargetAngleRad>Math.PI-1e-8)Add(values,u+v);
                    else values.Add(Math.Acos(Math.Clamp(u.Dot(v),-1,1))-c.TargetAngleRad);
                    continue;
                }
                if(c.Kind==AssemblyConstraintKind.PlanarMate)
                {Add(values,u+v);values.Add(delta.Dot(u));continue;}
                Add(values,u.Cross(v));
                if(c.Kind==AssemblyConstraintKind.Coaxial)Add(values,delta-u*delta.Dot(u));
            }
            return values.ToArray();
        }
        var x=new double[movable.Length*6];var candidate=Candidate(x);var residual=Residual(candidate);
        var budget=Stopwatch.StartNew();
        double cost=Cost(residual),damping=0.001;
        for(int iteration=0;iteration<100&&cost>1e-16&&x.Length>0;iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(budget.Elapsed>TimeSpan.FromSeconds(5))
                return Fail(relations.Select(c=>c.Id).ToImmutableArray(),AssemblySolveStatus.Conflict,
                    "Assembly solver time budget was reached; no pose was committed.");
            var j=Jacobian(x,residual,Candidate,Residual,cancellationToken);var normal=new double[x.Length,x.Length];
            var gradient=new double[x.Length];
            for(int row=0;row<residual.Length;row++)for(int col=0;col<x.Length;col++)
            {
                gradient[col]+=j[row,col]*residual[row];
                for(int other=0;other<x.Length;other++)normal[col,other]+=j[row,col]*j[row,other];
            }
            bool advanced=false;
            for(int trial=0;trial<10;trial++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matrix=(double[,])normal.Clone();
                for(int col=0;col<x.Length;col++)matrix[col,col]+=damping;
                if(!Solve(matrix,gradient.Select(v=>-v).ToArray(),out var step))break;
                var next=x.Zip(step,(v,d)=>v+Math.Clamp(d,-20,20)).ToArray();
                var test=Candidate(next);var testResidual=Residual(test);var testCost=Cost(testResidual);
                if(testCost<cost-1e-20)
                {x=next;candidate=test;residual=testResidual;cost=testCost;damping=Math.Max(damping/3,1e-12);advanced=true;break;}
                damping*=10;
            }
            if(!advanced)break;
        }
        var checks=relations.Select(c=>(Constraint:c,Evaluation:c.Evaluate(candidate))).ToArray();
        var details=checks.Select(v=>new AssemblyRelationResidual(v.Constraint.Id,v.Evaluation.ErrorMm,
            v.Evaluation.AngleErrorRad,v.Evaluation.Status)).ToImmutableArray();
        var failing=checks.Where(v=>v.Evaluation.Status!=AssemblyConstraintStatus.Satisfied)
            .Select(v=>v.Constraint.Id).ToImmutableArray();
        if(!failing.IsEmpty)
        {
            var failure=Fail(failing,AssemblySolveStatus.Conflict,
                "The simultaneous candidate did not satisfy all relations; no pose was committed.");
            return failure with{Report=failure.Report with{Residuals=details}};
        }
        try {candidate.Validate(validateReviewCache:false);}
        catch(CadValidationException error)
        {return Fail(relations.Select(c=>c.Id).ToImmutableArray(),AssemblySolveStatus.Conflict,error.Message);}
        var jacobian=Jacobian(x,residual,Candidate,Residual,cancellationToken);
        var components=ImmutableArray.CreateBuilder<AssemblyComponentFreedom>();
        var redundant=ImmutableArray.CreateBuilder<AssemblyConstraintId>();
        var relationRows=RelationRows(relations).ToArray();
        foreach(var group in groups)
        {
            var groupColumns=movable.Select((p,i)=>(p,i)).Where(v=>group.Contains(v.p))
                .SelectMany(v=>Enumerable.Range(v.i*6,6)).ToArray();
            var groupRows=relationRows.Where(v=>group.Contains(v.Path))
                .SelectMany(v=>Enumerable.Range(v.Start,v.Count)).ToArray();
            var sub=new double[groupRows.Length,groupColumns.Length];
            for(int row=0;row<groupRows.Length;row++)for(int col=0;col<groupColumns.Length;col++)
                sub[row,col]=jacobian[groupRows[row],groupColumns[col]];
            int rank=Rank(sub);
            var modes=Nullspace(sub,groupColumns,movable);
            components.Add(new(group.ToImmutableArray(),rank,groupColumns.Length-rank,
                Math.Max(0,groupRows.Length-rank),group.Any(fixedPaths.Contains))
                {RankUncertain=Rank(sub,1e-5)!=rank||modes.Length!=groupColumns.Length-rank,
                    NullspaceModes=modes});
            foreach(var relation in relationRows.Where(v=>group.Contains(v.Path)))
            {
                var reducedRows=groupRows.Where(row=>row<relation.Start||row>=relation.Start+relation.Count).ToArray();
                var reduced=new double[reducedRows.Length,groupColumns.Length];
                for(int row=0;row<reducedRows.Length;row++)for(int col=0;col<groupColumns.Length;col++)
                    reduced[row,col]=jacobian[reducedRows[row],groupColumns[col]];
                if(Rank(reduced)==rank)redundant.Add(relation.Id);
            }
        }
        var changed=relations.Where(c=>c.Evaluate(document).Status!=AssemblyConstraintStatus.Satisfied)
            .Select(c=>c.Id).ToImmutableArray();
        var status=roots.IsEmpty&&components.All(c=>c.LocalFreedom==0)?AssemblySolveStatus.Solved:
            AssemblySolveStatus.UnderConstrained;
        return new(candidate,new(status,changed,[],roots,status==AssemblySolveStatus.Solved?
            "All assembly relations are satisfied.":"Relations are satisfied; free motion remains.")
            {Residuals=details,Components=components.ToImmutable(),RedundantRelations=redundant.ToImmutable()});

        AssemblySolvePlan Fail(ImmutableArray<AssemblyConstraintId> ids,AssemblySolveStatus status,string message)=>
            new(document,new(status,[],ids,[],message));
    }
    private static IEnumerable<(AssemblyConstraintId Id,OccurrencePath Path,int Start,int Count)> RelationRows(AssemblyConstraint[] relations)
    {
        int start=0;
        foreach(var c in relations)
        {
            int count=c.Kind switch
            {
                AssemblyConstraintKind.Fixed=>0,AssemblyConstraintKind.Coincident=>3,
                AssemblyConstraintKind.Distance=>1,
                AssemblyConstraintKind.AngleAxes=>c.TargetAngleRad<1e-8||c.TargetAngleRad>Math.PI-1e-8?3:1,
                AssemblyConstraintKind.Coaxial=>6,AssemblyConstraintKind.PlanarMate=>4,_=>3
            };
            if(count>0)yield return(c.Id,c.PrimaryPath,start,count);
            start+=count;
        }
    }
    private static List<OccurrencePath[]> Groups(OccurrencePath[] paths,AssemblyConstraint[] relations)
    {
        var seen=new HashSet<OccurrencePath>();var result=new List<OccurrencePath[]>();
        foreach(var path in paths)
        {
            if(!seen.Add(path))continue;
            var queue=new Queue<OccurrencePath>();queue.Enqueue(path);var group=new List<OccurrencePath>();
            while(queue.Count>0)
            {
                var current=queue.Dequeue();group.Add(current);
                foreach(var c in relations.Where(c=>c.SecondaryPath is not null))
                {
                    var next=c.PrimaryPath.Equals(current)?c.SecondaryPath:
                        c.SecondaryPath!.Equals(current)?c.PrimaryPath:null;
                    if(next is not null&&seen.Add(next))queue.Enqueue(next);
                }
            }
            result.Add(group.OrderBy(p=>p.ToString()).ToArray());
        }
        return result;
    }
    private static double Cost(double[] residual)=>residual.Sum(v=>v*v);
    private static double[,] Jacobian(double[] x,double[] baseline,Func<double[],DocumentSnapshot> candidate,
        Func<DocumentSnapshot,double[]> residual,CancellationToken token)
    {
        var result=new double[baseline.Length,x.Length];
        for(int col=0;col<x.Length;col++)
        {
            token.ThrowIfCancellationRequested();
            var shifted=(double[])x.Clone();double step=col%6<3?1e-5:1e-6;shifted[col]+=step;
            var values=residual(candidate(shifted));
            for(int row=0;row<baseline.Length;row++)result[row,col]=(values[row]-baseline[row])/step;
        }
        return result;
    }
    private static int Rank(double[,] source,double tolerance=1e-7)
    {
        var matrix=(double[,])source.Clone();int rows=matrix.GetLength(0),cols=matrix.GetLength(1),rank=0;
        for(int col=0;col<cols&&rank<rows;col++)
        {
            int best=rank;for(int row=rank+1;row<rows;row++)
                if(Math.Abs(matrix[row,col])>Math.Abs(matrix[best,col]))best=row;
            if(Math.Abs(matrix[best,col])<tolerance)continue;
            for(int j=col;j<cols;j++)(matrix[rank,j],matrix[best,j])=(matrix[best,j],matrix[rank,j]);
            double pivot=matrix[rank,col];
            for(int row=rank+1;row<rows;row++)
            {double factor=matrix[row,col]/pivot;for(int j=col;j<cols;j++)matrix[row,j]-=factor*matrix[rank,j];}
            rank++;
        }
        return rank;
    }
    private static ImmutableArray<ImmutableArray<AssemblyInstanceMotion>> Nullspace(
        double[,] source,int[] columns,OccurrencePath[] movable)
    {
        var matrix=(double[,])source.Clone();int rows=matrix.GetLength(0),cols=matrix.GetLength(1);
        var pivots=new List<int>();const double tolerance=1e-7;
        for(int col=0;col<cols&&pivots.Count<rows;col++)
        {
            int pivotRow=pivots.Count,best=pivotRow;
            for(int row=pivotRow+1;row<rows;row++)
                if(Math.Abs(matrix[row,col])>Math.Abs(matrix[best,col]))best=row;
            if(Math.Abs(matrix[best,col])<tolerance)continue;
            for(int j=0;j<cols;j++)(matrix[pivotRow,j],matrix[best,j])=(matrix[best,j],matrix[pivotRow,j]);
            double pivot=matrix[pivotRow,col];
            for(int j=0;j<cols;j++)matrix[pivotRow,j]/=pivot;
            for(int row=0;row<rows;row++)
            {
                if(row==pivotRow)continue;
                double factor=matrix[row,col];
                for(int j=0;j<cols;j++)matrix[row,j]-=factor*matrix[pivotRow,j];
            }
            pivots.Add(col);
        }
        var modes=ImmutableArray.CreateBuilder<ImmutableArray<AssemblyInstanceMotion>>();
        for(int free=0;free<cols;free++)
        {
            if(pivots.Contains(free))continue;
            var vector=new double[cols];vector[free]=1;
            for(int row=0;row<pivots.Count;row++)vector[pivots[row]]=-matrix[row,free];
            double norm=Math.Sqrt(vector.Sum(v=>v*v));
            var motions=ImmutableArray.CreateBuilder<AssemblyInstanceMotion>();
            foreach(int instance in columns.Select(c=>c/6).Distinct())
            {
                double Value(int coordinate)
                {
                    int local=Array.IndexOf(columns,instance*6+coordinate);
                    return local<0?0:vector[local]/norm;
                }
                motions.Add(new(movable[instance],new(Value(0),Value(1),Value(2)),
                    new(Value(3),Value(4),Value(5))));
            }
            modes.Add(motions.ToImmutable());
        }
        return modes.ToImmutable();
    }
    private static bool Solve(double[,] a,double[] b,out double[] x)
    {
        int n=b.Length;x=new double[n];
        for(int col=0;col<n;col++)
        {
            int best=col;for(int row=col+1;row<n;row++)if(Math.Abs(a[row,col])>Math.Abs(a[best,col]))best=row;
            if(Math.Abs(a[best,col])<1e-18)return false;
            for(int k=col;k<n;k++)(a[col,k],a[best,k])=(a[best,k],a[col,k]);
            (b[col],b[best])=(b[best],b[col]);
            for(int row=col+1;row<n;row++)
            {double factor=a[row,col]/a[col,col];for(int k=col;k<n;k++)a[row,k]-=factor*a[col,k];b[row]-=factor*b[col];}
        }
        for(int row=n-1;row>=0;row--)
        {double sum=b[row];for(int col=row+1;col<n;col++)sum-=a[row,col]*x[col];x[row]=sum/a[row,row];}
        return true;
    }
}
