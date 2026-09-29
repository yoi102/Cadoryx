using Cadoryx.Db;

namespace Cadoryx.Rendering.Occt;

public static class OccurrenceGizmoMath
{
    /// <summary>Uniformly scales a primitive about its geometric center.</summary>
    public static GeometryRecipe ScalePrimitive(GeometryRecipe recipe,double factor)
    {
        if(!double.IsFinite(factor)||factor is < 0.01 or > 100)
            throw new CadValidationException("Scale must be between 0.01 and 100.");
        GeometryRecipe scaled=recipe switch
        {
            BoxRecipe box=>box with
            {
                X=box.X*factor,Y=box.Y*factor,Z=box.Z*factor,
                Placement=box.Placement with{Translation=box.Placement.Translation+
                    box.Placement.Rotation.Rotate(new Vector3d(box.X,box.Y,box.Z)*((1-factor)/2))}
            },
            CylinderRecipe cylinder=>cylinder with
            {
                Radius=cylinder.Radius*factor,Height=cylinder.Height*factor,
                Placement=cylinder.Placement with{Translation=cylinder.Placement.Translation+
                    cylinder.Placement.Rotation.Rotate(new Vector3d(0,0,cylinder.Height)*((1-factor)/2))}
            },
            _=>throw new CadValidationException("Scaling requires a Box or Cylinder feature.")
        };
        scaled.Validate();return scaled;
    }

    /// <summary>Reads a copied OCCT 3x4 transform; rejects shear and mirror transforms.</summary>
    public static (RigidTransform3d Rigid,double Scale) Decompose(double[] matrix)
    {
        if(matrix is not {Length:12}||matrix.Any(x=>!double.IsFinite(x)))
            throw new CadValidationException("Manipulator transform is invalid.");
        double scale=Math.Sqrt(matrix[0]*matrix[0]+matrix[4]*matrix[4]+matrix[8]*matrix[8]);
        if(scale is < 0.001 or > 1000)throw new CadValidationException("Manipulator scale is out of range.");
        double a=matrix[0]/scale,b=matrix[1]/scale,c=matrix[2]/scale;
        double d=matrix[4]/scale,e=matrix[5]/scale,f=matrix[6]/scale;
        double g=matrix[8]/scale,h=matrix[9]/scale,i=matrix[10]/scale;
        if(Math.Abs(a*a+d*d+g*g-1)>1e-3||Math.Abs(b*b+e*e+h*h-1)>1e-3||
            Math.Abs(c*c+f*f+i*i-1)>1e-3||Math.Abs(a*b+d*e+g*h)>1e-3||
            Math.Abs(a*c+d*f+g*i)>1e-3||Math.Abs(b*c+e*f+h*i)>1e-3||
            a*(e*i-f*h)-b*(d*i-f*g)+c*(d*h-e*g)<0.999)
            throw new CadValidationException("Manipulator produced a non-rigid or mirrored transform.");
        double x,y,z,w;
        double trace=a+e+i;
        if(trace>0)
        {
            double s=2*Math.Sqrt(trace+1);w=s/4;x=(h-f)/s;y=(c-g)/s;z=(d-b)/s;
        }
        else if(a>e&&a>i)
        {
            double s=2*Math.Sqrt(1+a-e-i);w=(h-f)/s;x=s/4;y=(b+d)/s;z=(c+g)/s;
        }
        else if(e>i)
        {
            double s=2*Math.Sqrt(1+e-a-i);w=(c-g)/s;x=(b+d)/s;y=s/4;z=(f+h)/s;
        }
        else
        {
            double s=2*Math.Sqrt(1+i-a-e);w=(d-b)/s;x=(c+g)/s;y=(f+h)/s;z=s/4;
        }
        double norm=Math.Sqrt(x*x+y*y+z*z+w*w);
        var rigid=new RigidTransform3d(new(matrix[3],matrix[7],matrix[11]),
            new(x/norm,y/norm,z/norm,w/norm));
        rigid.Validate();
        return (rigid,scale);
    }
}
