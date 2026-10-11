using System.Text.Json;
using Cadoryx.Commands;
using Cadoryx.Db;
using Cadoryx.Editor;
using Cadoryx.Sketching;
using static Cadoryx.ViewModels.Toolboxes.CadAgentParameterTools;

namespace Cadoryx.ViewModels.Toolboxes;

internal static class CadSketchFiniteContactAuthoring
{
    internal static async Task<string> ApplyAsync(CadDocumentViewModel document,JsonElement args,string action,CancellationToken token,DocumentCommandExpectation? expectation=null)
    {
        token.ThrowIfCancellationRequested();
        if(action is not ("set_sketch_finite_contact" or "delete_sketch_finite_contact"))throw new ArgumentException("Unknown finite contact action.");
        RequireFields(args,["action",..expectation is null?new[]{"documentId","stateId"}:[],"id","revision","constraintId",
            ..action=="set_sketch_finite_contact"?new[]{"curveA","curveB","parameterA","parameterB","direction","continuity","enabled",
                "parameterMode","seedA","lowerA","upperA","seedB","lowerB","upperB"}:[]]);
        var before=document.Session.Snapshot;var expected=expectation??new DocumentCommandExpectation(new(RequiredGuid(args,"documentId")),new(RequiredGuid(args,"stateId")));
        expected.Validate(before);var sketch=before.Sketches.GetValueOrDefault(new(RequiredGuid(args,"id")))??throw new ArgumentException("Exact sketch is missing.");
        if(sketch.Revision!=RequiredGuid(args,"revision"))throw new StaleDocumentException();
        var old=args.TryGetProperty("constraintId",out _)?sketch.Constraints.SingleOrDefault(c=>c.Id.Value==RequiredGuid(args,"constraintId")) as FiniteParametricContactConstraint??
            throw new ArgumentException("Exact finite contact constraint is missing."):null;
        var constraints=sketch.Constraints;SketchConstraintId resultId;
        if(action=="delete_sketch_finite_contact")
        {
            if(old is null)throw new ArgumentException("Deleting finite contact requires constraintId.");
            resultId=old.Id;constraints=constraints.Remove(old);
        }
        else
        {
            double Parameter(string key)
            {
                if(!args.TryGetProperty(key,out var value)||value.ValueKind!=JsonValueKind.Number||!value.TryGetDouble(out var number)||!double.IsFinite(number)||number<=0||number>=1)
                    throw new ArgumentException(key+" must be a finite normalized number strictly inside (0,1).");
                return number;
            }
            bool enabled=true;
            if(args.TryGetProperty("enabled",out var e))
            {if(e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new ArgumentException("Expected boolean enabled.");enabled=e.GetBoolean();}
            var branch=RequiredString(args,"direction",32) switch
                {"SameDirection"=>FiniteContactTangentBranch.SameDirection,"OppositeDirection"=>FiniteContactTangentBranch.OppositeDirection,_=>throw new ArgumentException("Explicit contact direction is required.")};
            var continuity=args.TryGetProperty("continuity",out var order)?order.ValueKind==JsonValueKind.String?order.GetString() switch
                {"G1"=>FiniteContactContinuity.Tangent,"G2"=>FiniteContactContinuity.Curvature,_=>throw new ArgumentException("Continuity must be G1 or G2.")}:throw new ArgumentException("Continuity must be a string."):
                old?.Continuity??FiniteContactContinuity.Tangent;
            var mode=args.TryGetProperty("parameterMode",out var selectedMode)?selectedMode.ValueKind==JsonValueKind.String?selectedMode.GetString() switch
                {"Fixed"=>FiniteContactParameterMode.Fixed,"SolveA"=>FiniteContactParameterMode.SolveA,"SolveB"=>FiniteContactParameterMode.SolveB,"SolveBoth"=>FiniteContactParameterMode.SolveBoth,
                    _=>throw new ArgumentException("Parameter mode must be Fixed, SolveA, SolveB or SolveBoth.")}:throw new ArgumentException("Parameter mode must be a string."):
                old?.ParameterMode??FiniteContactParameterMode.Fixed;
            bool solvesA=mode is FiniteContactParameterMode.SolveA or FiniteContactParameterMode.SolveBoth,solvesB=mode is FiniteContactParameterMode.SolveB or FiniteContactParameterMode.SolveBoth;
            SlidingContactParameter? Sliding(bool a,bool automatic)
            {
                string side=a?"A":"B";
                if(!automatic)
                {
                    if(new[]{"seed","lower","upper"}.Any(key=>args.TryGetProperty(key+side,out _)))throw new ArgumentException("Fixed parameters cannot contain hidden automatic authors.");
                    return null;
                }
                if(args.TryGetProperty("parameter"+side,out _))throw new ArgumentException("Automatic parameter"+side+" is read-only; supply its seed and bounds.");
                double Bound(string key)
                {
                    if(!args.TryGetProperty(key+side,out var value)||value.ValueKind!=JsonValueKind.Number||!value.TryGetDouble(out var number)||!double.IsFinite(number))
                        throw new ArgumentException("An automatic parameter requires explicit finite seed/lower/upper fields.");
                    return number;
                }
                var author=new SlidingContactParameter(Parameter("seed"+side),Bound("lower"),Bound("upper"));author.Validate(author.Seed);return author;
            }
            var slidingA=Sliding(true,solvesA);var slidingB=Sliding(false,solvesB);
            var relation=new FiniteParametricContactConstraint(old?.Id??SketchConstraintId.New(),new(RequiredGuid(args,"curveA")),new(RequiredGuid(args,"curveB")),
                slidingA?.Seed??Parameter("parameterA"),slidingB?.Seed??Parameter("parameterB"),branch,enabled)
                {Continuity=continuity,ParameterMode=mode,SlidingA=slidingA,SlidingB=slidingB};
            // An identical author request must retain its verified solution, rather
            // than replacing the read-only result with the seed and forcing a no-op solve.
            if(old is not null&&(relation with{ParameterA=old.ParameterA,ParameterB=old.ParameterB})==old&&
               (solvesA||relation.ParameterA==old.ParameterA)&&(solvesB||relation.ParameterB==old.ParameterB))
                relation=old;
            SketchFiniteParametricContact.ValidateTargets(sketch,relation);resultId=relation.Id;
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
        if(constraint is not FiniteParametricContactConstraint c)return null;
        var a=SketchFiniteParametricContact.EvaluateGeometry(sketch,c.A,c.ParameterA,(int)c.Continuity);var b=SketchFiniteParametricContact.EvaluateGeometry(sketch,c.B,c.ParameterB,(int)c.Continuity);
        double dx=a.Position.X-b.Position.X,dy=a.Position.Y-b.Position.Y,cross=a.Tangent.X*b.Tangent.Y-a.Tangent.Y*b.Tangent.X;
        double dot=a.Tangent.X*b.Tangent.X+a.Tangent.Y*b.Tangent.Y;
        double curvature=a.CurvaturePerMm-(c.Branch==FiniteContactTangentBranch.SameDirection?1:-1)*b.CurvaturePerMm;
        return new{c.ParameterA,c.ParameterB,direction=c.Branch.ToString(),continuity=c.Continuity==FiniteContactContinuity.Curvature?"G2":"G1",
            parameterMode=c.ParameterMode.ToString(),authorA=c.SlidingA,authorB=c.SlidingB,parameterAReadOnly=c.SolvesA,parameterBReadOnly=c.SolvesB,
            positionResidualMm=new Point2d(dx,dy),tangentResidual=cross,curvatureResidualPerMm=curvature,
            verified=c.IsEnabled&&Math.Abs(dx)<=1e-7&&Math.Abs(dy)<=1e-7&&Math.Abs(cross)<=1e-9&&(c.Branch==FiniteContactTangentBranch.SameDirection?dot>0:dot<0)&&
                (c.Continuity!=FiniteContactContinuity.Curvature||Math.Abs(curvature)<=1e-7)};
    }
}
