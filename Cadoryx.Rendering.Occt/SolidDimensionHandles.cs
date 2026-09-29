using Cadoryx.Db;

namespace Cadoryx.Rendering.Occt;

public sealed record SolidDimensionHandle(SolidDimension Dimension, Vector3d WorldPoint,
    Vector3d WorldAxis, double Value);

/// <summary>Placement-aware positions and screen-space axis projection for direct feature editing.</summary>
public static class SolidDimensionHandles
{
    public static IReadOnlyList<SolidDimensionHandle> Describe(GeometryRecipe recipe,RigidTransform3d occurrence)
    {
        occurrence.Validate();
        var placement=recipe switch
        {
            BoxRecipe b=>b.Placement,CylinderRecipe c=>c.Placement,ExtrudeRecipe e=>e.Placement,
            RevolveRecipe r=>r.Placement,
            _=>throw new NotSupportedException("This feature has no solid dimension handles.")
        };
        var world=occurrence*placement;
        SolidDimensionHandle At(SolidDimension dimension,Vector3d local,Vector3d axis,double value)=>
            new(dimension,world.Apply(local),world.Rotation.Rotate(axis),value);
        return recipe switch
        {
            BoxRecipe b=>[
                At(SolidDimension.BoxX,new(b.X,b.Y/2,b.Z/2),new(1,0,0),b.X),
                At(SolidDimension.BoxY,new(b.X/2,b.Y,b.Z/2),new(0,1,0),b.Y),
                At(SolidDimension.BoxZ,new(b.X/2,b.Y/2,b.Z),new(0,0,1),b.Z)],
            CylinderRecipe c=>[
                At(SolidDimension.CylinderRadius,new(c.Radius,0,c.Height/2),new(1,0,0),c.Radius),
                At(SolidDimension.CylinderHeight,new(0,0,c.Height),new(0,0,1),c.Height)],
            ExtrudeRecipe e=>[
                At(SolidDimension.ExtrudeDistance,new(0,0,e.Distance),new(0,0,1),e.Distance)],
            RevolveRecipe r=>RevolveHandles(r,At),
            _=>[]
        };
    }

    private static IReadOnlyList<SolidDimensionHandle> RevolveHandles(RevolveRecipe recipe,
        Func<SolidDimension,Vector3d,Vector3d,double,SolidDimensionHandle> at)
    {
        var radial=recipe.Profile.Points.OrderByDescending(p=>Math.Abs(p.X)).FirstOrDefault();
        double radius=Math.Abs(radial.X);
        if(radius<0.001)return [];
        double angle=recipe.AngleRadians;
        var point=new Vector3d(radial.X*Math.Cos(angle),radial.X*Math.Sin(angle),radial.Y);
        var tangent=new Vector3d(-radial.X*Math.Sin(angle),radial.X*Math.Cos(angle),0);
        return [at(SolidDimension.RevolveAngle,point,tangent,angle)];
    }

    public static double DragValue(double initial,int startX,int startY,int x,int y,
        (int X,int Y) handlePixel,(int X,int Y) axisPixel,double? snapSpacing=null)
    {
        double dx=axisPixel.X-handlePixel.X,dy=axisPixel.Y-handlePixel.Y;
        double length2=dx*dx+dy*dy;
        if(!double.IsFinite(initial)||initial<=0||length2<4||!double.IsFinite(length2))
            throw new ArgumentOutOfRangeException(nameof(axisPixel));
        double value=initial+((x-startX)*dx+(y-startY)*dy)/length2;
        if(snapSpacing is {} spacing)
        {
            if(!double.IsFinite(spacing)||spacing<=0)throw new ArgumentOutOfRangeException(nameof(snapSpacing));
            value=Math.Round(value/spacing)*spacing;
        }
        return Math.Clamp(value,0.001,1_000_000);
    }
}
