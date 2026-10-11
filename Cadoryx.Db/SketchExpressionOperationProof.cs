namespace Cadoryx.Db;

/// <summary>A declaration of exactly one new endpoint continuation in the author tree.
/// This is a value proof, not a trusted caller flag or a native topology mapping.</summary>
public static class SketchExpressionOperationProof
{
    public static bool IsSingleContinuation(SketchExpressionCurve before,SketchExpressionCurve after)
    {
        if(before.Id!=after.Id||before.IsConstruction!=after.IsConstruction||before.Source is not {} old||after.Source is not {} current||old.IsFrozen||current.IsFrozen||
            current.Entity!=old.Entity||current.Kind!=old.Kind||current.BasisDepth!=old.BasisDepth+1||
            current.BasisDepth>=SketchCurveExpressionGeometry.MaximumDepth||!current.Geometry.SameValues(old.Geometry))return false;
        var geometry=after.Geometry;
        return geometry.Kind==SketchCurveExpressionKind.Continuation&&geometry.Primitive is null&&geometry.SignedOffset==0&&
            geometry.Basis is {} basis&&basis.SameValues(before.Geometry)&&geometry.Reversed==before.Geometry.Reversed&&
            geometry.FirstParameter<=before.Geometry.FirstParameter&&geometry.LastParameter>=before.Geometry.LastParameter&&
            (geometry.FirstParameter<before.Geometry.FirstParameter||geometry.LastParameter>before.Geometry.LastParameter);
    }
    public static bool IsPeriodicComplement(SketchExpressionCurve before,SketchExpressionCurve after)
    {
        if(before.Id!=after.Id||before.IsConstruction!=after.IsConstruction||before.Source is not {} old||after.Source is not {} current||old.IsFrozen||current.IsFrozen||
            current.Entity!=old.Entity||current.Kind!=old.Kind||current.BasisDepth!=old.BasisDepth+1||
            current.BasisDepth>=SketchCurveExpressionGeometry.MaximumDepth||!current.Geometry.SameValues(old.Geometry)||
            SketchExpressionPeriod.FullPeriod(before.Geometry) is not {} period)return false;
        var g=after.Geometry;var b=before.Geometry;
        return g.Kind==SketchCurveExpressionKind.Continuation&&g.Primitive is null&&g.SignedOffset==0&&
            g.Basis is {} basis&&basis.SameValues(b)&&g.Reversed==b.Reversed&&g.ParameterSpan>0&&g.ParameterSpan<period&&
            (g.FirstParameter>=b.FirstParameter&&g.FirstParameter<b.LastParameter&&g.LastParameter>b.LastParameter&&g.LastParameter<=b.LastParameter+period||
             g.FirstParameter>=b.FirstParameter-period&&g.FirstParameter<b.FirstParameter&&g.LastParameter>b.FirstParameter&&g.LastParameter<=b.LastParameter);
    }
}
