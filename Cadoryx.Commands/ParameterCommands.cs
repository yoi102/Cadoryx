using System.Collections.Immutable;
using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;
using Cadoryx.Sketching;

namespace Cadoryx.Commands;

public interface IParameterEditCommand:ICadDocumentCommand {}
public sealed class SetParametersCommand(ParameterBook parameters,ISketchConstraintSolver? solver=null):IParameterEditCommand
{
    public string Name=>Cadoryx.Lang.Strings.Strings.DocumentParameters;
    public Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,CancellationToken token)
        =>ParameterRecompute.PrepareAsync(context,context.Snapshot with{Parameters=parameters},token,true,solver);
}

/// <summary>All driving fields are staged before solving; native results and downstream caches share one commit.</summary>
public static class ParameterRecompute
{
    public static async Task<PreparedDocumentEdit> PrepareAsync(DocumentCommandContext context,DocumentSnapshot next,CancellationToken token,
        bool allowDrivingEdits=false,ISketchConstraintSolver? solver=null)
    {
        token.ThrowIfCancellationRequested();
        SketchCurveParameterTargets.GuardStructure(context.Snapshot,next);
        SketchRegionParameterTargets.GuardStructure(context.Snapshot,next);
        ContourParameterTargets.GuardStructure(context.Snapshot,next);
        DatumRecompute.Guard(context.Snapshot,next);
        if(!next.Datums.IsEmpty)DatumPlanes.Order(next);
        await DatumRecompute.ConfirmAsync(context,next,token).ConfigureAwait(false);
        if(!allowDrivingEdits)
        {
            foreach(var binding in context.Snapshot.Parameters.Bindings)
            {
                if(!next.Parameters.Bindings.Contains(binding))continue;
                double before=ParameterTargets.Read(context.Snapshot,binding.Target).Value,after=ParameterTargets.Read(next,binding.Target).Value;
                if(before!=after)throw new CadValidationException("This dimension is driven by a named parameter. Edit the parameter or detach its binding first.");
            }
        }
        var values=ParameterGraph.Evaluate(next,token);var roots=new HashSet<FeatureId>();var sketches=new HashSet<SketchId>();
        var regionDistances=new Dictionary<SketchId,Dictionary<Guid,double>>();
        foreach(var binding in next.Parameters.Bindings)
        {
            var target=binding.Target;double value=values[binding.Parameter].Value;
            if(ParameterTargets.Read(next,target).Value==value)continue;
            if(target.Kind==ParameterTargetKind.SketchRegion)
            {
                SketchRegionParameterTargets.RequireWritable(next,target);var(sketch,group)=SketchRegionParameterTargets.Resolve(next,target);
                if(!regionDistances.TryGetValue(sketch.Id,out var distances))regionDistances.Add(sketch.Id,distances=[]);
                distances.Add(group.Id,value);sketches.Add(sketch.Id);
            }
            else if(target.Kind==ParameterTargetKind.ExactSketchCurve)
            {
                next=SketchCurveParameterTargets.Stage(next,target,value);sketches.Add(new(target.Owner));
            }
            else if(target.Kind==ParameterTargetKind.Feature)
            {
                var recipe=ParameterTargets.WriteRecipe(next,target,value,validate:false);var id=new FeatureId(target.Owner);
                next=next with{Features=next.Features.SetItem(id,next.Features[id] with{Recipe=recipe})};roots.Add(id);
            }
            else if(target.Kind==ParameterTargetKind.AssemblyPattern)
            {
                next=ParameterTargets.WriteAssemblyPattern(next,target,value);
            }
            else if(target.Kind==ParameterTargetKind.Datum)
            {
                next=ParameterTargets.WriteDatum(next,target,value);
            }
            else
            {
                var constraint=ParameterTargets.WriteConstraint(next,target,value);var id=new SketchId(target.Owner);var sketch=next.Sketches[id];
                next=next with{Sketches=next.Sketches.SetItem(id,sketch with{Constraints=sketch.Constraints.Select(c=>c.Id==constraint.Id?constraint:c).ToImmutableArray()})};sketches.Add(id);
            }
        }
        foreach(var id in sketches.ToArray())
        {
            token.ThrowIfCancellationRequested();var sketch=next.Sketches[id];
            var staged=sketch.RegionOffsets.IsEmpty?SketchCurveParameterTargets.ValidateAndSynchronize(sketch):
                SketchExpressionCommands.Regions(context,sketch)!.Refresh(SketchCurveParameterTargets.PrepareNumericSources(sketch),token,
                    distanceOverrides:regionDistances.GetValueOrDefault(id));
            next=next with{Sketches=next.Sketches.SetItem(id,staged)};
        }
        foreach(var id in roots)next.Features[id].Recipe.Validate();
        foreach(var pattern in next.AssemblyPatterns.Values)pattern.Validate();
        foreach(var plane in next.Datums.Planes.Values)
            if(context.Snapshot.Datums.Planes.TryGetValue(plane.Id,out var old)&&
                (plane with{Frame=old.Frame,SourceRevisions=old.SourceRevisions,Revision=old.Revision})!=old)
                next=next with{Datums=next.Datums with{Planes=next.Datums.Planes.SetItem(plane.Id,plane with{Revision=Guid.NewGuid()})}};
        next=DatumRecompute.RefreshAuthoredRevisions(context.Snapshot,next);
        if(!next.Datums.IsEmpty)next=DatumRecompute.Refresh(next,sketches);
        foreach(var f in next.Features.Values)
            if(DatumPlanes.HasStaleInput(next,f.Id))roots.Add(f.Id);
        foreach(var f in next.Features.Values)
            if(f.Recipe is HoleRecipe hole&&!f.IsSuppressed&&!DatumPlanes.HasStaleInput(next,f.Id))
            {
                var refreshed=hole.Refresh(next,f.PartId);
                if(refreshed.DatumRevision!=hole.DatumRevision||!DatumPlanes.SameFrame(refreshed.Frame,hole.Frame))roots.Add(f.Id);
            }
        foreach(var f in next.Features.Values)
            if(f.Recipe is RepetitionRecipe repetition&&!f.IsSuppressed&&!DatumPlanes.HasStaleInput(next,f.Id))
            {
                var refreshed=repetition.Refresh(next,f.PartId);
                if(refreshed.DatumRevision!=repetition.DatumRevision||!DatumPlanes.SameFrame(refreshed.Frame,repetition.Frame))roots.Add(f.Id);
            }
        foreach(var f in next.Features.Values)
            if(f.Recipe is HolePatternRecipe pattern&&!f.IsSuppressed&&!DatumPlanes.HasStaleInput(next,f.Id))
            {
                var refreshed=pattern.Layout.Refresh(next,f.PartId);
                if(refreshed.DatumRevision!=pattern.Layout.DatumRevision||!DatumPlanes.SameFrame(refreshed.Frame,pattern.Layout.Frame))roots.Add(f.Id);
            }
        foreach(var f in next.Features.Values)
            if(f.Recipe is ExtrusionPatternRecipe extrusionPattern&&!f.IsSuppressed&&!DatumPlanes.HasStaleInput(next,f.Id))
            {
                var refreshed=extrusionPattern.Layout.Refresh(next,f.PartId);
                if(refreshed.DatumRevision!=extrusionPattern.Layout.DatumRevision||!DatumPlanes.SameFrame(refreshed.Frame,extrusionPattern.Layout.Frame))roots.Add(f.Id);
            }
        foreach(var f in next.Features.Values)
            if(f.Recipe is ExtrudeRecipe {Options:{Datum:not null} options}&&!f.IsSuppressed&&!DatumPlanes.HasStaleInput(next,f.Id)&&
                options.Refresh(next,f.PartId)!=options)roots.Add(f.Id);
        foreach(var f in next.Features.Values)
            if(f.Recipe is DraftRecipe draft&&!f.IsSuppressed&&!DatumPlanes.HasStaleInput(next,f.Id)&&
                !draft.Programs.SequenceEqual(draft.Refresh(next,f.PartId).Programs))roots.Add(f.Id);
        foreach(var f in next.Features.Values)
            if(f.Recipe is RevolveRecipe {Axis:{Datum:not null}} revolve&&!f.IsSuppressed&&!DatumPlanes.HasStaleInput(next,f.Id)&&
                revolve.Refresh(next,f.PartId).Axis!=revolve.Axis)roots.Add(f.Id);
        solver??=new ManagedSketchConstraintSolver(context.Kernel as IExactSketchExpressionRegions,context.Kernel as ISketchExpressionRegionVerifier);
        foreach(var sketch in SketchProjectionGeometry.Order(next.Sketches))
        {
            token.ThrowIfCancellationRequested();bool projected=sketch.Projections.Any(p=>sketches.Contains(p.SourceSketch));
            if(!sketches.Contains(sketch.Id)&&!projected)continue;
            var candidate=SketchExpressionCommands.Refresh(context,SketchProjectionGeometry.Refresh(next.Sketches[sketch.Id],next.Sketches,projected),token);
            var report=await solver.SolveAsync(candidate,cancellationToken:token).ConfigureAwait(false);
            if(!report.Succeeded)throw new SketchSolveException(report);
            var solved=report.Solution!;var regionContext=SketchExpressionCommands.Regions(context,candidate);SketchSolverIntent.Validate(candidate,solved,regionContext,token);
            solved=solved with{Revision=Guid.NewGuid()};solved.Validate();SketchContactSolutionVerification.Validate(candidate,solved,regionContext:regionContext);
            SketchExpressionCommands.Confirm(context,solved,token);
            next=next with{Sketches=next.Sketches.SetItem(solved.Id,solved)};sketches.Add(solved.Id);
        }
        foreach(var f in next.Features.Values.Where(f=>GuidedAuthoringSources.Sketches(f).Any(sketches.Contains)))roots.Add(f.Id);
        SketchProjectionGeometry.ValidateGraph(next.Sketches);ParameterGraph.ValidateApplied(next);
        if(roots.Count==0)return new(next.StateId==context.Snapshot.StateId&&(next.Parameters!=context.Snapshot.Parameters||next.Datums!=context.Snapshot.Datums)?next.WithNewState():next);
        var edit=await FeatureRecompute.PrepareAsync(context with{Snapshot=next},roots,null,token,geometryBaseline:context.Snapshot).ConfigureAwait(false);
        try
        {
            // Box source revisions may have changed during native recompute; geometry has already used the staged semantic frames.
            if(edit.Snapshot.Datums.IsEmpty)return edit;
            var refreshed=DatumRecompute.Refresh(edit.Snapshot,new HashSet<SketchId>());
            foreach(var f in refreshed.Features.Values)
                if(f.Recipe is HoleRecipe hole&&!f.IsStale&&!f.IsSuppressed)
                {
                    var current=hole.Refresh(refreshed,f.PartId);
                    if(!DatumPlanes.SameFrame(current.Frame,hole.Frame))throw new CadValidationException("Hole frame changed after source recompute.");
                    if(current.DatumRevision!=hole.DatumRevision)refreshed=refreshed with{Features=refreshed.Features.SetItem(f.Id,f with{Recipe=current})};
                }
            foreach(var f in refreshed.Features.Values)
                if(f.Recipe is RepetitionRecipe repetition&&!f.IsStale&&!f.IsSuppressed)
                {
                    var current=repetition.Refresh(refreshed,f.PartId);
                    if(!DatumPlanes.SameFrame(current.Frame,repetition.Frame))throw new CadValidationException("Repetition frame changed after source recompute.");
                    if(current.DatumRevision!=repetition.DatumRevision)refreshed=refreshed with{Features=refreshed.Features.SetItem(f.Id,f with{Recipe=current})};
                }
            foreach(var f in refreshed.Features.Values)
                if(f.Recipe is HolePatternRecipe pattern&&!f.IsStale&&!f.IsSuppressed)
                {
                    var current=pattern.Refresh(refreshed,f.PartId);
                    if(!DatumPlanes.SameFrame(current.Layout.Frame,pattern.Layout.Frame))throw new CadValidationException("Hole replay layout changed after source recompute.");
                    if(current.Layout.DatumRevision!=pattern.Layout.DatumRevision)refreshed=refreshed with{Features=refreshed.Features.SetItem(f.Id,f with{Recipe=current})};
                }
            foreach(var f in refreshed.Features.Values)
                if(f.Recipe is ExtrusionPatternRecipe extrusionPattern&&!f.IsStale&&!f.IsSuppressed)
                {
                    var current=extrusionPattern.Refresh(refreshed,f.PartId);
                    if(!DatumPlanes.SameFrame(current.Layout.Frame,extrusionPattern.Layout.Frame))throw new CadValidationException("Extrusion replay layout changed after source recompute.");
                    if(current.Layout.DatumRevision!=extrusionPattern.Layout.DatumRevision)refreshed=refreshed with{Features=refreshed.Features.SetItem(f.Id,f with{Recipe=current})};
                }
            foreach(var f in refreshed.Features.Values)
                if(f.Recipe is ExtrudeRecipe {Options:{Datum:not null} options} extrusion&&!f.IsStale&&!f.IsSuppressed)
                {
                    var current=options.Refresh(refreshed,f.PartId);
                    if(current.LimitPlane is {} a&&options.LimitPlane is {} b&&!DatumPlanes.SameFrame(a,b))
                        throw new CadValidationException("Extrusion limit changed after source recompute.");
                    if(current!=options)refreshed=refreshed with{Features=refreshed.Features.SetItem(f.Id,f with{Recipe=extrusion with{Options=current}})};
                }
            foreach(var f in refreshed.Features.Values)
                if(f.Recipe is RevolveRecipe {Axis:{Datum:not null} axis} revolve&&!f.IsStale&&!f.IsSuppressed)
                {
                    var current=revolve.Refresh(refreshed,f.PartId);
                    if(!axis.SameLineValues(current.Axis!))throw new CadValidationException("Revolution axis changed after source recompute.");
                    if(current.Axis!=axis)refreshed=refreshed with{Features=refreshed.Features.SetItem(f.Id,f with{Recipe=current})};
                }
            foreach(var f in refreshed.Features.Values)
                if(f.Recipe is DraftRecipe draft&&!f.IsStale&&!f.IsSuppressed)
                {
                    var current=draft.Refresh(refreshed,f.PartId);
                    if(draft.Programs.Zip(current.Programs).Any(p=>!DatumPlanes.SameFrame(p.First.NeutralPlane,p.Second.NeutralPlane)))
                        throw new CadValidationException("Draft neutral plane changed after source recompute.");
                    if(!current.Programs.SequenceEqual(draft.Programs))refreshed=refreshed with{Features=refreshed.Features.SetItem(f.Id,f with{Recipe=current})};
                }
            return new(refreshed,[edit]);
        }
        catch{edit.Dispose();throw;}
    }
}

