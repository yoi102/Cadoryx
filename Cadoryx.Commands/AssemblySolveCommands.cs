using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Commands;

public enum AssemblySolveStatus { Solved, UnderConstrained, Conflict, StaleOrUnsupported }
public sealed record AssemblyRelationResidual(AssemblyConstraintId Id,double ErrorMm,double AngleErrorRad,AssemblyConstraintStatus Status);
/// <summary>Infinitesimal local motion at the solved pose, in millimetres and radians.</summary>
public sealed record AssemblyInstanceMotion(OccurrencePath Path,Vector3d TranslationMm,Vector3d RotationRad);
public sealed record AssemblyComponentFreedom(ImmutableArray<OccurrencePath> Instances,int Rank,int LocalFreedom,
    int RedundantEquations,bool HasFixedReference)
{
    public bool RankUncertain {get;init;}
    public ImmutableArray<ImmutableArray<AssemblyInstanceMotion>> NullspaceModes {get;init;}=[];
}
public sealed record AssemblySolveReport(AssemblySolveStatus Status,
    ImmutableArray<AssemblyConstraintId> Adjusted,
    ImmutableArray<AssemblyConstraintId> Conflicting,
    ImmutableArray<OccurrencePath> UnanchoredRoots,string Explanation)
{
    public const string CurrentSolverVersion="cadoryx.assembly.lm-svd.2";
    public string SolverVersion {get;init;}=CurrentSolverVersion;
    public AssemblySolveFailureReason FailureReason {get;init;}
    public int Iterations {get;init;}
    public double ElapsedMs {get;init;}
    public bool HasGlobalUniquenessProof=>false;
    public ImmutableArray<AssemblyRelationResidual> Residuals { get; init; }=[];
    public ImmutableArray<AssemblyComponentFreedom> Components { get; init; }=[];
    public ImmutableArray<AssemblyConstraintId> RedundantRelations {get;init;}=[];
}
public sealed record AssemblySolvePlan(DocumentSnapshot Candidate,AssemblySolveReport Report);

/// <summary>Bounded simultaneous assembly solve. It commits only a fully satisfied
/// candidate; conflicting or stale relations leave the document unchanged.</summary>
public static class AssemblySolveCommands
{
    public static SolveAssemblyCommand Solve()=>new();

    /// <summary>Verifies native datums and prepares the exact pose that confirmation will commit.</summary>
    public static async Task<PreparedAssemblySolveCommand> PreparePreviewAsync(DocumentCommandContext context,
        CancellationToken cancellationToken=default)
    {
        await VerifyDatumsAsync(context,cancellationToken).ConfigureAwait(false);
        var plan=await Task.Run(()=>Plan(context.Snapshot,cancellationToken),cancellationToken).ConfigureAwait(false);
        return new(context.Snapshot,context.Generation,plan);
    }

    private static Task VerifyDatumsAsync(DocumentCommandContext context,CancellationToken token)=>
        AssemblyDatumVerification.VerifyAsync(context,context.Snapshot.AssemblyConstraints.Values
            .Where(c=>c.IsEnabled).SelectMany(c=>new[]{c.PrimaryDatum,c.SecondaryDatum}.OfType<AssemblyDatumReference>()),token);

    public sealed class PreparedAssemblySolveCommand : ICadDocumentCommand
    {
        private readonly DocumentSnapshot baseline;
        private readonly long generation;
        public DocumentSnapshot Candidate {get;}
        public AssemblySolveReport Report {get;}
        public string Name=>"Confirm assembly positions";
        internal PreparedAssemblySolveCommand(DocumentSnapshot baseline,long generation,AssemblySolvePlan plan)
        {this.baseline=baseline;this.generation=generation;Candidate=plan.Candidate;Report=plan.Report;}
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
        {
            if(!ReferenceEquals(context.Snapshot,baseline)||context.Generation!=generation)
                throw new CadValidationException(Cadoryx.Lang.Strings.Strings.AssemblySolvePreviewChanged);
            if(Report.FailureReason!=AssemblySolveFailureReason.None||Report.Status is AssemblySolveStatus.Conflict or AssemblySolveStatus.StaleOrUnsupported)
                throw new CadValidationException(Report.Explanation);
            await VerifyDatumsAsync(context,cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();Candidate.Validate(validateReviewCache:false);
            return new PreparedDocumentEdit(ReferenceEquals(Candidate,baseline)?baseline:Candidate.WithNewState());
        }
    }

    public static AssemblySolvePlan Plan(DocumentSnapshot document,CancellationToken cancellationToken=default,
        AssemblySolveOptions? options=null)=>AssemblyCycleSolver.Plan(document,cancellationToken,options);

    public sealed class SolveAssemblyCommand : ICadDocumentCommand
    {
        public string Name=>"Solve assembly relations";
        public AssemblySolveReport? Report {get;private set;}
        public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await AssemblyDatumVerification.VerifyAsync(context,context.Snapshot.AssemblyConstraints.Values
                .Where(c=>c.IsEnabled).SelectMany(c=>new[]{c.PrimaryDatum,c.SecondaryDatum}
                    .OfType<AssemblyDatumReference>()),cancellationToken).ConfigureAwait(false);
            var plan=AssemblyCycleSolver.Plan(context.Snapshot,cancellationToken);Report=plan.Report;
            if(plan.Report.Status is AssemblySolveStatus.Conflict or AssemblySolveStatus.StaleOrUnsupported)
                throw new CadValidationException(plan.Report.Explanation);
            return new PreparedDocumentEdit(ReferenceEquals(plan.Candidate,context.Snapshot)?
                context.Snapshot:plan.Candidate.WithNewState());
        }
    }
}
