using System.Collections.Immutable;
using System.Diagnostics;
using Cadoryx.Db;
using Strings=Cadoryx.Lang.Strings.Strings;

namespace Cadoryx.Commands;

/// <summary>Bounded simultaneous rigid-pose solve on an isolated document candidate.</summary>
internal static class AssemblyCycleSolver
{
    public static AssemblySolvePlan Plan(DocumentSnapshot document,CancellationToken cancellationToken=default,AssemblySolveOptions? options=null)
    {
        options??=new();options.Validate();cancellationToken.ThrowIfCancellationRequested();var budget=Stopwatch.StartNew();int iterations=0;
        var relations=document.AssemblyConstraints.Values.Where(c=>c.IsEnabled).OrderBy(c=>c.Id.Value).ToArray();
        var invalid=relations.Where(c=>c.Evaluate(document).Status is AssemblyConstraintStatus.MissingInstance or
            AssemblyConstraintStatus.DefinitionChanged or AssemblyConstraintStatus.TopologyStale or
            AssemblyConstraintStatus.TopologyAnchorUnsupported).Select(c=>c.Id).ToImmutableArray();
        if(!invalid.IsEmpty)return Fail(invalid,AssemblySolveStatus.StaleOrUnsupported,Strings.AssemblySolveInvalidReference,AssemblySolveFailureReason.InvalidReference);
        var paths=relations.SelectMany(c=>c.SecondaryPath is {} b?new[]{c.PrimaryPath,b}:new[]{c.PrimaryPath})
            .Distinct().OrderBy(p=>p.ToString()).ToArray();
        if(paths.Length>options.MaxInstances||relations.Length>options.MaxRelations)
            return Fail(relations.Select(c=>c.Id).ToImmutableArray(),AssemblySolveStatus.Conflict,
                Strings.AssemblySolveLimitExceeded,AssemblySolveFailureReason.LimitExceeded);
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
                    Strings.AssemblySolveSharedParent,AssemblySolveFailureReason.SharedParent);
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
        double cost=Cost(residual),damping=0.001;
        bool singular=false;
        for(int iteration=0;iteration<options.MaxIterations&&cost>1e-16&&x.Length>0;iteration++)
        {
            iterations=iteration+1;
            cancellationToken.ThrowIfCancellationRequested();
            if(budget.Elapsed>options.Budget)
                return Fail(relations.Select(c=>c.Id).ToImmutableArray(),AssemblySolveStatus.Conflict,
                    Strings.AssemblySolveTimeBudget,AssemblySolveFailureReason.TimeBudget);
            var j=Jacobian(x,residual,Candidate,Residual,relations,cancellationToken,budget,options.Budget);
            if(j is null)return TimeFailure();
            singular=!j.Cast<double>().Any(value=>Math.Abs(value)>1e-10);
            if(singular)break;
            bool advanced=false;
            for(int trial=0;trial<10;trial++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(budget.Elapsed>options.Budget)return Fail(relations.Select(c=>c.Id).ToImmutableArray(),AssemblySolveStatus.Conflict,
                    Strings.AssemblySolveTimeBudget,AssemblySolveFailureReason.TimeBudget);
                var step=AssemblyNumerics.Step(j,residual,damping);
                if(step.Any(value=>!double.IsFinite(value)))break;
                var next=x.Select((value,index)=>value+Math.Clamp(step[index],index%6<3?-20:-.35,index%6<3?20:.35)).ToArray();
                var test=Candidate(next);var testResidual=Residual(test);var testCost=Cost(testResidual);
                if(testCost<cost-1e-20)
                {x=next;candidate=test;residual=testResidual;cost=testCost;damping=Math.Max(damping/3,1e-12);advanced=true;break;}
                damping*=10;
            }
            if(!advanced)break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if(budget.Elapsed>options.Budget)return TimeFailure();
        var checks=relations.Select(c=>(Constraint:c,Evaluation:c.Evaluate(candidate))).ToArray();
        var details=checks.Select(v=>new AssemblyRelationResidual(v.Constraint.Id,v.Evaluation.ErrorMm,
            v.Evaluation.AngleErrorRad,v.Evaluation.Status)).ToImmutableArray();
        var failing=checks.Where(v=>v.Evaluation.Status!=AssemblyConstraintStatus.Satisfied)
            .Select(v=>v.Constraint.Id).ToImmutableArray();
        if(!failing.IsEmpty)
        {
            var reason=singular?AssemblySolveFailureReason.SingularJacobian:
                iterations==options.MaxIterations?AssemblySolveFailureReason.IterationLimit:AssemblySolveFailureReason.NoConvergence;
            var failure=Fail(failing,AssemblySolveStatus.Conflict,
                singular?Strings.AssemblySolveSingular:reason==AssemblySolveFailureReason.IterationLimit?Strings.AssemblySolveIterationLimit:Strings.AssemblySolveNoConvergence,reason);
            return failure with{Report=failure.Report with{Residuals=details}};
        }
        try {candidate.Validate(validateReviewCache:false);}
        catch(CadValidationException error)
        {return Fail(relations.Select(c=>c.Id).ToImmutableArray(),AssemblySolveStatus.Conflict,error.Message,AssemblySolveFailureReason.InvalidCandidate);}
        var jacobian=Jacobian(x,residual,Candidate,Residual,relations,cancellationToken,budget,options.Budget);
        if(jacobian is null)return TimeFailure();
        var components=ImmutableArray.CreateBuilder<AssemblyComponentFreedom>();
        var redundant=ImmutableArray.CreateBuilder<AssemblyConstraintId>();
        var relationRows=RelationRows(relations).ToArray();
        foreach(var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(budget.Elapsed>options.Budget)return TimeFailure();
            var groupColumns=movable.Select((p,i)=>(p,i)).Where(v=>group.Contains(v.p))
                .SelectMany(v=>Enumerable.Range(v.i*6,6)).ToArray();
            var groupRows=relationRows.Where(v=>group.Contains(v.Path))
                .SelectMany(v=>Enumerable.Range(v.Start,v.Count)).ToArray();
            var sub=new double[groupRows.Length,groupColumns.Length];
            for(int row=0;row<groupRows.Length;row++)for(int col=0;col<groupColumns.Length;col++)
                sub[row,col]=jacobian[groupRows[row],groupColumns[col]];
            var analysis=AssemblyNumerics.Analyze(sub);int rank=analysis.Rank;
            var modes=Modes(analysis.Nullspace,groupColumns,movable);
            bool degenerateDistance=relations.Any(c=>group.Contains(c.PrimaryPath)&&c.Kind==AssemblyConstraintKind.Distance&&c.TargetDistanceMm<1e-10&&c.Evaluate(candidate).ErrorMm<1e-10);
            components.Add(new(group.ToImmutableArray(),rank,groupColumns.Length-rank,
                Math.Max(0,groupRows.Length-rank),group.Any(fixedPaths.Contains))
                {RankUncertain=analysis.Uncertain||degenerateDistance||modes.Length!=groupColumns.Length-rank,
                    NullspaceModes=modes});
            foreach(var relation in relationRows.Where(v=>group.Contains(v.Path)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(budget.Elapsed>options.Budget)return Fail(relations.Select(c=>c.Id).ToImmutableArray(),AssemblySolveStatus.Conflict,
                    Strings.AssemblySolveTimeBudget,AssemblySolveFailureReason.TimeBudget);
                var reducedRows=groupRows.Where(row=>row<relation.Start||row>=relation.Start+relation.Count).ToArray();
                var reduced=new double[reducedRows.Length,groupColumns.Length];
                for(int row=0;row<reducedRows.Length;row++)for(int col=0;col<groupColumns.Length;col++)
                    reduced[row,col]=jacobian[reducedRows[row],groupColumns[col]];
                if(AssemblyNumerics.Analyze(reduced,includeNullspace:false).Rank==rank)redundant.Add(relation.Id);
            }
        }
        var changed=relations.Where(c=>c.Evaluate(document).Status!=AssemblyConstraintStatus.Satisfied)
            .Select(c=>c.Id).ToImmutableArray();
        var status=roots.IsEmpty&&components.All(c=>c.LocalFreedom==0&&!c.RankUncertain)?AssemblySolveStatus.Solved:
            AssemblySolveStatus.UnderConstrained;
        cancellationToken.ThrowIfCancellationRequested();
        if(budget.Elapsed>options.Budget)return TimeFailure();
        return new(candidate,new(status,changed,[],roots,status==AssemblySolveStatus.Solved?
            Strings.AssemblySolveLocallySatisfied:Strings.AssemblySolveLocalFreedom)
            {Iterations=iterations,ElapsedMs=budget.Elapsed.TotalMilliseconds,Residuals=details,Components=components.ToImmutable(),RedundantRelations=redundant.ToImmutable()});

        AssemblySolvePlan Fail(ImmutableArray<AssemblyConstraintId> ids,AssemblySolveStatus status,string message,AssemblySolveFailureReason reason)=>
            new(document,new(status,[],ids,[],message){FailureReason=reason,Iterations=iterations,ElapsedMs=budget.Elapsed.TotalMilliseconds});
        AssemblySolvePlan TimeFailure()=>Fail(relations.Select(c=>c.Id).ToImmutableArray(),AssemblySolveStatus.Conflict,
            Strings.AssemblySolveTimeBudget,AssemblySolveFailureReason.TimeBudget);
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
    private static double[,]? Jacobian(double[] x,double[] baseline,Func<double[],DocumentSnapshot> candidate,
        Func<DocumentSnapshot,double[]> residual,AssemblyConstraint[] relations,CancellationToken token,Stopwatch budget,TimeSpan timeBudget)
    {
        var result=new double[baseline.Length,x.Length];
        var angularRows=RelationRows(relations).Where(row=>relations.Single(c=>c.Id==row.Id) is
            {Kind:AssemblyConstraintKind.AngleAxes,TargetAngleRad:>1e-8 and <3.141592643589793})
            .SelectMany(row=>Enumerable.Range(row.Start,row.Count)).ToHashSet();
        for(int col=0;col<x.Length;col++)
        {
            token.ThrowIfCancellationRequested();
            if(budget.Elapsed>timeBudget)return null;
            var plus=(double[])x.Clone();var minus=(double[])x.Clone();double step=col%6<3?1e-5:1e-6;
            plus[col]+=step;minus[col]-=step;var forward=residual(candidate(plus));var backward=residual(candidate(minus));
            for(int row=0;row<baseline.Length;row++)
            {
                double derivative=(forward[row]-backward[row])/(2*step);
                // Aligned axes at a nonzero angle target have an unsigned-angle cusp.
                // One directional seed selects a local branch; the report never claims uniqueness.
                if(angularRows.Contains(row)&&Math.Abs(derivative)<1e-9&&Math.Abs(forward[row]-baseline[row])>step*1e-8)
                    derivative=(forward[row]-baseline[row])/step;
                result[row,col]=derivative;
            }
        }
        token.ThrowIfCancellationRequested();
        return budget.Elapsed>timeBudget?null:result;
    }
    private static ImmutableArray<ImmutableArray<AssemblyInstanceMotion>> Modes(double[][] vectors,int[] columns,OccurrencePath[] movable)
    {
        var modes=ImmutableArray.CreateBuilder<ImmutableArray<AssemblyInstanceMotion>>();
        foreach(var vector in vectors)
        {
            var motions=ImmutableArray.CreateBuilder<AssemblyInstanceMotion>();
            foreach(int instance in columns.Select(col=>col/6).Distinct())
            {
                double Value(int coordinate)
                {
                    int local=Array.IndexOf(columns,instance*6+coordinate);return local<0?0:vector[local];
                }
                motions.Add(new(movable[instance],new(Value(0),Value(1),Value(2)),new(Value(3),Value(4),Value(5))));
            }
            modes.Add(motions.ToImmutable());
        }
        return modes.ToImmutable();
    }
}