internal static class SketchSolverIntent
{
    public static void Validate(CadSketch candidate,CadSketch solved,SketchRegionEvaluationContext? regionContext=null,CancellationToken token=default)
    {
        if(solved.Id!=candidate.Id||solved.Revision!=candidate.Revision||solved.PartId!=candidate.PartId||solved.Name!=candidate.Name||solved.Plane!=candidate.Plane||
            !solved.Points.Select(p=>(p.Id,p.IsConstruction,p.Origin)).SequenceEqual(candidate.Points.Select(p=>(p.Id,p.IsConstruction,p.Origin)))||
            !solved.Lines.SequenceEqual(candidate.Lines)||!solved.Circles.Select(c=>(c.Id,c.Center,c.IsConstruction)).SequenceEqual(candidate.Circles.Select(c=>(c.Id,c.Center,c.IsConstruction)))||
            !solved.Arcs.SequenceEqual(candidate.Arcs)||!solved.Beziers.SequenceEqual(candidate.Beziers)||!solved.Splines.SequenceEqual(candidate.Splines)||
            !solved.Ellipses.SequenceEqual(candidate.Ellipses)||!solved.Freeforms.SequenceEqual(candidate.Freeforms)||
            !solved.NativeProjections.SequenceEqual(candidate.NativeProjections)||
            !solved.Projections.SequenceEqual(candidate.Projections))
            throw new CadValidationException("Solver changed sketch intent or identity.");
        SketchFiniteParametricContact.RequireUnchangedAuthors(candidate,solved);
        if(regionContext is null)SketchExpressionEntities.ValidateSolved(candidate,solved);else regionContext.ValidateSolved(candidate,solved,token);
    }
}
