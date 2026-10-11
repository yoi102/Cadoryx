using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Sketching;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadSketchCurvatureAuthoring
{
    internal static async Task<string> ApplyAsync(CadDocumentViewModel document,JsonElement args,string action,CancellationToken token,DocumentCommandExpectation? expectation=null)
    {
        token.ThrowIfCancellationRequested();
        if(action is not ("set_sketch_curvature" or "delete_sketch_curvature"))throw new ArgumentException("Unknown curvature action.");
        RequireFields(args,["action",..expectation is null?new[]{"documentId","stateId"}:[],"id","revision","constraintId",..action=="set_sketch_curvature"?new[]{"curveA","curveB","enabled"}:[]]);
        var before=document.Session.Snapshot;var expected=expectation??new DocumentCommandExpectation(new(RequiredGuid(args,"documentId")),new(RequiredGuid(args,"stateId")));
        expected.Validate(before);var sketch=before.Sketches.GetValueOrDefault(new(RequiredGuid(args,"id")))??throw new ArgumentException("Exact sketch is missing.");
        if(sketch.Revision!=RequiredGuid(args,"revision"))throw new StaleDocumentException();
        var old=args.TryGetProperty("constraintId",out _)?sketch.Constraints.SingleOrDefault(c=>c.Id.Value==RequiredGuid(args,"constraintId")) as EndpointCurvatureConstraint??
            throw new ArgumentException("Exact curvature constraint is missing."):null;
        var constraints=sketch.Constraints;SketchConstraintId resultId;
        if(action=="delete_sketch_curvature")
        {
            if(old is null)throw new ArgumentException("Deleting curvature continuity requires constraintId.");
            resultId=old.Id;constraints=constraints.Remove(old);
        }
        else
        {
            bool enabled=true;
            if(args.TryGetProperty("enabled",out var e))
            {if(e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("Expected boolean enabled.");enabled=e.GetBoolean();}
            var a=new SketchEntityId(RequiredGuid(args,"curveA"));var b=new SketchEntityId(RequiredGuid(args,"curveB"));
            var relation=new EndpointCurvatureConstraint(old?.Id??SketchConstraintId.New(),a,b,SketchEndpointCurvature.CommonEndpoint(sketch,a,b),IsEnabled:enabled);
            SketchEndpointCurvature.ValidateTargets(sketch,relation);resultId=relation.Id;
            constraints=old is null?constraints.Add(relation):constraints.Replace(old,relation);
        }
        bool changed=!sketch.Constraints.SequenceEqual(constraints);
        if(changed)await document.Session.ExecuteAsync(new UpsertSketchCommand(sketch with{Constraints=constraints},new ManagedSketchConstraintSolver()),token,expected);
        var after=document.Session.Snapshot;var current=after.Sketches[sketch.Id];
        return JsonSerializer.Serialize(new{success=true,action,changed,id=sketch.Id.ToString(),constraintId=resultId.ToString(),revision=current.Revision,
            documentId=after.Id.ToString(),stateId=after.StateId.ToString()});
    }

    internal static object? Inspect(CadSketch sketch,SketchConstraint constraint)
    {
        if(constraint is not EndpointCurvatureConstraint c)return null;
        var a=SketchEndpointCurvature.Evaluate(sketch,c.A,c.Joint);var b=SketchEndpointCurvature.Evaluate(sketch,c.B,c.Joint);
        double tangentResidual=a.Tangent.X*b.Tangent.Y-a.Tangent.Y*b.Tangent.X,curvatureResidual=a.CurvaturePerMm+b.CurvaturePerMm;
        return new{joint=c.Joint.ToString(),branch=c.Branch.ToString(),first=a.CurvaturePerMm,second=b.CurvaturePerMm,tangentResidual,curvatureResidual,
            verified=c.IsEnabled&&Math.Abs(tangentResidual)<=1e-9&&a.Tangent.X*b.Tangent.X+a.Tangent.Y*b.Tangent.Y<0&&Math.Abs(curvatureResidual)<=1e-7};
    }
}
