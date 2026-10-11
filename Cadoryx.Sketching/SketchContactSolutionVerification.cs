using Cadoryx.Db;
using Cadoryx.Kernel.Abstractions;

namespace Cadoryx.Sketching;

/// <summary>Commands verify returned coordinates, including drivers, before publishing a contact-bearing solve.</summary>
public static class SketchContactSolutionVerification
{
    public static void Validate(CadSketch authored,CadSketch solved,SketchSolveOptions? options=null,SketchRegionEvaluationContext? regionContext=null)
    {
        bool curvature=authored.Constraints.OfType<EndpointCurvatureConstraint>().Any(c=>c.IsEnabled);
        if(!curvature&&!authored.Constraints.OfType<EndpointTangentConstraint>().Any(c=>c.IsEnabled)&&
           !authored.Constraints.OfType<CircularContactConstraint>().Any(c=>c.IsEnabled)&&!authored.Constraints.OfType<FiniteParametricContactConstraint>().Any(c=>c.IsEnabled)&&
           !authored.ExpressionCurves.Any(c=>c.Endpoints is not null))return;
        SketchFiniteParametricContact.RequireUnchangedAuthors(authored,solved);
        if(regionContext is not null)regionContext.ValidateSolved(authored,solved);
        options??=new();var system=new SketchEquationSystem(authored,options,regionContext);var x=system.Coordinates(solved);var residual=system.Residual(x);
        if(!system.Admissible(x)||residual.Any(r=>!double.IsFinite(r)||Math.Abs(r)>1))
        {
            var error=new CadValidationException(curvature?"SKETCH.CURVATURE_OUTPUT":"SKETCH.UNVERIFIED_CONTACT_SOLUTION");
            error.Data[curvature?"SketchCurve.Message":"SketchContact.Message"]=curvature?"CurvatureOutputFailure":"ContactBranchFailure";throw error;
        }
        SketchCircularContact.RequireSolvedContacts(solved,options.LinearToleranceMm);
        SketchAssociativeEndpoints.RequireSolved(solved,options.LinearToleranceMm);
        SketchEndpointCurvature.RequireSolvedRelations(solved,options.AngularTolerance,options.CurvatureTolerancePerMm);
        SketchFiniteParametricContact.RequireSolvedRelations(solved,options.LinearToleranceMm,options.AngularTolerance,options.CurvatureTolerancePerMm);
    }
}
