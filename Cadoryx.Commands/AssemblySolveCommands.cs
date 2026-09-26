using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

public enum AssemblySolveStatus { Solved, UnderConstrained, Conflict, StaleOrUnsupported }
public sealed record AssemblySolveReport(AssemblySolveStatus Status,
    ImmutableArray<AssemblyConstraintId> Adjusted,
    ImmutableArray<AssemblyConstraintId> Conflicting,
    ImmutableArray<OccurrencePath> UnanchoredRoots,string Explanation);
public sealed record AssemblySolvePlan(DocumentSnapshot Candidate,AssemblySolveReport Report);

/// <summary>Conservative directed datum-graph solver. It commits only a fully satisfied result;
/// ambiguous multi-parent and cyclic unsatisfied graphs require explicit editing.</summary>
public static class AssemblySolveCommands
{
    public static SolveAssemblyCommand Solve()=>new();

    public static AssemblySolvePlan Plan(DocumentSnapshot document)
    {
        var enabled=document.AssemblyConstraints.Values.Where(c=>c.IsEnabled).OrderBy(c=>c.Id.Value).ToArray();
        var stale=enabled.Where(c=>c.Evaluate(document).Status is AssemblyConstraintStatus.MissingInstance or
            AssemblyConstraintStatus.DefinitionChanged or AssemblyConstraintStatus.TopologyStale or
            AssemblyConstraintStatus.TopologyAnchorUnsupported).Select(c=>c.Id).ToImmutableArray();
        if(!stale.IsEmpty)return Failure(AssemblySolveStatus.StaleOrUnsupported,stale,
            "Reselect stale paths or remove unsupported topology anchors before solving.");
        var pairs=enabled.Where(c=>c.Kind!=AssemblyConstraintKind.Fixed).ToArray();
        var duplicate=pairs.GroupBy(c=>c.SecondaryPath!).Where(g=>g.Count()>1&&
            g.Any(c=>c.Evaluate(document).Status!=AssemblyConstraintStatus.Satisfied))
            .SelectMany(g=>g.Select(c=>c.Id)).Distinct().ToImmutableArray();
        if(!duplicate.IsEmpty)return Failure(AssemblySolveStatus.Conflict,duplicate,
            "Several unsatisfied relations drive the same moving instance; choose one direction or isolate the relation.");

        var paths=pairs.SelectMany(c=>new[]{c.PrimaryPath,c.SecondaryPath!}).Distinct().ToArray();
        var outgoing=paths.ToDictionary(path=>path,_=>new List<AssemblyConstraint>());
        var indegree=paths.ToDictionary(path=>path,_=>0);
        foreach(var pair in pairs){outgoing[pair.PrimaryPath].Add(pair);indegree[pair.SecondaryPath!]++;}
        var ready=new Queue<OccurrencePath>(paths.Where(path=>indegree[path]==0)
            .OrderBy(path=>path.Slots.Length).ThenBy(path=>path.ToString()));
        var ordered=new List<AssemblyConstraint>();
        while(ready.Count>0)
        {
            var source=ready.Dequeue();
            foreach(var pair in outgoing[source].OrderBy(c=>c.Id.Value))
            {
                ordered.Add(pair);
                if(--indegree[pair.SecondaryPath!]==0)ready.Enqueue(pair.SecondaryPath!);
            }
        }
        if(ordered.Count!=pairs.Length)
        {
            var cyclic=pairs.Except(ordered).Where(c=>c.Evaluate(document).Status!=AssemblyConstraintStatus.Satisfied)
                .Select(c=>c.Id).ToImmutableArray();
            if(!cyclic.IsEmpty)return Failure(AssemblySolveStatus.Conflict,cyclic,
                "A cyclic unsatisfied relation graph has no unique directed adjustment.");
            ordered.AddRange(pairs.Except(ordered));
        }

        var candidate=document;var adjusted=ImmutableArray.CreateBuilder<AssemblyConstraintId>();
        foreach(var pair in ordered)
        {
            if(pair.Evaluate(candidate).Status==AssemblyConstraintStatus.Satisfied)continue;
            try
            {
                candidate=AssemblyConstraintCommands.AdjustPairSnapshot(candidate,pair.Id);
                candidate.Validate();adjusted.Add(pair.Id);
            }
            catch(CadValidationException error)
            {
                return Failure(AssemblySolveStatus.Conflict,[pair.Id],error.Message);
            }
        }
        var residual=enabled.Where(c=>c.Evaluate(candidate).Status!=AssemblyConstraintStatus.Satisfied)
            .Select(c=>c.Id).ToImmutableArray();
        if(!residual.IsEmpty)return Failure(AssemblySolveStatus.Conflict,residual,
            "The candidate does not satisfy every enabled relation; no placement was committed.");
        var fixedPaths=enabled.Where(c=>c.Kind==AssemblyConstraintKind.Fixed).Select(c=>c.PrimaryPath).ToHashSet();
        var roots=paths.Where(path=>!pairs.Any(c=>c.SecondaryPath!.Equals(path))&&!fixedPaths.Contains(path))
            .OrderBy(path=>path.ToString()).ToImmutableArray();
        var status=roots.IsEmpty?AssemblySolveStatus.Solved:AssemblySolveStatus.UnderConstrained;
        return new(candidate,new(status,adjusted.ToImmutable(),[],roots,
            roots.IsEmpty?"All enabled relations are satisfied.":
                "Relations are satisfied, but at least one component has no fixed reference pose."));

        AssemblySolvePlan Failure(AssemblySolveStatus status,ImmutableArray<AssemblyConstraintId> ids,string message)=>
            new(document,new(status,[],ids,[],message));
    }

    public sealed class SolveAssemblyCommand : ICadDocumentCommand
    {
        public string Name=>"Solve assembly relations";
        public AssemblySolveReport? Report {get;private set;}
        public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan=Plan(context.Snapshot);Report=plan.Report;
            if(plan.Report.Status is AssemblySolveStatus.Conflict or AssemblySolveStatus.StaleOrUnsupported)
                throw new CadValidationException(plan.Report.Explanation);
            return Task.FromResult(new PreparedDocumentEdit(ReferenceEquals(plan.Candidate,context.Snapshot)?
                context.Snapshot:plan.Candidate.WithNewState()));
        }
    }
}
