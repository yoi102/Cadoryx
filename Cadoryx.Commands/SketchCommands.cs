using Cadoryx.Db;
using Cadoryx.Sketching;

namespace Cadoryx.Commands;

public sealed class SketchSolveException(SketchSolveReport report) : InvalidOperationException(report.Diagnostic)
{
    public SketchSolveReport Report {get;}=report;
}

/// <summary>Solves an isolated candidate; the session owns generation checking, publication and exact history.</summary>
public sealed class UpsertSketchCommand(CadSketch candidate,ISketchConstraintSolver solver,SketchSolveOptions? options=null,string? newPartName=null) : ICadDocumentCommand
{
    public string Name=>"Edit sketch";
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        candidate.Validate();var document=context.Snapshot;
        if(!document.Definitions.ContainsKey(candidate.PartId)&&newPartName is not null)
        {
            using var addition=await ResourceCommands.AddPart(candidate.PartId,newPartName).PrepareAsync(context,cancellationToken).ConfigureAwait(false);
            document=addition.Snapshot;
        }
        if(!document.Definitions.TryGetValue(candidate.PartId,out var part)||part is not PartDefinition)
            throw new CadValidationException("A sketch must belong to an existing part.");
        if(document.Sketches.TryGetValue(candidate.Id,out var old)&&old.PartId!=candidate.PartId)
            throw new CadValidationException("Changing sketch ownership requires an explicit move operation.");
        if(old is not null&&old.Revision!=candidate.Revision)throw new CadValidationException("The sketch changed; reopen it before editing.");
        var report=await solver.SolveAsync(candidate,options,cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if(!report.Succeeded)throw new SketchSolveException(report);
        var solved=report.Solution!;
        // The injectable solver cannot replace authored identities, constraints or ownership.
        if(solved.Id!=candidate.Id||solved.Revision!=candidate.Revision||solved.PartId!=candidate.PartId||solved.Name!=candidate.Name||solved.Plane!=candidate.Plane||
            !solved.Points.Select(p=>(p.Id,p.IsConstruction)).SequenceEqual(candidate.Points.Select(p=>(p.Id,p.IsConstruction)))||
            !solved.Lines.SequenceEqual(candidate.Lines)||!solved.Circles.Select(c=>(c.Id,c.Center,c.IsConstruction)).SequenceEqual(candidate.Circles.Select(c=>(c.Id,c.Center,c.IsConstruction)))||
            !solved.Constraints.SequenceEqual(candidate.Constraints))throw new CadValidationException("Solver changed sketch intent or identity.");
        solved=solved with{Revision=Guid.NewGuid()};solved.Validate();
        var changed=document with{Sketches=document.Sketches.SetItem(solved.Id,solved)};
        var roots=changed.Features.Values.Where(f=>f.SketchSource?.SketchId==solved.Id).Select(f=>f.Id);
        return await FeatureRecompute.PrepareAsync(context with{Snapshot=changed},roots,null,cancellationToken).ConfigureAwait(false);
    }
}
public sealed class RemoveSketchCommand(SketchId id) : ICadDocumentCommand
{
    public string Name=>"Remove sketch";
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(!context.Snapshot.Sketches.ContainsKey(id))throw new CadValidationException("Sketch no longer exists.");
        if(context.Snapshot.Features.Values.Any(f=>f.SketchSource?.SketchId==id))
            throw new CadValidationException("This sketch is used by a feature and cannot be removed.");
        return Task.FromResult(new PreparedDocumentEdit((context.Snapshot with{Sketches=context.Snapshot.Sketches.Remove(id)}).WithNewState()));
    }
}
