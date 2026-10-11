using System.Collections.Immutable;
using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Sketching;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadSketchContactAuthoring
{
    internal static async Task<string> ApplyAsync(CadDocumentViewModel document,JsonElement args,string action,CancellationToken token,DocumentCommandExpectation? expectation=null)
    {
        token.ThrowIfCancellationRequested();
        if(action is not ("set_sketch_contact" or "delete_sketch_contact"))throw new ArgumentException("Unknown sketch contact action.");
        RequireFields(args,["action",..expectation is null?new[]{"documentId","stateId"}:[],"id","revision","constraintId",..action=="set_sketch_contact"?new[]{"curveA","curveB","branch","enabled"}:[]]);
        var before=document.Session.Snapshot;var expected=expectation??new DocumentCommandExpectation(new(RequiredGuid(args,"documentId")),new(RequiredGuid(args,"stateId")));
        expected.Validate(before);var sketch=before.Sketches.GetValueOrDefault(new(RequiredGuid(args,"id")))??throw new ArgumentException("Exact sketch is missing.");
        if(sketch.Revision!=RequiredGuid(args,"revision"))throw new StaleDocumentException();
        Guid? constraintId=args.TryGetProperty("constraintId",out _)?RequiredGuid(args,"constraintId"):null;
        var old=constraintId is {} id?sketch.Constraints.SingleOrDefault(c=>c.Id.Value==id) as CircularContactConstraint??
            throw new ArgumentException("Exact circular contact constraint is missing."):null;
        ImmutableArray<SketchConstraint> constraints;
        SketchConstraintId resultId;
        if(action=="delete_sketch_contact")
        {
            if(old is null)throw new ArgumentException("Deleting contact requires constraintId.");
            resultId=old.Id;constraints=sketch.Constraints.Remove(old);
        }
        else
        {
            string branch=RequiredString(args,"branch",32);
            if(!Enum.GetNames<CircularContactBranch>().Contains(branch,StringComparer.Ordinal))throw new ArgumentException("Unknown explicit circular contact branch.");
            bool enabled=true;
            if(args.TryGetProperty("enabled",out var e))
            {if(e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("Expected boolean enabled.");enabled=e.GetBoolean();}
            var relation=new CircularContactConstraint(old?.Id??SketchConstraintId.New(),new(RequiredGuid(args,"curveA")),new(RequiredGuid(args,"curveB")),Enum.Parse<CircularContactBranch>(branch),enabled);
            SketchCircularContact.ValidateTargets(sketch,relation);resultId=relation.Id;
            constraints=old is null?sketch.Constraints.Add(relation):sketch.Constraints.Replace(old,relation);
        }
        bool changed=!sketch.Constraints.SequenceEqual(constraints);
        if(changed)await document.Session.ExecuteAsync(new UpsertSketchCommand(sketch with{Constraints=constraints},new ManagedSketchConstraintSolver()),token,expected);
        var after=document.Session.Snapshot;var current=after.Sketches[sketch.Id];
        return JsonSerializer.Serialize(new{success=true,action,changed,id=sketch.Id.ToString(),constraintId=resultId.ToString(),revision=current.Revision,
            documentId=after.Id.ToString(),stateId=after.StateId.ToString()});
    }

    internal static object Inspect(CadSketch sketch,SketchConstraint constraint)
    {
        var contact=constraint as CircularContactConstraint;Point2d? point=null;string? diagnostic=null;
        if(contact is {IsEnabled:true})
        {
            bool verified=SketchCircularContact.TryContactPoint(sketch,contact,1e-7,out var result,out var message);diagnostic=message;
            if(verified)point=result;
        }
        return new{id=constraint.Id.ToString(),type=constraint.GetType().Name.Replace("Constraint",""),constraint.IsEnabled,
            targets=SketchConstraintEditing.Targets(constraint).Select(id=>id.ToString()).ToArray(),value=SketchConstraintEditing.Value(constraint),
            branch=contact?.Branch.ToString(),contactPoint=point,diagnostic,curvature=CadSketchCurvatureAuthoring.Inspect(sketch,constraint),finiteContact=CadSketchFiniteContactAuthoring.Inspect(sketch,constraint)};
    }
}
