using Cadoryx.Db;
using Cadoryx.Sketching;

namespace Cadoryx.Commands;

public sealed class SketchSolveException(SketchSolveReport report) : InvalidOperationException(report.Diagnostic)
{
    public SketchSolveReport Report {get;}=report;
}

/// <summary>Solves an isolated candidate; the session owns generation checking, publication and exact history.</summary>
public sealed class UpsertSketchCommand(CadSketch candidate,ISketchConstraintSolver solver,SketchSolveOptions? options=null,string? newPartName=null,
    IReadOnlyList<SketchCornerReplacement>? cornerReplacements=null,IReadOnlyList<FeatureId>? updateProfiles=null,Guid? datum=null) : ICadDocumentCommand
{
    public string Name=>"Edit sketch";
    public async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        var document=context.Snapshot;
        if(document.Sketches.TryGetValue(candidate.Id,out var previous))
        {SketchPointUsage.ValidateDrivenEdit(previous,candidate);SketchExpressionEntities.ValidateDrivenEdit(previous,candidate);}
        candidate=SketchExpressionCommands.Refresh(context,candidate,cancellationToken);candidate.Validate();
        if(document.Datums.SketchBindings.ContainsKey(candidate.Id)&&!DatumPlanes.SameFrame(candidate.Plane,document.Sketches[candidate.Id].Plane))
            throw new CadValidationException("Sketch plane is driven by a datum. Edit or detach its datum first.");
        var profileUpdates=SketchCornerProfileUpdate.Build(document,candidate,cornerReplacements?.ToArray()??[],updateProfiles?.ToArray()??[]);
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
        if(old is not null)
        {
            SketchProjectionGeometry.ValidateDrivenEdit(old,candidate);
            SketchNativeProjectionGeometry.ValidateDrivenEdit(old,candidate);
        }
        await SketchNativeProjectionCommands.ConfirmAsync(context with{Snapshot=document},candidate,cancellationToken).ConfigureAwait(false);
        candidate=SketchProjectionGeometry.Refresh(candidate,document.Sketches,false);
        candidate=SketchExpressionCommands.Refresh(context,candidate,cancellationToken);
        _=ParameterGraph.Evaluate(document with{Sketches=document.Sketches.SetItem(candidate.Id,candidate)},cancellationToken);
        var report=await solver.SolveAsync(candidate,options,cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if(!report.Succeeded)throw new SketchSolveException(report);
        var solved=report.Solution!;
        // The injectable solver cannot replace authored identities, constraints or ownership.
        var regionContext=SketchExpressionCommands.Regions(context,candidate);
        SketchSolverIntent.Validate(candidate,solved,regionContext,cancellationToken);
        solved=solved with{Revision=Guid.NewGuid()};solved.Validate();SketchContactSolutionVerification.Validate(candidate,solved,options,regionContext);
        SketchExpressionCommands.Confirm(context,solved,cancellationToken);
        var changed=document with{Sketches=document.Sketches.SetItem(solved.Id,solved)};
        if(datum is {} datumId)
        {
            if(old is not null||!document.Datums.Planes.TryGetValue(datumId,out var plane)||plane.IsStale||plane.PartId!=solved.PartId||!DatumPlanes.SameFrame(plane.Frame,solved.Plane))
                throw new CadValidationException("A new datum-bound sketch requires its explicit owner and current local frame.");
            changed=changed with{Datums=changed.Datums with{SketchBindings=changed.Datums.SketchBindings.Add(solved.Id,new(solved.Id,datumId,plane.Revision))}};
        }
        var affected=new HashSet<SketchId>{solved.Id};
        foreach(var dependent in SketchProjectionGeometry.Order(changed.Sketches))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(dependent.Id==solved.Id||!dependent.Projections.Any(p=>affected.Contains(p.SourceSketch)))continue;
            var refreshed=SketchExpressionCommands.Refresh(context,SketchProjectionGeometry.Refresh(dependent,changed.Sketches,true),cancellationToken);
            var dependentReport=await solver.SolveAsync(refreshed,options,cancellationToken).ConfigureAwait(false);
            if(!dependentReport.Succeeded)throw new SketchSolveException(dependentReport);
            var result=dependentReport.Solution!;
            if(result.Id!=refreshed.Id||result.PartId!=refreshed.PartId||result.Revision!=refreshed.Revision||result.Name!=refreshed.Name||result.Plane!=refreshed.Plane||
                !result.Points.Select(p=>(p.Id,p.IsConstruction,p.Origin)).SequenceEqual(refreshed.Points.Select(p=>(p.Id,p.IsConstruction,p.Origin)))||
                !result.Lines.SequenceEqual(refreshed.Lines)||!result.Circles.Select(c=>(c.Id,c.Center,c.IsConstruction)).SequenceEqual(refreshed.Circles.Select(c=>(c.Id,c.Center,c.IsConstruction)))||
                !result.Arcs.SequenceEqual(refreshed.Arcs)||!result.Beziers.SequenceEqual(refreshed.Beziers)||!result.Splines.SequenceEqual(refreshed.Splines)||!result.Ellipses.SequenceEqual(refreshed.Ellipses)||!result.Freeforms.SequenceEqual(refreshed.Freeforms)||
                !result.Projections.SequenceEqual(refreshed.Projections)||!result.NativeProjections.SequenceEqual(refreshed.NativeProjections))
                throw new CadValidationException("Solver changed projected sketch intent or identity.");
            SketchFiniteParametricContact.RequireUnchangedAuthors(refreshed,result);
            var dependentRegions=SketchExpressionCommands.Regions(context,refreshed);
            if(dependentRegions is null)SketchExpressionEntities.ValidateSolved(refreshed,result);else dependentRegions.ValidateSolved(refreshed,result,cancellationToken);
            result=result with{Revision=Guid.NewGuid()};result.Validate();SketchContactSolutionVerification.Validate(refreshed,result,options,dependentRegions);
            SketchExpressionCommands.Confirm(context,result,cancellationToken);
            changed=changed with{Sketches=changed.Sketches.SetItem(result.Id,result)};affected.Add(result.Id);
        }
        SketchProjectionGeometry.ValidateGraph(changed.Sketches);
        var roots=changed.Features.Values.Where(f=>GuidedAuthoringSources.Sketches(f).Any(affected.Contains)).Select(f=>f.Id);
        return await FeatureRecompute.PrepareAsync(context with{Snapshot=changed},roots,null,cancellationToken,profileUpdates).ConfigureAwait(false);
    }
}
public sealed class RemoveSketchCommand(SketchId id) : ICadDocumentCommand
{
    public string Name=>"Remove sketch";
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(!context.Snapshot.Sketches.ContainsKey(id))throw new CadValidationException("Sketch no longer exists.");
        if(context.Snapshot.Features.Values.Any(f=>GuidedAuthoringSources.Sketches(f).Contains(id))||context.Snapshot.Sketches.Values.Any(s=>s.Projections.Any(p=>p.SourceSketch==id)))
            throw new CadValidationException("This sketch is referenced by a feature or projection and cannot be removed.");
        return Task.FromResult(new PreparedDocumentEdit((context.Snapshot with{Sketches=context.Snapshot.Sketches.Remove(id)}).WithNewState()));
    }
}
