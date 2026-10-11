using System.Collections.Immutable;

namespace Cadoryx.Db;

/// <summary>Distance drivers address an explicit author operation path. Linked source data and
/// frozen provenance are protected; expressions never enter the dense solver as extra variables.</summary>
public static class SketchExpressionParameters
{
    public static IEnumerable<ParameterTarget> All(CadSketch sketch)
    {
        var owned=sketch.RegionOffsets.SelectMany(g=>g.Loops).SelectMany(l=>l.Outputs).Select(o=>o.Curve).ToHashSet();
        foreach(var curve in sketch.ExpressionCurves.Where(e=>e.Source is not {IsFrozen:true}&&!owned.Contains(e.Id)).OrderBy(e=>e.Id.Value))
        {
            int depth=0;for(var node=curve.Geometry;node is not null&&(curve.Source is null||depth<curve.Source.BasisDepth);node=node.Basis,depth++)
                if(node.Kind==SketchCurveExpressionKind.Offset)yield return ParameterTarget.SketchCurve(sketch.Id,curve.Id,"OffsetDistance",depth);
        }
    }
    private static SketchCurveExpressionGeometry Node(SketchExpressionCurve curve,ParameterTarget target)
    {
        if(target.Property!="OffsetDistance"||target.Index is not {} depth||depth<0||depth>=SketchCurveExpressionGeometry.MaximumDepth||
            curve.Source is {IsFrozen:true}||curve.Source is {} source&&depth>=source.BasisDepth)
            throw new CadValidationException("Expression distance requires an explicit mutable author operation path.");
        var node=curve.Geometry;for(int i=0;i<depth;i++)node=node.Basis??throw new CadValidationException("Missing expression distance operation.");
        if(node.Kind!=SketchCurveExpressionKind.Offset)throw new CadValidationException("The selected expression operation is not an offset.");
        return node;
    }
    public static ParameterQuantity Read(SketchExpressionCurve curve,ParameterTarget target)=>
        ParameterQuantity.Of(Node(curve,target).SignedOffset,ParameterDimension.Length);
    public static CadSketch Stage(CadSketch sketch,SketchExpressionCurve curve,ParameterTarget target,double value)
    {
        var node=Node(curve,target);CadGuard.Finite(value);var path=new List<SketchCurveExpressionGeometry>();var root=curve.Geometry;
        for(int i=0;i<target.Index!.Value;i++){path.Add(root);root=root.Basis!;}
        var geometry=node with{SignedOffset=value};for(int i=path.Count-1;i>=0;i--)geometry=path[i] with{Basis=geometry};
        return sketch with{ExpressionCurves=sketch.ExpressionCurves.Replace(curve,curve with{Geometry=geometry})};
    }
    public static void GuardStructure(SketchExpressionCurve before,SketchExpressionCurve after,ParameterTarget target)
    {
        _=Node(before,target);_=Node(after,target);
        if(before.Source?.Entity!=after.Source?.Entity||before.Source?.Kind!=after.Source?.Kind||before.Source?.BasisDepth!=after.Source?.BasisDepth)
            throw new CadValidationException("Detach distance parameter targets before changing expression provenance.");
        var a=before.Geometry;var b=after.Geometry;
        while(true)
        {
            if(a.Kind!=b.Kind||a.Reversed!=b.Reversed||a.FirstParameter!=b.FirstParameter||a.LastParameter!=b.LastParameter||
                (a.Basis is null)!=(b.Basis is null))throw new CadValidationException("Detach distance parameter targets before changing author operation paths, native ranges or direction.");
            if(a.Basis is null)return;a=a.Basis;b=b.Basis!;
        }
    }
}
