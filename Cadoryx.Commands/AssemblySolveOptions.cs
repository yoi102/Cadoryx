namespace Cadoryx.Commands;

/// <summary>Budgets may be reduced for an interactive caller, never raised above the supported solver envelope.</summary>
public sealed record AssemblySolveOptions(int MaxInstances=32,int MaxRelations=128,int MaxIterations=100,
    TimeSpan? TimeBudget=null)
{
    internal TimeSpan Budget=>TimeBudget??TimeSpan.FromSeconds(5);
    internal void Validate()
    {
        if(MaxInstances is <1 or >32||MaxRelations is <1 or >128||MaxIterations is <1 or >100||
            Budget<=TimeSpan.Zero||Budget>TimeSpan.FromSeconds(5))
            throw new ArgumentOutOfRangeException(nameof(AssemblySolveOptions));
    }
}

public enum AssemblySolveFailureReason
{None,InvalidReference,LimitExceeded,SharedParent,TimeBudget,IterationLimit,SingularJacobian,NoConvergence,InvalidCandidate}
